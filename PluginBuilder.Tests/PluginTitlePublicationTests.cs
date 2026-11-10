using System.Data;
using Dapper;
using Npgsql;
using PluginBuilder.Builds.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class PluginTitlePublicationTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Slug = "publication-title";

    [Theory]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("\t \u00a0\u3000", true)]
    public async Task PublicationRequiresATitleAndPublishedTitlesCanBeRenamedButNotCleared(string? title, bool preRelease)
    {
        await using var tester = CreateMigrationTester("title-publication");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        var missingPlugin = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.SetVersionBuild(new FullBuildId(Slug, 0), PluginVersion.Parse("1.0.0"), null, null, true));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingPlugin.SqlState);
        await SeedBuild(conn, title);
        var id = new FullBuildId(Slug, 0);
        var version = PluginVersion.Parse("1.0.0");
        var error = await Assert.ThrowsAsync<BuildServiceException>(() => conn.SetVersionBuild(id, version, null, null, preRelease));
        Assert.Equal(PluginTitleValidationException.RequiredMessage, error.Message);
        Assert.Equal(0, await conn.QuerySingleAsync<int>("SELECT count(*) FROM versions"));

        // The policy is enforced even when application validation is bypassed.
        var directInsert = await Assert.ThrowsAsync<PostgresException>(() => InsertVersion(conn));
        Assert.Equal(PostgresErrorCodes.CheckViolation, directInsert.SqlState);
        Assert.Equal(PluginTitleValidationException.RequiredConstraint, directInsert.ConstraintName);

        await conn.SetPluginSettings(Slug, new PluginSettings { PluginTitle = "  Public title  " });
        Assert.True(await conn.SetVersionBuild(id, version, null, null, preRelease));
        await conn.SetPluginSettings(Slug, new PluginSettings { PluginTitle = "  Renamed title  " });
        var clear = await Assert.ThrowsAsync<PluginTitleValidationException>(() =>
            conn.SetPluginSettings(Slug, new PluginSettings { PluginTitle = title }));
        Assert.Equal(PluginTitleValidationException.RequiredMessage, clear.Message);
        Assert.Equal("Renamed title", (await conn.GetSettings(Slug))!.PluginTitle);
        var directClear = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("UPDATE plugins SET settings='{}' WHERE slug=@Slug", new { Slug }));
        Assert.Equal(PluginTitleValidationException.RequiredConstraint, directClear.ConstraintName);

        // A manifest's name is metadata, not another reservation in the title namespace.
        Assert.False(await conn.IsPluginTitleInUse("Manifest name"));
        await conn.ExecuteAsync("INSERT INTO plugins(slug,settings) VALUES('another-plugin','{\"pluginTitle\":\"Manifest name\"}')");
        Assert.Equal("Manifest name", await conn.QuerySingleAsync<string>("SELECT manifest_info->>'Name' FROM builds"));

        await conn.ExecuteAsync("DELETE FROM versions");
        Assert.True(await conn.SetPluginSettings(Slug, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\t \u00a0")]
    public async Task MigrationRejectsPublishedMissingTitlesWithoutCopyingManifestNames(string? title)
    {
        await using var tester = CreateMigrationTester("title-legacy");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await using var conn = await tester.Open();
        await SeedBuild(conn, title);
        await InsertVersion(conn);
        var error = await Assert.ThrowsAsync<PostgresException>(() => tester.RunRemainingScripts());
        Assert.Contains("without a title", error.MessageText);
        Assert.Contains(Slug, error.Detail);
        Assert.Equal(title, (await conn.GetSettings(Slug))!.PluginTitle);
        Assert.Equal("Manifest name", await conn.QuerySingleAsync<string>("SELECT manifest_info->>'Name' FROM builds"));
        Assert.Null(await conn.QuerySingleAsync<string?>("SELECT to_regclass('plugins_title_unique')::text"));

        await conn.SetPluginSettings(Slug, new PluginSettings { PluginTitle = "Manually chosen title" });
        await tester.RunRemainingScripts();
        Assert.Equal("Manually chosen title", (await conn.GetSettings(Slug))!.PluginTitle);
    }

    [Theory]
    [InlineData(true, IsolationLevel.ReadCommitted)]
    [InlineData(false, IsolationLevel.ReadCommitted)]
    [InlineData(true, IsolationLevel.RepeatableRead)]
    [InlineData(false, IsolationLevel.RepeatableRead)]
    public async Task ConcurrentPublicationAndClearingCannotCommitAPublishedMissingTitle(bool publicationFirst, IsolationLevel isolation)
    {
        await using var tester = CreateMigrationTester("title-publish-race");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await tester.RunRemainingScripts();
        await using var winner = await tester.Open();
        await using var loser = await tester.Open();
        await using var observer = await tester.Open();
        await SeedBuild(winner, "Current title");
        await using var winningTransaction = await winner.BeginTransactionAsync();
        await using var losingTransaction = await loser.BeginTransactionAsync(isolation);
        // Establish the snapshot before the other connection changes either table.
        Assert.Equal(0, await loser.QuerySingleAsync<int>("SELECT count(*) FROM versions"));
        var id = new FullBuildId(Slug, 0);
        var version = PluginVersion.Parse("1.0.0");
        if (publicationFirst)
            await winner.SetVersionBuild(id, version, null, null, true);
        else
            await winner.SetPluginSettings(Slug, null);
        var pending = publicationFirst
            ? loser.SetPluginSettings(Slug, null)
            : loser.SetVersionBuild(id, version, null, null, true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await observer.QuerySingleAsync<bool>("SELECT cardinality(pg_blocking_pids(@Pid)) > 0", new { Pid = loser.ProcessID }))
            await Task.Delay(10, timeout.Token);
        Assert.False(pending.IsCompleted);
        await winningTransaction.CommitAsync();
        var error = await Record.ExceptionAsync(() => pending);
        if (isolation == IsolationLevel.RepeatableRead)
            Assert.Equal(PostgresErrorCodes.SerializationFailure, Assert.IsType<PostgresException>(error).SqlState);
        else if (publicationFirst)
            Assert.IsType<PluginTitleValidationException>(error);
        else
            Assert.IsType<BuildServiceException>(error);
        await losingTransaction.RollbackAsync();

        Assert.Equal(publicationFirst ? 1 : 0, await observer.QuerySingleAsync<int>("SELECT count(*) FROM versions"));
        Assert.Equal(publicationFirst ? "Current title" : null, (await observer.GetSettings(Slug))!.PluginTitle);
        Assert.Equal(0, await observer.QuerySingleAsync<int>("""
            SELECT count(*) FROM versions v JOIN plugins p ON p.slug=v.plugin_slug
            WHERE plugin_title_key(p.settings->>'pluginTitle') IS NULL
            """));
    }

    private static Task SeedBuild(NpgsqlConnection conn, string? title) => conn.ExecuteAsync("""
        INSERT INTO plugins(slug,settings) VALUES (@Slug,jsonb_build_object('pluginTitle',@Title::text));
        INSERT INTO builds(plugin_slug,id,state,manifest_info)
        VALUES (@Slug,0,'uploaded','{"Name":"Manifest name","Version":"1.0.0"}');
        """, new { Slug, Title = title });

    private static Task InsertVersion(NpgsqlConnection conn) => conn.ExecuteAsync("""
        INSERT INTO versions(plugin_slug,ver,build_id,btcpay_min_ver,pre_release)
        VALUES(@Slug,ARRAY[1,0,0],0,ARRAY[0,0,0],true)
        """, new { Slug });
}
