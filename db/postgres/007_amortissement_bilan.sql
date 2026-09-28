-- ============================================================================
-- Lonnii Desktop - Amortissement & Bilan
--
-- Backs the Amortissement module (fixed assets and their depreciation
-- schedules) and the Bilan module (balance sheet, compte de résultat, manual
-- écritures, opening stock per year).
--
-- Same rules as the earlier scripts: additive, nullable or defaulted, IF NOT
-- EXISTS, in a transaction.
--
-- Like 006, this invents nothing. The first five tables and their indexes are
-- copied verbatim from Lonnii Business's backend/setup_amortissement_bilan.sql,
-- so re-running this against a database that already has them does nothing.
--
-- stock_snapshots is the exception: the source repo has no CREATE TABLE for it
-- anywhere. Its shape below is INFERRED from backend/routes/gestionBilan.js,
-- which reads and writes group_id (not groupe_id), annee, stock_value_debut,
-- snapshot_date and created_by, and relies on ON CONFLICT (group_id, annee).
-- If the live table exists this statement is skipped, but compare its columns
-- first: the desktop model keys on (group_id, annee) and maps those five
-- columns only.
--
-- The desktop also reads can_edit_resultat_donnees, which gestionBilan.js
-- checks but no repo SQL ever inserts. It is added to gestion_privileges here.
-- ============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS immobilisations (
    id SERIAL PRIMARY KEY,
    groupe_id VARCHAR(255) NOT NULL,
    nom VARCHAR(255) NOT NULL,
    description TEXT,
    categorie VARCHAR(100) NOT NULL DEFAULT 'materiel',
    date_acquisition DATE NOT NULL,
    valeur_acquisition DECIMAL(15,2) NOT NULL,
    valeur_residuelle DECIMAL(15,2) DEFAULT 0,
    duree_amortissement INTEGER NOT NULL,
    methode_amortissement VARCHAR(50) NOT NULL DEFAULT 'lineaire',
    taux_degressif DECIMAL(5,2) DEFAULT NULL,
    date_mise_en_service DATE,
    statut VARCHAR(50) DEFAULT 'actif',
    date_cession DATE,
    valeur_cession DECIMAL(15,2),
    motif_sortie TEXT,
    numero_inventaire VARCHAR(100),
    localisation VARCHAR(255),
    fournisseur VARCHAR(255),
    numero_facture VARCHAR(100),
    notes TEXT,
    created_by VARCHAR(255) NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS amortissement_echeances (
    id SERIAL PRIMARY KEY,
    immobilisation_id INTEGER NOT NULL REFERENCES immobilisations(id) ON DELETE CASCADE,
    groupe_id VARCHAR(255) NOT NULL,
    annee INTEGER NOT NULL,
    numero_annee INTEGER NOT NULL,
    date_debut DATE NOT NULL,
    date_fin DATE NOT NULL,
    valeur_debut_periode DECIMAL(15,2) NOT NULL,
    dotation_annuelle DECIMAL(15,2) NOT NULL,
    amortissement_cumule DECIMAL(15,2) NOT NULL,
    valeur_nette_comptable DECIMAL(15,2) NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS bilan_comptes (
    id SERIAL PRIMARY KEY,
    groupe_id VARCHAR(255) NOT NULL,
    numero_compte VARCHAR(20) NOT NULL,
    libelle VARCHAR(255) NOT NULL,
    type_compte VARCHAR(50) NOT NULL,
    sous_type VARCHAR(100),
    solde DECIMAL(15,2) DEFAULT 0,
    description TEXT,
    is_system BOOLEAN DEFAULT FALSE,
    created_by VARCHAR(255),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS bilan_ecritures (
    id SERIAL PRIMARY KEY,
    groupe_id VARCHAR(255) NOT NULL,
    compte_id INTEGER NOT NULL REFERENCES bilan_comptes(id),
    date_ecriture DATE NOT NULL,
    libelle VARCHAR(255) NOT NULL,
    montant_debit DECIMAL(15,2) DEFAULT 0,
    montant_credit DECIMAL(15,2) DEFAULT 0,
    reference VARCHAR(100),
    notes TEXT,
    created_by VARCHAR(255) NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS resultat_comptes (
    id SERIAL PRIMARY KEY,
    groupe_id VARCHAR(255) NOT NULL,
    numero_compte VARCHAR(20) NOT NULL,
    libelle VARCHAR(255) NOT NULL,
    type_compte VARCHAR(50) NOT NULL,
    sous_type VARCHAR(100),
    solde DECIMAL(15,2) DEFAULT 0,
    description TEXT,
    is_system BOOLEAN DEFAULT FALSE,
    created_by VARCHAR(255),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Inferred - see the header.
CREATE TABLE IF NOT EXISTS stock_snapshots (
    id SERIAL PRIMARY KEY,
    group_id VARCHAR(255) NOT NULL,
    annee INTEGER NOT NULL,
    stock_value_debut DECIMAL(15,2) DEFAULT 0,
    snapshot_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(255),
    UNIQUE (group_id, annee)
);

CREATE INDEX IF NOT EXISTS idx_immobilisations_groupe ON immobilisations(groupe_id);
CREATE INDEX IF NOT EXISTS idx_immobilisations_statut ON immobilisations(statut);
CREATE INDEX IF NOT EXISTS idx_immobilisations_categorie ON immobilisations(categorie);
CREATE INDEX IF NOT EXISTS idx_amortissement_echeances_immo ON amortissement_echeances(immobilisation_id);
CREATE INDEX IF NOT EXISTS idx_amortissement_echeances_groupe ON amortissement_echeances(groupe_id);
CREATE INDEX IF NOT EXISTS idx_amortissement_echeances_annee ON amortissement_echeances(annee);
CREATE INDEX IF NOT EXISTS idx_bilan_comptes_groupe ON bilan_comptes(groupe_id);
CREATE INDEX IF NOT EXISTS idx_bilan_comptes_type ON bilan_comptes(type_compte);
CREATE INDEX IF NOT EXISTS idx_bilan_ecritures_groupe ON bilan_ecritures(groupe_id);
CREATE INDEX IF NOT EXISTS idx_bilan_ecritures_compte ON bilan_ecritures(compte_id);
CREATE INDEX IF NOT EXISTS idx_bilan_ecritures_date ON bilan_ecritures(date_ecriture);
CREATE INDEX IF NOT EXISTS idx_resultat_comptes_groupe ON resultat_comptes(groupe_id);
CREATE INDEX IF NOT EXISTS idx_resultat_comptes_type ON resultat_comptes(type_compte);

INSERT INTO gestion_privileges (name, display_name, description, module, is_admin_only)
VALUES ('can_edit_resultat_donnees', 'Modifier Données Résultat',
        'Permet de saisir le stock de début d''année et les montants manuels du compte de résultat',
        'bilan', TRUE)
ON CONFLICT (name) DO NOTHING;

COMMIT;
