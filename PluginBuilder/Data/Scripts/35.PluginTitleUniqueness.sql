-- The same Unicode whitespace characters as string.Trim(); internal spaces are preserved.
CREATE FUNCTION normalize_plugin_title(title text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    RETURN btrim(title, U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000');

CREATE FUNCTION plugin_title_key(title text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    RETURN lower(nullif(normalize_plugin_title(title), '') COLLATE "pg_c_utf8");

UPDATE plugins
SET settings = jsonb_set(settings, '{pluginTitle}', to_jsonb(normalize_plugin_title(settings->>'pluginTitle')))
WHERE jsonb_typeof(settings->'pluginTitle') = 'string';

ALTER TABLE plugins ADD CONSTRAINT plugins_title_length
    CHECK (char_length(settings->>'pluginTitle') <= 500);

CREATE UNIQUE INDEX plugins_title_unique ON plugins (plugin_title_key(settings->>'pluginTitle'));
