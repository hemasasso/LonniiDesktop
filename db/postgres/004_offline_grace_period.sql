-- ============================================================================
-- Lonnii Desktop - the offline grace period
--
-- Same rules as the earlier scripts: additive only, defaulted, IF NOT EXISTS,
-- in a transaction.
--
-- What this is for: an online-mode shop pays a lower price plus a yearly fee.
-- Nothing physical stops it installing, cutting the internet, and using the
-- software for ever as though it had bought the far dearer offline licence.
-- These two columns are how an installation knows it has stopped checking in.
-- ============================================================================

BEGIN;

-- How many days this workspace may run without reaching the licence server.
-- 7 rather than 0: zero would read as "no offline days allowed" and stop every
-- existing workspace the moment the check goes live.
--
-- Per workspace, not a constant, so a shop with genuinely poor connectivity can
-- be given a longer leash without shipping a new build - the same reasoning as
-- max_devices.
ALTER TABLE groupes ADD COLUMN IF NOT EXISTS max_offline_days INTEGER NOT NULL DEFAULT 7;

-- When this workspace last reached the licence server. Written from server time,
-- never from the client's, so winding a till's clock back buys nothing.
--
-- Also the detection side of this: an online-mode shop whose value here has gone
-- quiet is one that has stopped checking in, and that is visible in the dashboard
-- before the grace period even runs out.
ALTER TABLE groupes ADD COLUMN IF NOT EXISTS last_licence_check_at TIMESTAMP;

COMMIT;

-- Shops that have gone quiet, worth watching in online mode:
--   SELECT nom, mode, last_licence_check_at
--   FROM groupes
--   WHERE mode = 'online'
--     AND (last_licence_check_at IS NULL
--          OR last_licence_check_at < NOW() - INTERVAL '7 days')
--     AND is_deleted = FALSE
--   ORDER BY last_licence_check_at NULLS FIRST;
