using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Services;

public sealed class PluginScanTarget
{
    public long BuildId { get; set; }
    public string Repository { get; set; } = "";
    public string GitCommit { get; set; } = "";
    public string? PluginDirectory { get; set; }

    public string ToJson() => new JObject
    {
        ["buildId"] = BuildId, ["repository"] = Repository, ["gitCommit"] = GitCommit,
        ["pluginDirectory"] = PluginDirectory
    }.ToString(Formatting.None);
    public static PluginScanTarget? FromJson(string? json) => json is null ? null : JsonConvert.DeserializeObject<PluginScanTarget>(json);

    public bool IsValid()
    {
        try
        {
            if (BuildId < 0 || !BuildPolicy.IsGitObjectId(GitCommit)) return false;
            Repository = BuildPolicy.NormalizeRepositoryUrl(Repository);
            if (string.IsNullOrEmpty(PluginDirectory)) PluginDirectory = null;
            if (PluginDirectory is { } directory)
            {
                // Match Warden's relative-directory contract; null means the repository root.
                directory = directory.TrimEnd('/');
                if (directory.Length is 0 or > 1024 || directory.Any(c => char.IsControl(c) || c is '\\' or ':') ||
                    directory.Split('/').Any(part => part is "" or "." or ".." or ".git")) return false;
                PluginDirectory = directory;
            }
            return true;
        }
        catch (BuildServiceException) { return false; }
    }
}

public sealed class PluginScanService(DBConnectionFactory connections)
{
    public enum Outcome { Requested, NotFound, AlreadyProcessed, NoTarget }
    public sealed record Result(Outcome Outcome, Guid? ScanRequestId = null);
    public const string MissingTargetMessage = "A successful build with a resolved commit and a valid plugin directory is required to request a scan.";

    public static async Task<PluginScanTarget?> GetLatestTarget(NpgsqlConnection conn, string pluginSlug, CancellationToken cancellationToken = default)
    {
        var target = await conn.QuerySingleOrDefaultAsync<PluginScanTarget>(new CommandDefinition("""
            SELECT id AS BuildId, build_info->>'gitRepository' AS Repository,
                build_info->>'gitCommit' AS GitCommit, build_info->>'pluginDir' AS PluginDirectory
            FROM builds WHERE plugin_slug = @pluginSlug AND state = 'uploaded' ORDER BY id DESC LIMIT 1
            """, new { pluginSlug }, cancellationToken: cancellationToken));
        return target is not null && target.IsValid() ? target : null;
    }

    public async Task<Result> Request(string? pluginSlug, int? listingRequestId, string userId, Guid? tokenId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        PluginScanTarget? target = null;
        if (listingRequestId is { } requestId)
        {
            var row = await conn.QuerySingleOrDefaultAsync<ListingRow>(new CommandDefinition("""
                SELECT lr.plugin_slug AS PluginSlug, lr.status, lr.review_target::text AS Target
                FROM plugin_listing_requests lr JOIN plugins p ON p.slug = lr.plugin_slug
                WHERE lr.id = @requestId FOR UPDATE OF lr, p
                """, new { requestId }, tx, cancellationToken: cancellationToken));
            if (row is null) return new(Outcome.NotFound);
            if (row.Status != "pending") return new(Outcome.AlreadyProcessed);
            pluginSlug = row.PluginSlug;
            target = PluginScanTarget.FromJson(row.Target);
            if (target is null)
            {
                // An old pending request without a snapshot acquires one on its first manual scan.
                target = await GetLatestTarget(conn, pluginSlug, cancellationToken);
                if (target is not null)
                    await conn.ExecuteAsync(new CommandDefinition("UPDATE plugin_listing_requests SET review_target = @target::jsonb WHERE id = @requestId",
                        new { target = target.ToJson(), requestId }, tx, cancellationToken: cancellationToken));
            }
        }
        else
        {
            var slug = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT slug FROM plugins WHERE slug = @pluginSlug FOR SHARE", new { pluginSlug }, tx, cancellationToken: cancellationToken));
            if (slug is null) return new(Outcome.NotFound);
            target = await GetLatestTarget(conn, slug, cancellationToken);
        }
        if (target is null || !target.IsValid()) return new(Outcome.NoTarget);
        var id = await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            "SELECT emit_plugin_scan_request(@pluginSlug, @target::jsonb, @userId, @tokenId, @listingRequestId, 'manual')",
            new { pluginSlug, target = target.ToJson(), userId, tokenId, listingRequestId }, tx, cancellationToken: cancellationToken));
        await tx.CommitAsync(cancellationToken);
        return new(Outcome.Requested, id);
    }

    private sealed class ListingRow
    {
        public string PluginSlug { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Target { get; set; }
    }
}
