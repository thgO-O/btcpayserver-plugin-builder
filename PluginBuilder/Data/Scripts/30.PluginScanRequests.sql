ALTER TABLE plugin_listing_requests ADD COLUMN review_target jsonb;

-- Legacy requests acquire a validated target on their first manual scan.
-- Historical build paths may not satisfy Warden's directory contract.
INSERT INTO settings(key, value) VALUES ('AutomaticListingScansEnabled', 'false') ON CONFLICT (key) DO NOTHING;

CREATE FUNCTION emit_plugin_scan_request(slug text, target jsonb, actor text, token uuid, listing_id integer, source text)
RETURNS uuid AS $$
DECLARE request_id uuid := gen_random_uuid();
BEGIN
    PERFORM emit_admin_event('plugin.scan_requested', target || jsonb_build_object(
        'scanRequestId', request_id, 'pluginSlug', slug, 'listingRequestId', listing_id,
        'userId', actor, 'tokenId', token, 'source', source));
    RETURN request_id;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION admin_listing_event() RETURNS trigger AS $$
BEGIN
    PERFORM emit_admin_event('listing.requested', jsonb_build_object('userId', NEW.submitted_by,
        'pluginSlug', NEW.plugin_slug, 'listingRequestId', NEW.id,
        'releaseNote', NEW.release_note, 'telegramVerificationMessage', NEW.telegram_verification_message,
        'userReviews', NEW.user_reviews, 'announcementDate', NEW.announcement_date) || COALESCE(NEW.review_target, '{}'::jsonb));
    -- Read the setting in the submission transaction; missing or invalid values disable automation.
    IF NEW.review_target IS NOT NULL AND EXISTS (
        SELECT 1 FROM settings WHERE key = 'AutomaticListingScansEnabled' AND lower(trim(value)) = 'true'
    ) THEN
        PERFORM emit_plugin_scan_request(NEW.plugin_slug, NEW.review_target, NEW.submitted_by, NULL, NEW.id, 'listing');
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
