# Plugin title uniqueness

Configured titles are trimmed when settings are saved. Internal spaces, accents
and Unicode normalization forms are preserved. Application validation and the
PostgreSQL unique index use the same `plugin_title_key` function, which trims
.NET whitespace and lowercases with `pg_c_utf8`. This requires PostgreSQL 17
or later, matching the project's PostgreSQL image.

Configured titles share one namespace across visibility states. Editing one's own
title, including changing its case, is allowed. Null and blank titles have no key.
Legacy plugins can continue using the selected published manifest's `Name` as a
public fallback. Manifest names can coincide with configured titles because the
index covers only `settings.pluginTitle`.

Creation and owner/admin title edits require a nonblank title. These forms check
title length before uploading files. Titles are limited to 500 Unicode code points
so UTF-8 keys fit the B-tree index. PostgreSQL arbitrates concurrent title claims,
and the losing request receives a field validation error. Rejected creation rolls
back the slug and ownership. A rejected edit preserves existing media and pending
gallery changes, cleans up uploads from that request on a best-effort basis, and
asks the user to select new files again.

The first build updates only its four build-default fields in settings, preserving
a concurrent edit to the title or description.

Migration `35.PluginTitleUniqueness` trims existing configured titles, adds the
length constraint and builds the unique index. PostgreSQL validates existing data
through those constraints and takes the required table locks. The script and
migration marker run in one Npgsql command, so failure rolls back both, including
the trimming. Resolve any conflicting or oversized titles manually and retry
startup with the migration marker absent.

Schedule deployment when briefly blocking access to the plugins table is
acceptable. Reverting application code leaves the database constraints in place.
