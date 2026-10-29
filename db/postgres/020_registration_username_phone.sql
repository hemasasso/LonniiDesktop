-- ============================================================================
-- Lonnii Desktop - username and phone asked at sign-up
--
-- Same rules as the earlier scripts: additive, nullable, IF NOT EXISTS, in a transaction.
-- Held on the request until the emailed code comes back, then copied to the new account.
-- ============================================================================

BEGIN;

ALTER TABLE registration_requests ADD COLUMN IF NOT EXISTS username TEXT;
ALTER TABLE registration_requests ADD COLUMN IF NOT EXISTS phone TEXT;

COMMIT;
