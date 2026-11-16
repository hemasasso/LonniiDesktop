-- ============================================================================
-- Lonnii Desktop - Prélèvement, effort de guerre, consigne, livraison, fractions
--
-- Desktop-only, like 010 (TVA added at the till). Every amount is stored with
-- the sale, so changing a rate or a product later never rewrites an old receipt.
--
-- * ventes_parametres: prelevement_rate (% of HT, NULL = off), livraison_enabled
--   (till asks for a delivery fee), fractions_enabled (sell 1/4, 1/2, 3/4).
--   NULL reads as off everywhere.
-- * products: effort_guerre (fixed amount per unit), emballage_recuperable +
--   consigne_montant (returnable packaging deposit per unit), prix_quart /
--   prix_demi / prix_trois_quarts (the shop's own price per fraction, NULL = not
--   sold), fraction_ouverte (quarters 0-3 left in an opened unit, on top of
--   quantity - quantity itself stays a whole number).
-- * ventes: the amounts charged on the sale; emballages_pris / emballages_rendus
--   (returnable packaging taken / brought back, when emballages_suivi is on -
--   a client's balance is the sum of pris - rendus over their sales).
-- * emballages_retours: empties a client brought back outside a sale.
-- * ventes_items: quarts (fraction sold on top of quantity, 1-3) and its price.
--
-- Same rules as the earlier scripts: additive, nullable or DEFAULT, IF NOT
-- EXISTS, in a transaction.
-- ============================================================================

BEGIN;

ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS prelevement_rate NUMERIC(5,2);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS livraison_enabled BOOLEAN;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS fractions_enabled BOOLEAN;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS emballages_suivi BOOLEAN;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS emballages_unite VARCHAR(50);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS page_orientation VARCHAR(20);
-- Prélèvement on TTC (after TVA) instead of HT; NULL = HT.
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS prelevement_apres_tva BOOLEAN;
-- The shop's own names for the prélèvement and the effort de guerre; NULL = the defaults.
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS prelevement_label VARCHAR(60);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS effort_guerre_label VARCHAR(60);
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS effort_guerre_enabled BOOLEAN;
-- Emballages brought back in advance kept as a client credit; NULL = not accepted.
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS emballages_credit_accepte BOOLEAN;
-- "Emballage de 12 / 16 / 24": units in one emballage, per product, kept on each sale line.
ALTER TABLE products ADD COLUMN IF NOT EXISTS emballage_contenance INTEGER;
ALTER TABLE ventes_items ADD COLUMN IF NOT EXISTS emballage_contenance INTEGER;
-- Emballages the client's credit covered (no consigne on those), and the credit left.
ALTER TABLE ventes_items ADD COLUMN IF NOT EXISTS emballages_credit NUMERIC(10,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS emballages_credit NUMERIC(10,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS emballages_credit_restant NUMERIC(10,2);

ALTER TABLE products ADD COLUMN IF NOT EXISTS effort_guerre NUMERIC(15,2);
ALTER TABLE products ADD COLUMN IF NOT EXISTS emballage_recuperable BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE products ADD COLUMN IF NOT EXISTS consigne_montant NUMERIC(15,2);
ALTER TABLE products ADD COLUMN IF NOT EXISTS prix_quart NUMERIC(15,2);
ALTER TABLE products ADD COLUMN IF NOT EXISTS prix_demi NUMERIC(15,2);
ALTER TABLE products ADD COLUMN IF NOT EXISTS prix_trois_quarts NUMERIC(15,2);
ALTER TABLE products ADD COLUMN IF NOT EXISTS fraction_ouverte INTEGER NOT NULL DEFAULT 0;

ALTER TABLE ventes ADD COLUMN IF NOT EXISTS prelevement_rate NUMERIC(5,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS prelevement_amount NUMERIC(15,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS effort_guerre_amount NUMERIC(15,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS consigne_amount NUMERIC(15,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS livraison_amount NUMERIC(15,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS emballages_pris NUMERIC(10,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS emballages_rendus NUMERIC(10,2);
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS consigne_rendue NUMERIC(15,2);

CREATE TABLE IF NOT EXISTS emballages_retours (
    id VARCHAR(36) PRIMARY KEY,
    group_id VARCHAR(255) NOT NULL,
    client_nom VARCHAR(255),
    client_telephone VARCHAR(100),
    client_email VARCHAR(255),
    quantite NUMERIC(10,2) NOT NULL,
    product_id VARCHAR(255),
    nom_produit VARCHAR(255),
    montant NUMERIC(15,2),
    mode_paiement VARCHAR(50),
    vente_id VARCHAR(255),
    created_by VARCHAR(255),
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_emballages_retours_group_id ON emballages_retours(group_id);

ALTER TABLE ventes_items ADD COLUMN IF NOT EXISTS quarts INTEGER;
ALTER TABLE ventes_items ADD COLUMN IF NOT EXISTS prix_fraction NUMERIC(15,2);
ALTER TABLE ventes_items ADD COLUMN IF NOT EXISTS consigne NUMERIC(15,2);
ALTER TABLE ventes_items ADD COLUMN IF NOT EXISTS emballages_pris NUMERIC(10,2);

-- Emballage counts in quarters (1.5 = 1 caisse and a half). Also corrects a database
-- where an earlier copy of this script created them as INTEGER.
ALTER TABLE ventes ALTER COLUMN emballages_pris TYPE NUMERIC(10,2);
ALTER TABLE ventes ALTER COLUMN emballages_rendus TYPE NUMERIC(10,2);
ALTER TABLE ventes_items ALTER COLUMN emballages_pris TYPE NUMERIC(10,2);
ALTER TABLE emballages_retours ALTER COLUMN quantite TYPE NUMERIC(10,2);

COMMIT;
