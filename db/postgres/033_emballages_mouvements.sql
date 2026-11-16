-- ============================================================================
-- Lonnii Desktop - Stock > Emballages: the shop's own empties
--
-- Additive only, IF NOT EXISTS, in a transaction.
--
-- Changes to the empties held in the shop other than a client bringing them
-- back (that stays in emballages_retours): an inventaire (stored as the
-- difference it made), empties sent back to the supplier (with the consigne
-- the supplier paid back, if any, also written to the caisse as an entree
-- of category consigne_fournisseur), and broken or lost ones.
-- quantite is signed (+ adds empties, - removes), counted by quarters.
-- ============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS emballages_mouvements (
    id VARCHAR(36) PRIMARY KEY,
    group_id VARCHAR(255) NOT NULL,
    product_id VARCHAR(255) NOT NULL,
    nom_produit VARCHAR(255),
    type VARCHAR(30) NOT NULL,
    quantite NUMERIC(10,2) NOT NULL,
    montant NUMERIC(15,2),
    mode_paiement VARCHAR(30),
    supplier_id VARCHAR(255),
    supplier_name VARCHAR(255),
    note VARCHAR(500),
    created_by VARCHAR(255),
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_emballages_mouvements_group_product
    ON emballages_mouvements (group_id, product_id);

COMMIT;
