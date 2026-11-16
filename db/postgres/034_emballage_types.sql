-- ============================================================================
-- Lonnii Desktop - Stock > Emballages: interchangeable packaging
--
-- Additive only, IF NOT EXISTS, in a transaction. Apply after 033.
--
-- Products whose crates are the same (all SODIBO crates, Brafaso and
-- Brafort...) share one emballage type: the Emballages tab then counts their
-- empties and what clients owe as one. products.emballage_type_id points at
-- the type; NULL = the product's emballage is counted on its own.
-- emballages_mouvements.product_id may hold a type's id (a count, supplier
-- return or casse for the whole type).
-- ============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS emballage_types (
    id VARCHAR(36) PRIMARY KEY,
    group_id VARCHAR(255) NOT NULL,
    nom VARCHAR(255) NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_emballage_types_group_id
    ON emballage_types (group_id);

ALTER TABLE products ADD COLUMN IF NOT EXISTS emballage_type_id VARCHAR(255);

COMMIT;
