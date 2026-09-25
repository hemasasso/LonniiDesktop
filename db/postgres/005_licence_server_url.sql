-- ============================================================================
-- Lonnii Desktop - where a workspace's licence server lives
--
-- Same rules as the earlier scripts: additive, nullable, IF NOT EXISTS, in a
-- transaction.
--
-- Taken from the credentials file at setup and kept, because everything after
-- installation needs it: refreshing settings, and registering a new till - which
-- has to be counted on our server rather than locally, or a shop could grant
-- itself machines simply by adding rows on its own hardware.
--
-- Nullable: workspaces created before the desktop product existed have no
-- licence server, and never needed one.
-- ============================================================================

BEGIN;

ALTER TABLE groupes ADD COLUMN IF NOT EXISTS licence_server_url VARCHAR(255);

COMMIT;
