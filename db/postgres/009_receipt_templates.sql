-- ============================================================================
-- Lonnii Desktop - receipt/facture layouts and section switches (ventes_parametres)
--
-- Unlike 006, these columns are the desktop's own: Lonnii Business has one fixed
-- layout per document and no way to hide parts of it. They sit on the web app's
-- table because that row is already the one place a workspace's printed-document
-- configuration lives; the web app ignores columns it does not know.
--
-- Every column is nullable with no DEFAULT on purpose. NULL is what the desktop
-- reads as "never configured", which prints the 80 mm ticket exactly as before -
-- so a row the web app writes or updates keeps printing the same document.
--
-- *_hidden_sections hold comma-separated keys (see ReceiptSections in
-- Lonnii.Shared). NULL means the defaults apply; '' means the shop hides nothing.
--
-- Same rules as the earlier scripts: additive, nullable, IF NOT EXISTS, in a
-- transaction.
-- ============================================================================

BEGIN;

ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS receipt_template VARCHAR(20);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS facture_template VARCHAR(20);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS receipt_hidden_sections TEXT;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS facture_hidden_sections TEXT;

-- Sender block of the A4 layout (and the contact line on tickets).
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS company_address VARCHAR(255);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS company_phone VARCHAR(100);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS company_email VARCHAR(255);

-- Free text, one identifier per line (RCCM, NIU, capital...), because which ones
-- must be printed differs by country.
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS company_legal_info TEXT;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS legal_footer_text TEXT;

-- Percentage, e.g. 19.25. NULL = no tax breakdown printed.
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS tva_rate NUMERIC(5,2);

COMMIT;
