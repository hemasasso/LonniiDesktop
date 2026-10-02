-- ============================================================================
-- Lonnii Desktop - Paiement Groupé (groupe_payments)
--
-- Lonnii Business's own table, from backend/migrations/add_groupe_payments_table.sql,
-- add_avoir_to_groupe_payments.sql and add_partial_change_to_groupe_payments.sql,
-- repeated verbatim so the desktop can be pointed at a database where they were never run.
-- Re-running it against one that has them does nothing.
--
-- The three is_avoir_solded / avoir_solded_at / avoir_solded_by columns have no CREATE in
-- that repo: POST /ventes/solder-avoir reads and writes them, so they exist in the live
-- database, but their types are inferred. avoir_solded_by is VARCHAR(36) because it holds a
-- user id, and users.iduser is VARCHAR(36).
--
-- created_by is INTEGER in the web app, which fills it with parseInt() of a GUID user id.
-- The desktop does not map it; it stays NULL.
--
-- Same rules as the earlier scripts: additive, nullable or defaulted, IF NOT EXISTS, in a
-- transaction.
-- ============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS groupe_payments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id VARCHAR(255) NOT NULL,
    client_name VARCHAR(255),
    caissier_name VARCHAR(255),
    facture_ids UUID[] NOT NULL,
    factures_data JSONB NOT NULL,
    total_amount DECIMAL(15, 2) NOT NULL,
    mode_paiement VARCHAR(50) NOT NULL DEFAULT 'cash',
    created_by INTEGER,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

ALTER TABLE groupe_payments ADD COLUMN IF NOT EXISTS caissier_name VARCHAR(255);
ALTER TABLE groupe_payments ADD COLUMN IF NOT EXISTS avoir_amount NUMERIC(15,2) DEFAULT 0;
ALTER TABLE groupe_payments ADD COLUMN IF NOT EXISTS montant_paye NUMERIC(15,2) DEFAULT 0;
ALTER TABLE groupe_payments ADD COLUMN IF NOT EXISTS partial_change_given NUMERIC(15,2) DEFAULT 0;
ALTER TABLE groupe_payments ADD COLUMN IF NOT EXISTS is_avoir_solded BOOLEAN DEFAULT FALSE;
ALTER TABLE groupe_payments ADD COLUMN IF NOT EXISTS avoir_solded_at TIMESTAMP;
ALTER TABLE groupe_payments ADD COLUMN IF NOT EXISTS avoir_solded_by VARCHAR(36);

CREATE INDEX IF NOT EXISTS idx_groupe_payments_group_id ON groupe_payments(group_id);
CREATE INDEX IF NOT EXISTS idx_groupe_payments_created_at ON groupe_payments(created_at DESC);

COMMIT;
