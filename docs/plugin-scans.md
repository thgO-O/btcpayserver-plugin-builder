# External plugin scans

Plugin Builder requests scans through its durable admin event stream. An external
coordinator starts and follows the scan in Warden, then can submit the ordinary
listing approval or rejection with a review note. Plugin Builder stores the source
reference and decision, not scan progress, Warden run IDs, findings, or reports.
Version releases are unchanged.

## Start manually, enable automation later

`AutomaticListingScansEnabled` starts as `false`. In **Admin → Settings Editor**,
edit it to `true` to request a scan for each **new** listing submission. Changes
apply to subsequent submissions without restarting. Missing or invalid values
disable automation. Values saved in the editor are normalized to `true` or `false`,
including pasted values with surrounding whitespace. Enabling the setting does not replay existing requests;
disabling it does not cancel events already queued or scans already running.

Admins can always use **Request scan** on a pending listing's detail page or the
plugin's admin edit page. The listing button uses the source fixed in that request;
the plugin button uses its most recent successful build and creates a standalone
scan. Neither action changes listing decisions or plugin visibility. Migration
leaves existing listings without a source snapshot. An old pending listing acquires
one on its first manual scan, after the source has passed the same validation as a
new submission. An incompatible historical build does not prevent a later valid
build from being selected before that first scan; once captured, the target stays fixed.

New listing submissions require a successful build with an HTTPS GitHub/GitLab
repository and a resolved Git commit. The highest-ID `uploaded` build is selected.
The repository, full commit, and relative plugin directory are captured from build
metadata, independently of later settings changes or builds. An unusable latest
build requires correction; an older build is not silently selected instead.

## API and event contract

The following endpoints use the existing ServerAdmin Basic/admin-token authentication:

| Method and path | Purpose |
| --- | --- |
| `POST /api/v1/admin/plugins/{slug}/scan` | Request a standalone scan of the latest successful build. |
| `POST /api/v1/admin/listing-requests/{id}/scan` | Request a scan using a pending listing's pinned source. |

No body is required. Success is `202` with `{scanRequestId,message}`: it confirms
the request was recorded, not that a scanner is configured or has started.
Missing objects return `404`, closed listings return `409`, and an unavailable or
invalid source returns `422`. Repeated deliberate requests create distinct scans.
The UI uses the same service and requires antiforgery protection.

Both manual and automatic requests emit `plugin.scan_requested` in the normal
schema-version-1 envelope. Example `data`:

```json
{
  "scanRequestId": "a0a37831-8c61-4532-b09d-7d6f3ef05d44",
  "pluginSlug": "example",
  "listingRequestId": 123,
  "buildId": 7,
  "repository": "https://github.com/example/plugin",
  "gitCommit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "pluginDirectory": "Plugins/Example",
  "userId": "requesting-account",
  "tokenId": null,
  "source": "manual"
}
```

`source` is `manual` or `listing` (automatic). `listingRequestId` is null for a
standalone request. `pluginDirectory` is null for the repository root. `tokenId`
identifies a delegated admin token when used. IDs and source data come from the
server, not a caller-supplied request body. Events are committed with the action;
a rolled-back listing submission does not leave a scan event behind.

`listing.requested` continues to be emitted regardless of the flag and now includes
the pinned source fields. Admin listing reads expose that source as `reviewTarget`.
**Only `plugin.scan_requested` instructs the coordinator to start a scan.** Starting
scans from `listing.requested` would bypass the feature flag.

## Warden coordinator

Use Warden's existing MCP endpoint `/api/hooks/mcp` with a `run:plugins` token;
that scope also permits reading runs and findings. Prepare its `plugins` project
using the BTCPay pack or owner configuration. Keep Warden and any Cloudflare Access
service credentials in the coordinator, separate from Plugin Builder's admin token.

1. Poll the Builder admin event stream, filter `plugin.scan_requested`, and persist
   the cursor and dispatch records externally. If polling multiple event types,
   filter one unfiltered stream locally or maintain separate cursors per filter.
2. Call Warden MCP `start_scan` with `project: "plugins"`, `repo: data.repository`,
   `ref: data.gitCommit`, `verify: true`, and `full: true`. Set `directory` from
   `data.pluginDirectory` only when nonempty. `full` avoids Warden's automatic
   incremental baseline for repeat repository/directory scans. Model and lenses
   come from Warden's saved settings.
3. Keep the Builder instance/event ID, scan-request ID, and returned Warden run ID
   in the coordinator. Warden's `start_scan` is not idempotent and does not accept
   arbitrary correlation fields. Reuse known dispatch records for redeliveries;
   reconcile an ambiguous start response before creating another run.
4. Follow `get_run`, verify the audited commit, and read the associated findings
   through Warden. A completed run does not itself approve a listing. A failed,
   canceled, or interrupted run leaves the listing pending; recovery stays external.
5. For a linked listing, apply the operator's review policy and submit only
   `decision` (`approve`/`reject`) and `note` to the existing listing review API.
   Do not embed findings or reports in the decision message. A manual scan is not
   blanket authorization to approve listings or change visibility. Standalone scans
   finish outside the Builder. An already-decided listing cannot be overwritten.

Warden's `/api/hooks/plugin-builder` currently accepts only `build.succeeded` and
acknowledges other event types without starting a scan. Point this flow at the
coordinator, not that endpoint. Existing build-success subscriptions are independent
and may generate additional scans; this feature does not change them.
