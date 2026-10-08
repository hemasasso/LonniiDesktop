-- ============================================================================
-- Lonnii Desktop - per-shop stock settings
--
-- Lonnii Business's stock_settings is one global key/value table with no shop column;
-- the desktop keeps settings per shop. The new column is nullable, so every existing row
-- stays a global default that Business keeps reading exactly as before.
--
-- Same rules as the earlier scripts: additive, IF NOT EXISTS, in a transaction.
-- ============================================================================

BEGIN;

ALTER TABLE stock_settings ADD COLUMN IF NOT EXISTS group_id VARCHAR(36);
CREATE INDEX IF NOT EXISTS idx_stock_settings_group_id ON stock_settings(group_id);

COMMIT;
