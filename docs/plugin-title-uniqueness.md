# Plugin title uniqueness

Configured plugin titles are trimmed when settings are saved. Internal spaces,
accents and Unicode normalization forms are preserved. Null and blank titles do
not reserve a title for an unpublished plugin. Creation and primary-owner settings
require a nonblank title. Every published version, including a pre-release,
requires a configured nonblank title. The administrator can rename it, but cannot
clear it while any version remains published. Removing every published version
allows the title to be cleared again.

`plugin_title_key` defines title equality for both the application's validation
query and the PostgreSQL unique index. It trims the same whitespace characters as
`.NET string.Trim()` and lowercases with PostgreSQL's built-in `pg_c_utf8`
collation. This compares accented letters without removing their accents, even
when the database was initialized with the `C` locale. It requires PostgreSQL 17
or later, matching the project's development and production PostgreSQL images.
See [PostgreSQL's collation documentation](https://www.postgresql.org/docs/17/collation.html).

All plugin visibility states share this title namespace. Editing one's own title,
including changing its case, is allowed. The unique index arbitrates concurrent
claims; the losing request receives the same field validation message as an
ordinary duplicate. Creation inserts the plugin, ownership and settings in one
transaction, so rejected claims do not reserve slugs. First-build defaults update
only build fields and cannot overwrite a concurrently edited title.

Database triggers serialize publication with title edits on the plugin's row.
The publication trigger updates the row without changing its settings, so stale
repeatable-read snapshots cannot bypass the rule. Missing-title edits are field
validation errors; publication failures use the normal user-visible build error.

Titles are limited to 500 Unicode code points so UTF-8 keys fit the PostgreSQL
B-tree index. All three save flows check the trimmed title before uploading files;
the shared save helper and database enforce the limit too. Supplementary Unicode
characters count once, as in PostgreSQL `char_length`. Names are never truncated.
If a title race rejects a save after uploads, only files uploaded by that request
are cleaned up on a best-effort basis. Existing images remain in place, and the
form asks the user to select the new files again.

## Existing data and deployment

Migration `35.PluginTitleUniqueness` takes table locks on `plugins` and
`versions`, checks for published plugins without titles, and
trims existing configured titles. PostgreSQL validates the existing titles
when it adds the length constraint and builds the unique index. Each script
and its migration marker commit together in one transaction. Schedule the
migration when briefly blocking access to these tables is acceptable.

Before deployment, back up the database and run this read-only preflight on
PostgreSQL 17 or later:

```sql
WITH titles AS (
    SELECT p.slug,
           btrim(p.settings->>'pluginTitle',
                 U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000') AS title,
           EXISTS (SELECT 1 FROM versions v WHERE v.plugin_slug = p.slug) AS published
    FROM plugins p
)
SELECT 'duplicate' AS problem,
       lower(title COLLATE "pg_c_utf8") AS title_key,
       array_agg(slug ORDER BY slug) AS plugins
FROM titles
WHERE nullif(title, '') IS NOT NULL
GROUP BY lower(title COLLATE "pg_c_utf8")
HAVING count(*) > 1
UNION ALL
SELECT 'too long', title, ARRAY[slug]
FROM titles
WHERE char_length(title) > 500
UNION ALL
SELECT 'published without title', title, ARRAY[slug]
FROM titles
WHERE published AND nullif(title, '') IS NULL;
```

If it reports rows, a maintainer must decide distinct, valid titles before
deployment. PostgreSQL validates the data again when the migration runs, so a
write between preflight and deployment can still cause the migration to fail.

If the migration fails at startup, it rolls back all its changes, including
trimming and the migration marker. For `plugins_title_unique` or
`plugins_title_length` errors, run the query above to identify the affected
slugs. Resolve the titles explicitly using the previous application version or
a reviewed SQL update. Leave the migration marker absent and retry startup.

Rolling back application code alone leaves the unique index in place. An older
version can therefore receive database errors for duplicate or missing published
titles; resolve data and deployment issues rather than silently dropping the
protection. Backups are
needed to recover the exact original surrounding whitespace if the normalization
itself must be reversed.

## Manifest names

Public responses use `settings.pluginTitle` when present, otherwise the
`Name` of the selected published version's manifest. Version selection depends on
host compatibility, pre-release inclusion and whether all versions were requested.
The title index alone does not protect this fallback. The publication/title-edit
guards ensure published plugins retain configured titles, so their manifest names
cannot become public titles by clearing the configured title. The defensive
fallback remains unchanged. Historical or current manifest names are not reserved
as additional titles, and manifests are not rewritten by this migration.
