-- ============================================================================
-- Lonnii Desktop - "Activer remise"
--
-- Same rules as the earlier scripts: additive, IF NOT EXISTS, in a transaction.
-- remise_enabled: checked in Configuration, the till (desktop and phone) shows the
-- per-line and global remise fields. NULL = off.
-- ============================================================================

BEGIN;

ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS remise_enabled BOOLEAN NULL;

COMMIT;
