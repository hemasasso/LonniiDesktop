-- ============================================================================
-- Lonnii Desktop - "Montant en lettres" on A4 reçus and factures
--
-- Same rules as the earlier scripts: additive, IF NOT EXISTS, in a transaction.
-- Ticked in the receipt settings, an A4 reçu / facture writes its total in words too.
-- NULL = off.
-- ============================================================================

BEGIN;

ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS receipt_amount_in_words BOOLEAN NULL;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS facture_amount_in_words BOOLEAN NULL;

COMMIT;
