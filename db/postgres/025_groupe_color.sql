-- ============================================================================
-- Lonnii Desktop - espace colour
--
-- Same rules as the earlier scripts: additive, IF NOT EXISTS, in a transaction.
-- color: '#RRGGBB' chosen in Administration and shown on the espace cards (desktop picker and
-- phone). NULL = the Lonnii blue (#4361EE). Online shops send theirs with their name.
-- ============================================================================

BEGIN;

ALTER TABLE groupes ADD COLUMN IF NOT EXISTS color VARCHAR(7) NULL;

COMMIT;
