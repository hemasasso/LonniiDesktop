-- ============================================================================
-- Lonnii Desktop - TVA added at the till, and print-after-sale switches
--
-- Desktop-only, like 009: Lonnii Business has no TVA handling and always shows
-- the document after a sale.
--
-- ventes.tva_rate / tva_amount are set only when the workspace's prices are HT
-- (ventes_parametres.tva_mode = 'ajoutee'): the till then adds the TVA and
-- total_amount is TTC. They are stored with the sale so a later change of rate
-- cannot rewrite an old receipt. NULL on every sale the web app records, and on
-- every desktop sale whose prices already include TVA.
--
-- The ventes_parametres columns read NULL as the old behaviour: tva_mode
-- 'incluse', and the document opened after every sale.
--
-- Same rules as the earlier scripts: additive, nullable, IF NOT EXISTS, in a
-- transaction.
-- ============================================================================

BEGIN;

ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS tva_mode VARCHAR(10);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS receipt_print_after_sale BOOLEAN;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS facture_print_after_sale BOOLEAN;

ALTER TABLE ventes ADD COLUMN IF NOT EXISTS tva_rate NUMERIC(5,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS tva_amount NUMERIC(15,2);

COMMIT;
