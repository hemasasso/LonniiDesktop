-- ============================================================================
-- Lonnii Desktop - sale-to-caisse link, one-column discount, supplier balance
--
-- Same rules as the earlier scripts: additive, nullable or with a DEFAULT, IF NOT EXISTS,
-- in a transaction. Found by comparing the model's columns with information_schema
-- (2026-10-08). Nothing Lonnii Business reads is changed or removed.
-- ============================================================================

BEGIN;

-- Which cash-register session a sale was rung up in. Existing sales have none.
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS caisse_id INTEGER;
CREATE INDEX IF NOT EXISTS idx_ventes_caisse_id ON ventes(caisse_id);

-- Lonnii Business keeps a line's discount in two columns (discount_percentage and
-- discount_amount) beside discount_type; the desktop keeps one value that discount_type
-- explains. The new column is filled from the old ones so existing lines show their
-- discount; Business's own columns are left exactly as they are.
ALTER TABLE ventes_items ADD COLUMN IF NOT EXISTS discount NUMERIC(15,2) NOT NULL DEFAULT 0;

UPDATE ventes_items
SET discount = CASE
        WHEN discount_type = 'percentage' THEN COALESCE(discount_percentage, 0)
        ELSE COALESCE(discount_amount, 0)
    END
WHERE discount = 0
  AND (COALESCE(discount_percentage, 0) <> 0 OR COALESCE(discount_amount, 0) <> 0);

-- What the shop still owes a supplier (a running balance, adjusted by an admin).
ALTER TABLE suppliers ADD COLUMN IF NOT EXISTS montant_du NUMERIC(15,2) NOT NULL DEFAULT 0;

COMMIT;
