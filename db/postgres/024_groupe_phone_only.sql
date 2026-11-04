-- ============================================================================
-- Lonnii Desktop - phone-only shops
--
-- Same rules as the earlier scripts: additive, IF NOT EXISTS, in a transaction.
-- phone_only: the shop has no computer and our server is its host. Set from the dashboard
-- (Desktop > boutique > "Téléphone uniquement"), and at sign-up for shops registered from a phone.
-- ============================================================================

BEGIN;

ALTER TABLE groupes ADD COLUMN IF NOT EXISTS phone_only BOOLEAN NOT NULL DEFAULT FALSE;

-- Shops registered from a phone and still waiting for approval: until now "online and not
-- approved" was the only sign of them.
UPDATE groupes SET phone_only = TRUE
WHERE mode = 'online' AND approval_status <> 'approved' AND phone_only = FALSE;

COMMIT;
