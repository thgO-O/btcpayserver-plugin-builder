using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Builds.Services;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PluginScanTests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Fact]
    public async Task AutomationIsOptInAndEditorChangesOnlyAffectFutureRequests()
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(server, conn);
        var slug = new PluginSlug("scan-flag");
        await conn.NewPlugin(slug, admin);
        var target = await AddBuild(conn, slug, 'a');
        Assert.Equal("false", await conn.SettingsGetAsync(SettingsKeys.AutomaticListingScansEnabled));
        Assert.False(server.GetService<AdminSettingsCache>().AutomaticListingScansEnabled);
        var first = await CreateRequest(conn, slug, admin, target);
        Assert.Empty(await ScanEvents(conn));
        var listing = JObject.Parse(await conn.QuerySingleAsync<string>("SELECT data::text FROM admin_events WHERE type = 'listing.requested'"));
        Assert.Equal(target.GitCommit, listing["gitCommit"]!.Value<string>());

        using var browser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(browser, "scan-admin@example.com", "123456");
        var editor = await browser.GetStringAsync("/admin/SettingsEditor");
        Assert.Contains("data-key=\"AutomaticListingScansEnabled\"", editor);
        using var saved = await browser.PostAsync("/admin/SettingsEditor", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["key"] = SettingsKeys.AutomaticListingScansEnabled, ["value"] = " \tTrUe\u00a0\r\n",
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(editor)
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("true", await conn.SettingsGetAsync(SettingsKeys.AutomaticListingScansEnabled));
        Assert.True(server.GetService<AdminSettingsCache>().AutomaticListingScansEnabled);
        using var adminApi = server.CreateHttpClient();
        adminApi.SetBasicAuth("scan-admin@example.com", "123456");
        var status = JObject.Parse(await adminApi.GetStringAsync("/api/v1/admin/status"));
        Assert.True(status["automaticListingScansEnabled"]!.Value<bool>());
        Assert.Empty(await ScanEvents(conn)); // Enabling does not replay the old pending request.
        var second = await CreateRequest(conn, slug, admin, target);
        var automatic = Assert.Single(await ScanEvents(conn));
        Assert.Equal("listing", automatic["source"]!.Value<string>());
        Assert.Equal(second, automatic["listingRequestId"]!.Value<int>());
        Assert.Equal(target.BuildId, automatic["buildId"]!.Value<long>());
        Assert.Equal("src/Plugin", automatic["pluginDirectory"]!.Value<string>());
        Assert.Equal("pending", await conn.QuerySingleAsync<string>("SELECT status FROM plugin_listing_requests WHERE id = @first", new { first }));

        foreach (var value in new[] { "false", " \nFaLsE\t", "invalid" })
        {
            await conn.SettingsSetAsync(SettingsKeys.AutomaticListingScansEnabled, value);
            Assert.Equal("false", await conn.SettingsGetAsync(SettingsKeys.AutomaticListingScansEnabled));
            await CreateRequest(conn, slug, admin, target);
            Assert.Single(await ScanEvents(conn));
        }
        await conn.SettingsDeleteAsync(SettingsKeys.AutomaticListingScansEnabled);
        await CreateRequest(conn, slug, admin, target);
        Assert.Single(await ScanEvents(conn));

        await conn.SettingsSetAsync(SettingsKeys.AutomaticListingScansEnabled, "true");
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await CreateRequest(conn, slug, admin, target);
            await tx.RollbackAsync();
        }
        Assert.Single(await ScanEvents(conn)); // No event survives a rolled-back submission.
    }

    [Fact]
    public async Task AdminTokenCanRequestStandaloneAndPinnedListingScansWithoutChangingDecisions()
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(server, conn);
        var owner = await server.CreateFakeUserAsync("scan-owner@example.com", "123456");
        var slug = new PluginSlug("scan-target");
        await conn.NewPlugin(slug, owner);
        var original = await AddBuild(conn, slug, 'a');
        var listingId = await CreateRequest(conn, slug, owner, original);
        var latest = await AddBuild(conn, slug, 'b');
        var path = $"/api/v1/admin/plugins/{slug}/scan";
        using var client = server.CreateHttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
        client.SetBasicAuth("scan-owner@example.com", "123456");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, null)).StatusCode);
        client.SetBasicAuth("scan-admin@example.com", "123456");
        using var issued = await client.PostAsJsonAsync("/api/v1/admin/access-tokens", new { name = "Scan integration", expiresInDays = 1 });
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var token = JObject.Parse(await issued.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["token"]!.Value<string>());
        using var standalone = await client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Accepted, standalone.StatusCode);
        var accepted = JObject.Parse(await standalone.Content.ReadAsStringAsync());
        var dispatched = Assert.Single(await ScanEvents(conn));
        Assert.Equal(accepted["scanRequestId"]!.Value<string>(), dispatched["scanRequestId"]!.Value<string>());
        Assert.Equal(latest.GitCommit, dispatched["gitCommit"]!.Value<string>());
        Assert.Equal(admin, dispatched["userId"]!.Value<string>());
        Assert.Equal(token["id"]!.Value<string>(), dispatched["tokenId"]!.Value<string>());
        Assert.Equal(JTokenType.Null, dispatched["listingRequestId"]!.Type);

        var listingPath = $"/api/v1/admin/listing-requests/{listingId}";
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(listingPath + "/scan", null)).StatusCode);
        var linked = (await ScanEvents(conn))[1];
        Assert.Equal(original.GitCommit, linked["gitCommit"]!.Value<string>());
        Assert.Equal(listingId, linked["listingRequestId"]!.Value<int>());
        var detail = JObject.Parse(await client.GetStringAsync(listingPath));
        Assert.Equal("pending", detail["status"]!.Value<string>());
        Assert.Equal(original.BuildId, detail["reviewTarget"]!["buildId"]!.Value<long>());
        Assert.Equal("unlisted", await conn.QuerySingleAsync<string>("SELECT visibility::text FROM plugins WHERE slug = @slug", new { slug = slug.ToString() }));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(listingPath + "/review", new { decision = "approve", note = "Reviewed source and metadata." })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(listingPath + "/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(listingPath + "/review", new { decision = "reject", note = "Late result" })).StatusCode);
        Assert.Equal(2, (await ScanEvents(conn)).Length);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/admin/plugins/missing/scan", null)).StatusCode);
        var empty = new PluginSlug("scan-empty");
        await conn.NewPlugin(empty, owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/v1/admin/plugins/{empty}/scan", null)).StatusCode);
    }

    [Fact]
    public async Task AScanWaitsForAConcurrentApprovalAndDoesNotEmitAfterIt()
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        await using var observer = await server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(server, conn);
        var slug = new PluginSlug("scan-approval-race");
        await conn.NewPlugin(slug, admin);
        var target = await AddBuild(conn, slug, 'a');
        var requestId = await CreateRequest(conn, slug, admin, target);
        await using var reviewConnections = ConnectionFactory(conn.ConnectionString, "scan-race-review");
        await using var scanConnections = ConnectionFactory(conn.ConnectionString, "scan-race-request");
        using var scope = server.WebApp.Services.CreateScope();
        var reviews = ActivatorUtilities.CreateInstance<ListingReviewService>(scope.ServiceProvider, reviewConnections);
        var scans = new PluginScanService(scanConnections);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var barrier = await conn.BeginTransactionAsync(timeout.Token);

        // Pause approval in its event trigger, after the pending row has been updated
        // but before commit. A scan without the row lock can still read the old status.
        await conn.ExecuteAsync(new CommandDefinition("SELECT singleton FROM admin_event_counter FOR UPDATE",
            transaction: barrier, cancellationToken: timeout.Token));
        var approval = reviews.Review(requestId, admin, true, "Approved", _ => null, timeout.Token);
        Task<PluginScanService.Result>? scan = null;
        try
        {
            await WaitForBlockedOperation(observer, "scan-race-review", approval, timeout.Token);
            scan = scans.Request(null, requestId, admin, null, timeout.Token);
            // Both implementations reach a lock wait: the correct scan waits for
            // the listing row; a stale read instead waits to append its scan event.
            await WaitForBlockedOperation(observer, "scan-race-request", scan, timeout.Token);
            await barrier.CommitAsync(timeout.Token);

            Assert.Equal(ListingReviewService.Outcome.Completed, await approval.WaitAsync(timeout.Token));
            Assert.Equal(PluginScanService.Outcome.AlreadyProcessed, (await scan.WaitAsync(timeout.Token)).Outcome);
            Assert.Empty(await ScanEvents(conn));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM admin_events WHERE type = 'listing.approved' AND data->>'listingRequestId' = @id",
                new { id = requestId.ToString() }));
            Assert.Equal(PluginListingRequestStatus.Approved, (await conn.GetListingRequest(requestId))!.Status);
        }
        finally
        {
            timeout.Cancel();
            await barrier.DisposeAsync();
            try
            {
                await approval.WaitAsync(TimeSpan.FromSeconds(10));
                if (scan is not null) await scan.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Release and observe blocked operations without masking an assertion.
            }
        }
    }

    [Fact]
    public async Task AnInvalidLatestUploadedBuildDoesNotFallBackToAnOlderValidBuild()
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(server, conn);
        var slug = new PluginSlug("scan-invalid-latest");
        await conn.NewPlugin(slug, admin);
        await AddBuild(conn, slug, 'a');
        var listingId = await conn.CreateListingRequest(slug, "Release", "Proof", "Reviews", null, admin);

        var invalid = new FullBuildId(slug, await conn.NewBuild(slug,
            new PluginBuildParameters("https://github.com/example/plugin") { PluginDirectory = "src//Plugin" }));
        await conn.UpdateBuild(invalid, BuildStates.Uploaded, new JObject { ["gitCommit"] = new string('b', 40) });

        using var client = server.CreateHttpClient();
        client.SetBasicAuth("scan-admin@example.com", "123456");
        var pluginPath = $"/api/v1/admin/plugins/{slug}/scan";
        var listingPath = $"/api/v1/admin/listing-requests/{listingId}/scan";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync(pluginPath, null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync(listingPath, null)).StatusCode);
        Assert.Empty(await ScanEvents(conn));
        var listing = (await conn.GetListingRequest(listingId))!;
        Assert.Null(listing.ReviewTarget);
        Assert.Equal(PluginListingRequestStatus.Pending, listing.Status);

        var corrected = await AddBuild(conn, slug, 'c');
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(pluginPath, null)).StatusCode);
        var standalone = Assert.Single(await ScanEvents(conn));
        Assert.Equal(corrected.BuildId, standalone["buildId"]!.Value<long>());
        Assert.Equal(corrected.GitCommit, standalone["gitCommit"]!.Value<string>());
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(listingPath, null)).StatusCode);
        var events = await ScanEvents(conn);
        Assert.Equal(2, events.Length);
        Assert.Equal(corrected.BuildId, events[1]["buildId"]!.Value<long>());
        Assert.Equal(corrected.GitCommit, events[1]["gitCommit"]!.Value<string>());
        Assert.Equal(corrected.BuildId, (await conn.GetListingRequest(listingId))!.ReviewTarget!.BuildId);
    }

    [Fact]
    public async Task BrowserScanRequiresAntiforgeryAndLegacyListingAcquiresSnapshotOnce()
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(server, conn);
        var slug = new PluginSlug("scan-browser");
        await conn.NewPlugin(slug, admin);
        var request = await conn.CreateListingRequest(slug, "Release", "Proof", "Reviews", null, admin);
        var target = await AddBuild(conn, slug, 'c', null);
        using var browser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(browser, "scan-admin@example.com", "123456");
        var page = await browser.GetStringAsync($"/admin/listing-requests/{request}");
        Assert.Contains("RequestListingScan", page);
        var path = $"/admin/listing-requests/{request}/scan";
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync(path, new FormUrlEncodedContent([]))).StatusCode);
        Assert.Empty(await ScanEvents(conn));
        using var response = await browser.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = await conn.GetListingRequest(request);
        Assert.Equal(target.GitCommit, saved!.ReviewTarget!.GitCommit);
        var scan = Assert.Single(await ScanEvents(conn));
        Assert.Equal(JTokenType.Null, scan["pluginDirectory"]!.Type);
        Assert.Equal("manual", scan["source"]!.Value<string>());
        Assert.Equal(PluginListingRequestStatus.Pending, saved.Status);
        await AddBuild(conn, slug, 'd');
        Assert.Equal(target.GitCommit, (await conn.GetListingRequest(request))!.ReviewTarget!.GitCommit);
    }

    [Fact]
    public async Task MigrationPreservesDecisionsAndLegacyTargetsAreValidatedOnFirstManualScan()
    {
        await using var migration = CreateMigrationTester();
        await migration.RunScriptsUntil("30.PluginScanRequests");
        await using var conn = await migration.Open();
        await conn.ExecuteAsync("""
            INSERT INTO plugins(slug) VALUES ('scan-migration'), ('scan-no-build'), ('scan-legacy-directory');
            INSERT INTO builds_ids VALUES ('scan-migration', 0), ('scan-legacy-directory', 0);
            INSERT INTO builds(plugin_slug, id, state, build_info)
            VALUES ('scan-migration', 0, 'uploaded', @source::jsonb),
                   ('scan-legacy-directory', 0, 'uploaded', @source::jsonb || '{"pluginDir":"src//Plugin"}'::jsonb);
            INSERT INTO plugin_listing_requests(plugin_slug, release_note, telegram_verification_message, user_reviews, status)
            VALUES ('scan-migration', 'Release', 'Proof', 'Reviews', 'pending'),
                   ('scan-migration', 'Release', 'Proof', 'Reviews', 'approved'),
                   ('scan-migration', 'Release', 'Proof', 'Reviews', 'rejected'),
                   ('scan-no-build', 'Release', 'Proof', 'Reviews', 'pending'),
                   ('scan-legacy-directory', 'Release', 'Proof', 'Reviews', 'pending');
            """, new { source = new JObject { ["gitRepository"] = "https://github.com/example/plugin", ["gitCommit"] = new string('a', 40) }.ToString() });
        var before = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events");
        await migration.RunRemainingScripts();
        Assert.Equal(before, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events"));
        Assert.Equal("false", await conn.SettingsGetAsync(SettingsKeys.AutomaticListingScansEnabled));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM plugin_listing_requests WHERE review_target IS NOT NULL"));
        Assert.Equal(new[] { "approved", "pending", "rejected" },
            (await conn.QueryAsync<string>("SELECT status FROM plugin_listing_requests WHERE plugin_slug = 'scan-migration' ORDER BY status")).ToArray());

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["POSTGRES"] = conn.ConnectionString
        }).Build();
        await using var factory = new DBConnectionFactory(config);
        var scans = new PluginScanService(factory);
        var rootRequest = (await conn.GetPendingListingRequestForPlugin("scan-migration"))!;
        Assert.Equal(PluginScanService.Outcome.Requested, (await scans.Request(null, rootRequest.Id, "admin", null)).Outcome);
        Assert.Equal(0, (await conn.GetListingRequest(rootRequest.Id))!.ReviewTarget!.BuildId);
        var rootScan = Assert.Single(await ScanEvents(conn));
        Assert.Equal(0, rootScan["buildId"]!.Value<long>());
        Assert.Equal(JTokenType.Null, rootScan["pluginDirectory"]!.Type);

        // The old build pipeline accepts this path, but Warden does not.
        BuildPolicy.ValidateBuildInputs("main", "src//Plugin", "Release");
        var legacy = (await conn.GetPendingListingRequestForPlugin("scan-legacy-directory"))!;
        Assert.Equal(PluginScanService.Outcome.NoTarget, (await scans.Request(null, legacy.Id, "admin", null)).Outcome);
        Assert.Null((await conn.GetListingRequest(legacy.Id))!.ReviewTarget);
        Assert.Single(await ScanEvents(conn));

        var target = await AddBuild(conn, new PluginSlug(legacy.PluginSlug), 'b');
        Assert.Equal(PluginScanService.Outcome.Requested, (await scans.Request(null, legacy.Id, "admin", null)).Outcome);
        Assert.Equal(target.BuildId, (await conn.GetListingRequest(legacy.Id))!.ReviewTarget!.BuildId);
        Assert.Equal(target.GitCommit, (await ScanEvents(conn))[1]["gitCommit"]!.Value<string>());
        await AddBuild(conn, new PluginSlug(legacy.PluginSlug), 'c');
        Assert.Equal(PluginScanService.Outcome.Requested, (await scans.Request(null, legacy.Id, "admin", null)).Outcome);
        Assert.Equal(target.GitCommit, (await ScanEvents(conn))[2]["gitCommit"]!.Value<string>());
        Assert.Equal(PluginListingRequestStatus.Pending, (await conn.GetListingRequest(legacy.Id))!.Status);
    }

    [Fact]
    public async Task NewListingSubmissionRequiresSuccessfulBuildAndPinsItThroughTheOwnerForm()
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(server, conn);
        var slug = new PluginSlug("scan-owner-submit");
        await conn.NewPlugin(slug, admin);
        await conn.SetPluginSettings(slug, new PluginSettings
        {
            PluginTitle = "Scan plugin", Description = "Example", GitRepository = "https://github.com/example/plugin",
            Documentation = "https://example.com/docs", VideoUrl = "https://youtu.be/abc", Logo = "https://example.com/logo.png"
        });
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "GithubGistUrl" = 'https://gist.github.com/example/proof',
                "AccountDetail" = '{"nostr":{"npub":"npub1example"}}'::jsonb WHERE "Id" = @admin
            """, new { admin });
        using var browser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(browser, "scan-admin@example.com", "123456");
        var path = $"/plugins/{slug}/request-listing";
        var page = await browser.GetStringAsync(path);
        Assert.Contains("A successful build", page);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ReleaseNote"] = "Release", ["UserReviews"] = "Reviewed", ["TelegramVerificationMessage"] = "https://t.me/btcpayserver/123",
            ["HasSuccessfulBuild"] = "true", ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        });
        using var missing = await browser.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Contains("A successful build", await missing.Content.ReadAsStringAsync());
        Assert.False(await conn.HasPendingListingRequest(slug));
        var target = await AddBuild(conn, slug, 'e');
        using var accepted = await browser.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var request = await conn.GetPendingListingRequestForPlugin(slug);
        Assert.NotNull(request);
        Assert.Equal(target.BuildId, request.ReviewTarget!.BuildId);
        Assert.Equal(target.GitCommit, request.ReviewTarget.GitCommit);
        Assert.Empty(await ScanEvents(conn));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Plugins/One/", true)]
    [InlineData("../Other", false)]
    [InlineData("/absolute", false)]
    [InlineData("src//Plugin", false)]
    [InlineData(".git", false)]
    [InlineData("C:/Plugin", false)]
    public void TargetsUseWardensRelativeDirectoryContract(string? directory, bool valid)
    {
        var target = new PluginScanTarget { BuildId = 1, Repository = "https://github.com/example/plugin", GitCommit = new string('a', 40), PluginDirectory = directory };
        Assert.Equal(valid, target.IsValid());
    }

    [Fact]
    [Trait("Category", "PlaywrightUITest")]
    public async Task AdminScanControlsAndAutomationToggleWorkInTheBrowser()
    {
        await using var ui = new PlaywrightTester(Log, NewServer());
        await ui.StartAsync();
        await using var conn = await ui.Server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(ui.Server, conn);
        var slug = new PluginSlug("scan-ui");
        await conn.NewPlugin(slug, admin);
        var target = await AddBuild(conn, slug, 'a');
        var request = await CreateRequest(conn, slug, admin, target);
        await ui.LogIn("scan-admin@example.com");
        await ui.GoToUrl($"/admin/plugins/edit/{slug}");
        var page = ui.Page!;
        await Expect(page.Locator("#RequestPluginScan")).ToBeEnabledAsync();
        await page.Locator("#RequestPluginScan").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Scan requested");
        Assert.Single(await ScanEvents(conn));

        await ui.GoToUrl($"/admin/listing-requests/{request}");
        await Expect(page.Locator("#RequestListingScan")).ToBeEnabledAsync();
        await page.Locator("#RequestListingScan").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Scan requested");
        var events = await ScanEvents(conn);
        Assert.Equal(2, events.Length);
        Assert.Equal(request, events[1]["listingRequestId"]!.Value<int>());
        Assert.Equal("pending", await conn.QuerySingleAsync<string>("SELECT status FROM plugin_listing_requests WHERE id = @request", new { request }));

        await ui.GoToUrl("/admin/SettingsEditor");
        var row = page.Locator("tr[data-key='AutomaticListingScansEnabled']");
        await Expect(row.Locator(".value")).ToHaveTextAsync("false");
        await row.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await row.Locator("textarea").FillAsync("true\n");
        await row.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(row.Locator(".value")).ToHaveTextAsync("true");
        Assert.Equal("true", await conn.SettingsGetAsync(SettingsKeys.AutomaticListingScansEnabled));
        Assert.Equal(2, (await ScanEvents(conn)).Length);
        var next = await CreateRequest(conn, slug, admin, target);
        var automatic = (await ScanEvents(conn))[2];
        Assert.Equal(next, automatic["listingRequestId"]!.Value<int>());
        Assert.Equal("listing", automatic["source"]!.Value<string>());

        await row.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await row.Locator("textarea").FillAsync("false\n");
        await row.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(row.Locator(".value")).ToHaveTextAsync("false");
        Assert.Equal("false", await conn.SettingsGetAsync(SettingsKeys.AutomaticListingScansEnabled));
        await CreateRequest(conn, slug, admin, target);
        Assert.Equal(3, (await ScanEvents(conn)).Length);
    }

    [Fact]
    [Trait("Category", "PlaywrightUITest")]
    public async Task OwnerCannotSubmitInTheBrowserUntilASuccessfulBuildIsAvailable()
    {
        await using var ui = new PlaywrightTester(Log, NewServer());
        await ui.StartAsync();
        await using var conn = await ui.Server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(ui.Server, conn);
        await ui.VerifyUserAccounts("scan-admin@example.com");
        var slug = new PluginSlug("scan-submit-ui");
        await conn.NewPlugin(slug, admin);
        await conn.SetPluginSettings(slug, new PluginSettings
        {
            PluginTitle = "Scan plugin", Description = "Example", GitRepository = "https://github.com/example/plugin",
            Documentation = "https://example.com/docs", VideoUrl = "https://youtu.be/abc", Logo = "https://example.com/logo.png"
        });
        await ui.LogIn("scan-admin@example.com");
        await ui.GoToUrl($"/plugins/{slug}/request-listing");
        var page = ui.Page!;
        await Expect(page.Locator("#plugin-settings-status")).ToHaveTextAsync("Complete");
        await Expect(page.Locator("#owner-settings-status")).ToHaveTextAsync("Complete");
        await FillForm();
        await Expect(page.Locator("#request-form-status")).ToHaveTextAsync("Complete");
        await Expect(page.Locator("#SubmitListing")).ToBeDisabledAsync();
        Assert.False(await conn.HasPendingListingRequest(slug));

        var target = await AddBuild(conn, slug, 'f');
        await page.ReloadAsync();
        await FillForm();
        await Expect(page.Locator("#SubmitListing")).ToBeEnabledAsync();
        await page.Locator("#SubmitListing").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Your listing request has been sent");
        var request = await conn.GetPendingListingRequestForPlugin(slug);
        Assert.NotNull(request);
        Assert.Equal(target.BuildId, request.ReviewTarget!.BuildId);
        Assert.Empty(await ScanEvents(conn));

        async Task FillForm()
        {
            await page.Locator("#ReleaseNote").FillAsync("Release");
            await page.Locator("#TelegramVerificationMessage").FillAsync("https://t.me/btcpayserver/123");
            await page.Locator("#UserReviews").FillAsync("Reviewed at https://example.com/review");
        }
    }

    private static DBConnectionFactory ConnectionFactory(string connectionString, string applicationName)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = applicationName };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["POSTGRES"] = settings.ToString()
        }).Build();
        return new DBConnectionFactory(config);
    }

    private static async Task WaitForBlockedOperation(NpgsqlConnection observer, string applicationName, Task pending, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                   SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                       WHERE datname = current_database() AND application_name = @applicationName
                         AND wait_event_type = 'Lock' AND cardinality(pg_blocking_pids(pid)) > 0)
                   """, new { applicationName }, cancellationToken: timeout.Token)))
        {
            if (pending.IsCompleted) await pending;
            Assert.False(pending.IsCompleted, "The operation must reach the competing transaction before the barrier is released.");
            await Task.Delay(10, timeout.Token);
        }
    }

    private async Task<ServerTester> StartServer()
    {
        var server = NewServer();
        await server.Start();
        return server;
    }

    private ServerTester NewServer()
    {
        var server = Create("PluginScans");
        server.ReuseDatabase = false;
        server.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(descriptor);
        };
        return server;
    }

    private static async Task<string> CreateAdmin(ServerTester server, NpgsqlConnection conn)
    {
        var id = await server.CreateFakeUserAsync("scan-admin@example.com", "123456");
        await conn.ExecuteAsync("""INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") SELECT @id, "Id" FROM "AspNetRoles" WHERE "NormalizedName" = 'SERVERADMIN'""", new { id });
        return id;
    }

    private static async Task<PluginScanTarget> AddBuild(NpgsqlConnection conn, PluginSlug slug, char commit, string? directory = "src/Plugin")
    {
        var id = new FullBuildId(slug, await conn.NewBuild(slug, new PluginBuildParameters("https://github.com/example/plugin") { PluginDirectory = directory }));
        await conn.UpdateBuild(id, BuildStates.Uploaded, new JObject { ["gitCommit"] = new string(commit, 40) });
        return (await PluginScanService.GetLatestTarget(conn, slug.ToString()))!;
    }

    private static Task<int> CreateRequest(NpgsqlConnection conn, PluginSlug slug, string actor, PluginScanTarget target) =>
        conn.CreateListingRequest(slug, "Release", "Proof", "Reviews", null, actor, target);

    private static async Task<JObject[]> ScanEvents(NpgsqlConnection conn) =>
        (await conn.QueryAsync<string>("SELECT data::text FROM admin_events WHERE type = 'plugin.scan_requested' ORDER BY id")).Select(JObject.Parse).ToArray();
}
