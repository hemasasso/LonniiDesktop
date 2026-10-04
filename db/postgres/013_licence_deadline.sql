-- ============================================================================
-- Lonnii Desktop - the offline deadline (hard stop at max_offline_days)
--
-- Same rules as the earlier scripts: additive only, nullable, IF NOT EXISTS,
-- in a transaction. Requires 004_offline_grace_period.sql.
--
-- These columns are only ever written on an installation that has a licence
-- server to answer to (groupes.licence_server_url set). The OCI server itself
-- never locks itself, so on PostgreSQL they simply stay NULL - they exist so the
-- shared EF model can read the table.
-- ============================================================================

BEGIN;

-- The server's MustReconnectBy as it last arrived. Past it the workspace is
-- locked until the machine reaches the licence server again.
ALTER TABLE groupes ADD COLUMN IF NOT EXISTS licence_deadline TIMESTAMP;

-- The latest instant the installation has seen, from its own clock or the
-- server's. "Now" is never earlier than this, so winding the clock back buys
-- nothing.
ALTER TABLE groupes ADD COLUMN IF NOT EXISTS licence_clock_mark TIMESTAMP;

COMMIT;
