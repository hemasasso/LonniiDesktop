-- ============================================================================
-- Lonnii Desktop - offline grace period of 14 days for online-mode shops
--
-- Same rules as the earlier scripts: additive/defaulted, in a transaction.
-- (The only data change is the UPDATE below, and it is limited to rows still on
-- the old default.)
--
-- Was 7 days (004_offline_grace_period.sql). An online shop may now go 14 days
-- without reaching the licence server before the desktop stops. Local-mode shops
-- are not affected: they are never sent a deadline (MustReconnectBy is null).
-- ============================================================================

BEGIN;

-- New workspaces start at 14.
ALTER TABLE groupes ALTER COLUMN max_offline_days SET DEFAULT 14;

-- Online workspaces still on the old default move to 14. A workspace the owner
-- gave a different value (say 30 for poor connectivity) is left alone.
UPDATE groupes
SET max_offline_days = 14
WHERE mode = 'online'
  AND max_offline_days = 7;

COMMIT;

-- Check:
--   SELECT mode, max_offline_days, COUNT(*) FROM groupes GROUP BY 1, 2 ORDER BY 1, 2;
