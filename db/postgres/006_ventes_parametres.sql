-- ============================================================================
-- Lonnii Desktop - receipt and invoice configuration (ventes_parametres)
--
-- Backs "Paramètre Reçu et Facture": logo, QR code, company name, the two
-- document titles, the boxed notices, the footers and the typeface.
--
-- Same rules as the earlier scripts: additive, nullable or defaulted, IF NOT
-- EXISTS, in a transaction.
--
-- Unlike 001-005 this adds almost nothing new to production. Every column below
-- already exists in Lonnii Business's own schema, spread across
-- backend/migrations/create_ventes_parametres_table.sql, add_facture_text_columns.sql,
-- add_receipt_footer_text.sql, add_avoir_notice_columns.sql and
-- add_font_config_columns.sql. The script is here so the desktop can be pointed at
-- a database where some of those migrations were never run - which is exactly the
-- situation db/postgres/README.md describes for `ventes` - and re-running it against
-- a database that already has them does nothing.
--
-- The DEFAULTs are the web app's own, repeated verbatim, and they match
-- ReceiptSettingsDefaults in Lonnii.Shared. A shop that has never opened the
-- settings screen therefore gets the same receipt from either application.
--
-- The desktop model does NOT map the `id` column: it keys on groupe_id, which is
-- UNIQUE and the only thing anything looks a row up by. An INSERT from the desktop
-- omits id, so the sequence default fills it.
-- ============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS ventes_parametres (
    id SERIAL PRIMARY KEY,
    groupe_id VARCHAR(255) NOT NULL UNIQUE,
    company_name VARCHAR(255),
    note_under_qr TEXT,
    logo_path VARCHAR(500),
    qr_code_path VARCHAR(500),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (groupe_id) REFERENCES groupes(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_ventes_parametres_groupe ON ventes_parametres(groupe_id);

-- Facture: an unpaid sale, settled at the till later.
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS facture_title VARCHAR(100) DEFAULT 'FACTURE';
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS facture_notice_title VARCHAR(255) DEFAULT 'À RÉGLER À LA CAISSE';
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS facture_notice_text TEXT DEFAULT 'Merci de présenter cette facture pour le paiement';
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS facture_footer_text VARCHAR(255) DEFAULT 'Merci de votre visite!';
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS facture_title_font_size INTEGER DEFAULT 16;

-- Reçu: a settled sale.
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS receipt_title VARCHAR(100) DEFAULT 'REÇU DE VENTE';
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS receipt_footer_text VARCHAR(255) DEFAULT 'Merci pour votre achat!';
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS receipt_title_font_size INTEGER DEFAULT 16;

-- Printed before the seller's name. Configurable because the person who rang the
-- sale up is called something different per trade - vendeur, caissier, préparateur.
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS seller_label VARCHAR(255) DEFAULT 'Vendeur';

-- Shown on a receipt only, when the client overpaid and leaves with a credit.
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS avoir_notice_title VARCHAR(255) DEFAULT 'NOTE IMPORTANTE:';
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS avoir_notice_text TEXT
    DEFAULT 'Le client peut présenter ce reçu pour récupérer un avoir de';

-- Typeface, shared by both documents.
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS receipt_font_family VARCHAR(50) DEFAULT 'Courier New';
ALTER TABLE ventes_parametres
    ADD COLUMN IF NOT EXISTS receipt_font_size INTEGER DEFAULT 11;

COMMIT;
