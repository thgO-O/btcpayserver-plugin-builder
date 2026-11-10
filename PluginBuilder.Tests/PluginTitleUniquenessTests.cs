using Dapper;
using Npgsql;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class PluginTitleUniquenessTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task MigrationNormalizesTitlesAndUsesTheSameKeyAsValidation()
    {
        await using var tester = CreateMigrationTester("title-normalization");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await using (var conn = await tester.Open())
        {
            await conn.ExecuteAsync("""
                INSERT INTO plugins (slug, settings) VALUES
                ('cafe', jsonb_build_object('pluginTitle', @Title::text, 'description', 'Keep this')),
                ('untitled', '{}'), ('null-title', '{"pluginTitle":null}');
                """, new { Title = "\t \u00a0Café  Payments\u3000\r\n" });
        }

        await tester.RunRemainingScripts();
        await using var migrated = await tester.Open();
        Assert.Equal("Café  Payments", await migrated.QuerySingleAsync<string>(
            "SELECT settings->>'pluginTitle' FROM plugins WHERE slug='cafe'"));
        Assert.Equal("Keep this", await migrated.QuerySingleAsync<string>(
            "SELECT settings->>'description' FROM plugins WHERE slug='cafe'"));
        Assert.True(await migrated.IsPluginTitleInUse("  CAFÉ  PAYMENTS\t"));
        Assert.False(await migrated.IsPluginTitleInUse("Cafe  Payments"));
        Assert.False(await migrated.IsPluginTitleInUse("Café Payments"));
        Assert.False(await migrated.IsPluginTitleInUse("Café  Payments", PluginSlug.Parse("cafe")));

        Assert.True(await migrated.SetPluginSettings("cafe", new PluginSettings { PluginTitle = "  CAFé  Payments " }));
        Assert.Equal("CAFé  Payments", (await migrated.GetSettings("cafe"))!.PluginTitle);
        var duplicate = await Assert.ThrowsAsync<PluginTitleValidationException>(() =>
            migrated.SetPluginSettings("untitled", new PluginSettings { PluginTitle = "\nCafé  Payments " }));
        Assert.Equal(PluginTitleValidationException.DuplicateMessage, duplicate.Message);
        Assert.Equal(PluginTitleValidationException.UniqueConstraint, Assert.IsType<PostgresException>(duplicate.InnerException).ConstraintName);
        Assert.Null((await migrated.GetSettings("untitled"))?.PluginTitle);

        var directWrite = await Assert.ThrowsAsync<PostgresException>(() => migrated.ExecuteAsync("""
            UPDATE plugins SET settings = '{"pluginTitle":"  café  Payments  "}' WHERE slug='null-title';
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, directWrite.SqlState);
        Assert.Equal(PluginTitleValidationException.UniqueConstraint, directWrite.ConstraintName);
    }

    [Fact]
    public async Task MigrationRejectsCollisionsWithoutChangingExistingTitles()
    {
        await using var tester = CreateMigrationTester("title-collisions");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await using (var conn = await tester.Open())
        {
            await conn.ExecuteAsync("""
                INSERT INTO plugins (slug, settings) VALUES
                ('first', '{"pluginTitle":"  Shared title "}'),
                ('second', '{"pluginTitle":"SHARED TITLE"}');
                """);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => tester.RunRemainingScripts());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(PluginTitleValidationException.UniqueConstraint, error.ConstraintName);
        await using var verify = await tester.Open();
        Assert.Equal("  Shared title ", await verify.QuerySingleAsync<string>(
            "SELECT settings->>'pluginTitle' FROM plugins WHERE slug='first'"));
        Assert.Equal("SHARED TITLE", (await verify.GetSettings("second"))!.PluginTitle);
        Assert.False(await verify.QuerySingleAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM migrations WHERE script_name='35.PluginTitleUniqueness')"));
        Assert.Null(await verify.QuerySingleAsync<string?>("SELECT to_regclass('plugins_title_unique')::text"));

        // Only an explicit maintainer decision resolves a collision; retrying then succeeds.
        await verify.ExecuteAsync("UPDATE plugins SET settings='{}' WHERE slug='second'");
        await tester.RunRemainingScripts();
        Assert.Equal("Shared title", (await verify.GetSettings("first"))!.PluginTitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentWritesRejectOneTitleAndRollBackTheLosingCreation(bool creating)
    {
        await using var tester = CreateMigrationTester("title-race");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await tester.RunRemainingScripts();
        await using var first = await tester.Open();
        await using var second = await tester.Open();
        await first.ExecuteAsync("""
            INSERT INTO plugins (slug, settings) VALUES
            ('winner', '{}'), ('loser', '{"pluginTitle":"Original title"}');
            """);
        await using var firstTransaction = await first.BeginTransactionAsync();
        await first.SetPluginSettings("winner", new PluginSettings { PluginTitle = "  Shared title  " });
        await using var secondTransaction = await second.BeginTransactionAsync();
        if (creating)
            await second.ExecuteAsync("INSERT INTO plugins (slug) VALUES ('new-plugin')");
        var pending = second.SetPluginSettings(creating ? "new-plugin" : "loser",
            new PluginSettings { PluginTitle = "\tSHARED TITLE\n" });

        // Observe the second PostgreSQL backend waiting on the first transaction's UNIQUE check.
        await using var observer = await tester.Open();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await observer.QuerySingleAsync<bool>(
                   "SELECT cardinality(pg_blocking_pids(@Pid)) > 0", new { Pid = second.ProcessID }))
            await Task.Delay(10, timeout.Token);
        Assert.False(pending.IsCompleted);
        await firstTransaction.CommitAsync();
        await Assert.ThrowsAsync<PluginTitleValidationException>(() => pending);
        await secondTransaction.RollbackAsync();

        Assert.Equal(1, await observer.QuerySingleAsync<int>(
            "SELECT count(*) FROM plugins WHERE plugin_title_key(settings->>'pluginTitle')=plugin_title_key('shared title')"));
        Assert.Equal("Original title", (await observer.GetSettings("loser"))!.PluginTitle);
        Assert.False(await observer.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM plugins WHERE slug='new-plugin')"));
    }

    [Fact]
    public async Task MigrationRejectsOversizedTitlesWithoutTruncatingThem()
    {
        await using var tester = CreateMigrationTester("title-too-long");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        var title = new string('é', 501);
        await using var conn = await tester.Open();
        await conn.ExecuteAsync("INSERT INTO plugins(slug, settings) VALUES('too-long', jsonb_build_object('pluginTitle', @Title::text))",
            new { Title = title });
        var error = await Assert.ThrowsAsync<PostgresException>(() => tester.RunRemainingScripts());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(PluginTitleValidationException.LengthConstraint, error.ConstraintName);
        Assert.Equal(title, (await conn.GetSettings("too-long"))!.PluginTitle);
        await conn.ExecuteAsync("UPDATE plugins SET settings=jsonb_build_object('pluginTitle', @Title::text) WHERE slug='too-long'",
            new { Title = new string('界', 500) });
        await tester.RunRemainingScripts();
        Assert.Equal(new string('界', 500), (await conn.GetSettings("too-long"))!.PluginTitle);
    }

    [Fact]
    public async Task MigrationAndItsMarkerRollBackTogetherWhenRecordingFails()
    {
        await using var tester = CreateMigrationTester("title-marker");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await using var conn = await tester.Open();
        await conn.ExecuteAsync("""
            INSERT INTO plugins(slug, settings) VALUES('unchanged', '{"pluginTitle":"  Original title  "}');
            INSERT INTO migrations VALUES('35.PluginTitleUniqueness');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => tester.RunRemainingScripts());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("  Original title  ", (await conn.GetSettings("unchanged"))!.PluginTitle);
        Assert.Null(await conn.QuerySingleAsync<string?>("SELECT to_regclass('plugins_title_unique')::text"));
        await conn.ExecuteAsync("DELETE FROM migrations WHERE script_name='35.PluginTitleUniqueness'");
        await tester.RunRemainingScripts();
        Assert.Equal("Original title", (await conn.GetSettings("unchanged"))!.PluginTitle);
    }
}
