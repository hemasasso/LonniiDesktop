-- ============================================================================
-- Lonnii Desktop - cover photo of an espace (shown in the group picker)
--
-- Same rules as the earlier scripts: additive, nullable, IF NOT EXISTS, in a transaction.
-- Added after 001-013 were written (migration AddGroupePhoto), so no earlier script has it.
-- ============================================================================

BEGIN;

ALTER TABLE groupes ADD COLUMN IF NOT EXISTS photo_url TEXT;

COMMIT;
