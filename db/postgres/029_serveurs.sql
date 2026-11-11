-- ============================================================================
-- Lonnii Desktop - Serveurs (waiter / agent on a sale)
--
-- Same rules as the earlier scripts: additive, IF NOT EXISTS, in a transaction.
--
-- * ventes_parametres: the switch and the shop's own name for the field
--   ("Serveur", "Vendeur", "Agent"...). NULL = off / default label.
-- * serveurs: the shop's list of names, like clients - not accounts, they never sign in.
-- * ventes: who served the sale. serveur_nom is a copy of the name at the time of the
--   sale, so renaming or removing a serveur does not rewrite old receipts or stats.
-- ============================================================================

BEGIN;

ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS serveur_enabled BOOLEAN NULL;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS serveur_label VARCHAR(255) NULL;
ALTER TABLE ventes_parametres ADD COLUMN IF NOT EXISTS serveur_label_plural VARCHAR(255) NULL;

CREATE TABLE IF NOT EXISTS serveurs (
    id VARCHAR(36) PRIMARY KEY,
    group_id VARCHAR(255) NOT NULL,
    nom VARCHAR(255) NOT NULL,
    telephone VARCHAR(100),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_serveurs_group_id ON serveurs(group_id);

ALTER TABLE ventes ADD COLUMN IF NOT EXISTS serveur_id VARCHAR(36) NULL;
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS serveur_nom VARCHAR(255) NULL;

CREATE INDEX IF NOT EXISTS idx_ventes_group_serveur ON ventes(group_id, serveur_id);

COMMIT;
