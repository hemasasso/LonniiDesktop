-- ============================================================================
-- Lonnii Desktop - shops registered from a phone
--
-- Same rules as the earlier scripts: additive, nullable, IF NOT EXISTS, in a transaction.
-- android or ios: a shop with no computer. It is created online (still pending), and the
-- dashboard approves it online, with a subscription.
-- ============================================================================

BEGIN;

ALTER TABLE registration_requests ADD COLUMN IF NOT EXISTS platform TEXT;

COMMIT;
