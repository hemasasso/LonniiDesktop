-- ============================================================================
-- Lonnii Desktop - self-registration with email confirmation and our approval
--
-- Same rules as the earlier scripts: additive, defaulted, IF NOT EXISTS, in a transaction.
--
-- groupes.approval_status: 'approved' | 'pending' | 'rejected'. EVERY EXISTING ROW
-- BECOMES 'approved' (the default), so no live customer is affected. A shop that
-- registers itself is created 'pending' and cannot activate until you set it to
-- 'approved', e.g. from the dashboard:
--
--   UPDATE groupes SET approval_status = 'approved' WHERE id = '<group id>';
--
-- Shops waiting for you:
--
--   SELECT g.id, g.nom, u.email, g.created_at
--   FROM groupes g JOIN users u ON u.iduser = g.iduser_admin
--   WHERE g.approval_status = 'pending' ORDER BY g.created_at;
--
-- registration_requests holds a registration until its emailed code comes back; nothing
-- else exists for the shop before then. Rows are deleted on success, expiry or retry.
-- ============================================================================

BEGIN;

ALTER TABLE groupes ADD COLUMN IF NOT EXISTS approval_status VARCHAR(20) NOT NULL DEFAULT 'approved';

CREATE TABLE IF NOT EXISTS registration_requests (
    id               VARCHAR(36)  PRIMARY KEY,
    shop_name        TEXT         NOT NULL,
    email            TEXT         NOT NULL,
    password_hash    TEXT         NOT NULL,
    device_id        TEXT         NOT NULL,
    device_name      TEXT,
    code_hash        TEXT         NOT NULL,
    code_expires_at  TIMESTAMP    NOT NULL,
    attempts         INTEGER      NOT NULL DEFAULT 0,
    ip_address       TEXT,
    created_at       TIMESTAMP    NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_registration_requests_email ON registration_requests (email);

COMMIT;
