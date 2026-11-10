LOCK TABLE plugins, versions IN EXCLUSIVE MODE;

-- The same Unicode whitespace characters as string.Trim(); internal spaces are preserved.
CREATE FUNCTION normalize_plugin_title(title text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    RETURN btrim(title, U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000');

CREATE FUNCTION plugin_title_key(title text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    RETURN lower(nullif(normalize_plugin_title(title), '') COLLATE "pg_c_utf8");

DO $$
DECLARE
    missing_published text;
BEGIN
    SELECT string_agg(p.slug, ', ' ORDER BY p.slug) INTO missing_published
    FROM plugins p
    WHERE plugin_title_key(p.settings->>'pluginTitle') IS NULL
      AND EXISTS (SELECT 1 FROM versions v WHERE v.plugin_slug = p.slug);
    IF missing_published IS NOT NULL THEN
        RAISE EXCEPTION 'Published plugins without a title must be resolved before migration'
            USING DETAIL = missing_published,
                  HINT = 'Set these titles manually, then restart the migration. Manifest names will not be copied automatically.';
    END IF;
END;
$$;

UPDATE plugins
SET settings = jsonb_set(settings, '{pluginTitle}', to_jsonb(normalize_plugin_title(settings->>'pluginTitle')))
WHERE jsonb_typeof(settings->'pluginTitle') = 'string';

ALTER TABLE plugins ADD CONSTRAINT plugins_title_length
    CHECK (char_length(settings->>'pluginTitle') <= 500);

CREATE UNIQUE INDEX plugins_title_unique ON plugins (plugin_title_key(settings->>'pluginTitle'));

CREATE FUNCTION require_published_plugin_title() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF plugin_title_key(NEW.settings->>'pluginTitle') IS NULL
       AND EXISTS (SELECT 1 FROM versions WHERE plugin_slug = NEW.slug) THEN
        RAISE check_violation USING CONSTRAINT = 'plugins_published_title_required',
            MESSAGE = 'Published plugins must have a title. Enter a title in plugin settings.';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER plugins_require_published_title BEFORE UPDATE OF settings ON plugins
    FOR EACH ROW EXECUTE FUNCTION require_published_plugin_title();

CREATE FUNCTION require_version_plugin_title() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    title text;
BEGIN
    -- Serialize publication with title edits. Updating also prevents a stale
    -- REPEATABLE READ snapshot from clearing the title after publication.
    UPDATE plugins SET settings = settings WHERE slug = NEW.plugin_slug
        RETURNING settings->>'pluginTitle' INTO title;
    IF NOT FOUND THEN
        RAISE foreign_key_violation USING MESSAGE = 'The plugin for this version does not exist.';
    END IF;
    IF plugin_title_key(title) IS NULL THEN
        RAISE check_violation USING CONSTRAINT = 'plugins_published_title_required',
            MESSAGE = 'Published plugins must have a title. Enter a title in plugin settings.';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER versions_require_plugin_title BEFORE INSERT OR UPDATE OF plugin_slug ON versions
    FOR EACH ROW EXECUTE FUNCTION require_version_plugin_title();
