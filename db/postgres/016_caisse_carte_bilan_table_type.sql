-- ============================================================================
-- Lonnii Desktop - card float on the caisse, and the table a bilan entry belongs to
--
-- Same rules as the earlier scripts: additive, nullable or with a DEFAULT, IF NOT EXISTS,
-- in a transaction. Found by comparing the model's columns with information_schema
-- (2026-10-08); the sibling renames are mapped in the model instead (ColumnNames).
-- ============================================================================

BEGIN;

-- Card-account balance at opening / counted at closing, like the existing mobile-money pair.
ALTER TABLE caisses ADD COLUMN IF NOT EXISTS montant_initial_carte NUMERIC(15,2) NOT NULL DEFAULT 0;
ALTER TABLE caisses ADD COLUMN IF NOT EXISTS montant_final_carte NUMERIC(15,2);

-- Which statement a bilan entry feeds: 'bilan' (the default, every existing row) or the
-- financial / exceptional result tables.
ALTER TABLE bilan_ecritures ADD COLUMN IF NOT EXISTS table_type VARCHAR(30) NOT NULL DEFAULT 'bilan';

COMMIT;
