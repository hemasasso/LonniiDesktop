-- ============================================================================
-- Lonnii Desktop - sync_id on the integer-keyed tables phones write to
--
-- Same rules as the earlier scripts: additive, IF NOT EXISTS, in a transaction.
-- The integer ids are only unique within one database; once phones save on OCI while a
-- shop's host saves too, both can hand out the same number. sync_id is the GUID live sync
-- matches rows by. The DEFAULT fills existing rows and anything Lonnii Business inserts.
-- TEXT, not uuid: the model sends it as a string, and a uuid column refuses text.
-- ============================================================================

BEGIN;

ALTER TABLE caisses                 ADD COLUMN IF NOT EXISTS sync_id TEXT NOT NULL DEFAULT gen_random_uuid()::text;
ALTER TABLE caisse_transactions     ADD COLUMN IF NOT EXISTS sync_id TEXT NOT NULL DEFAULT gen_random_uuid()::text;
ALTER TABLE charges                 ADD COLUMN IF NOT EXISTS sync_id TEXT NOT NULL DEFAULT gen_random_uuid()::text;
ALTER TABLE charges_categories      ADD COLUMN IF NOT EXISTS sync_id TEXT NOT NULL DEFAULT gen_random_uuid()::text;
ALTER TABLE immobilisations         ADD COLUMN IF NOT EXISTS sync_id TEXT NOT NULL DEFAULT gen_random_uuid()::text;
ALTER TABLE amortissement_echeances ADD COLUMN IF NOT EXISTS sync_id TEXT NOT NULL DEFAULT gen_random_uuid()::text;

CREATE UNIQUE INDEX IF NOT EXISTS ix_caisses_sync_id                 ON caisses (sync_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_caisse_transactions_sync_id     ON caisse_transactions (sync_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_charges_sync_id                 ON charges (sync_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_charges_categories_sync_id      ON charges_categories (sync_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_immobilisations_sync_id         ON immobilisations (sync_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_amortissement_echeances_sync_id ON amortissement_echeances (sync_id);

COMMIT;
