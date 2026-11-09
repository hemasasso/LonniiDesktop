-- ============================================================================
-- Lonnii Desktop - Nouvelle Vente tiles: show Qté / show Prix
--
-- Same rules as the earlier scripts: additive, IF NOT EXISTS, in a transaction.
-- Set in Configuration; the till (desktop and phone) shows the stock quantity and
-- the price on each product tile only when on. NULL = on (shown, as before).
-- ============================================================================

BEGIN;

ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS tile_show_quantity BOOLEAN NULL;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS tile_show_price BOOLEAN NULL;

COMMIT;
