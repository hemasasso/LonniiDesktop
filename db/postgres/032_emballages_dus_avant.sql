-- ============================================================================
-- Lonnii Desktop - empties the client owed before a sale
--
-- Additive only, IF NOT EXISTS, in a transaction.
--
-- Kept on each sale so the reçu and the facture can print the client's total
-- still to bring back, old empties included ("Total à rapporter : 1 + 1/2
-- caisses (dont 1/2 d'avant)") - also on a reprint later. Counts in quarters,
-- like the other emballage columns.
-- ============================================================================

BEGIN;

ALTER TABLE ventes ADD COLUMN IF NOT EXISTS emballages_dus_avant NUMERIC(10,2);

COMMIT;
