-- ============================================================================
-- Lonnii Desktop - remaining additive columns on the live PostgreSQL database
--
-- Same rules as 001: additive only, nullable or defaulted, IF NOT EXISTS,
-- in a transaction. Read it before running it.
--
-- These are columns the desktop model expects that the live schema does not
-- have. Everything the live schema has and the model does not is left alone -
-- EF simply ignores unknown columns, and all of them are nullable or defaulted.
--
-- Verified against the live schema of users, categories and products on
-- 2026-09-24.
-- ============================================================================

BEGIN;

-- When the password last changed. Access tokens issued before this moment are
-- refused, so a password reset ends existing sessions instead of leaving them
-- valid for hours. Nullable: an existing account has never reset, and NULL
-- correctly means "no cut-off".
ALTER TABLE users ADD COLUMN IF NOT EXISTS password_changed_at TIMESTAMP;

-- Category illustration shown in the desktop stock screens.
ALTER TABLE categories ADD COLUMN IF NOT EXISTS image_url VARCHAR(500);

-- Stock sold without tracking a quantity - a service, or something weighed out
-- of a sack. Distinct from the live stock_type column, which the desktop model
-- does not read; if these two ever need to agree, reconcile them deliberately
-- rather than letting both drift.
ALTER TABLE products ADD COLUMN IF NOT EXISTS stock_illimite BOOLEAN NOT NULL DEFAULT FALSE;

-- The unit shown beside a quantity on screen and on a receipt, e.g. "kg", "sac".
ALTER TABLE products ADD COLUMN IF NOT EXISTS unite_affichage VARCHAR(50);

-- Soft delete, matching the pattern already used on users and groupes. A product
-- that has been sold cannot simply be removed without orphaning ventes_items.
ALTER TABLE products ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMP;
ALTER TABLE products ADD COLUMN IF NOT EXISTS deleted_by VARCHAR(36);

-- Who cancelled a sale. Lonnii Business's own PUT /:id/annuler never records this - only
-- cancellation_reason and cancelled_at - so this is desktop-only, same as avoir_solded_by
-- already is for who settled an avoir.
ALTER TABLE ventes ADD COLUMN IF NOT EXISTS cancelled_by VARCHAR(36);

COMMIT;

-- ----------------------------------------------------------------------------
-- NOT handled here, on purpose: products.tags
--
-- Live it is text[]; the desktop model has it as a single string. Changing the
-- column type would break Lonnii Business, and changing the model would make the
-- SQLite and PostgreSQL shapes disagree. It needs a decision, not a migration -
-- most likely mapping the model to text[] on PostgreSQL only, the same way the
-- money converter is applied to SQLite only.
--
-- Nothing in the desktop reads tags today, so this is not urgent.
-- ----------------------------------------------------------------------------
