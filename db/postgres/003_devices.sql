-- ============================================================================
-- Lonnii Desktop - the devices table
--
-- Same rules as 001 and 002: additive only, IF NOT EXISTS, in a transaction.
--
-- New to the desktop product; Lonnii Business has no equivalent, because a web
-- app never needed to know which machine it was running on. Creating a table
-- touches nothing the web app reads, so this is the safest script of the three.
--
-- Note groupes.is_deleted is NOT added here: the live schema already has it.
-- ============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS devices (
    id              VARCHAR(36) PRIMARY KEY,
    group_id        VARCHAR(36) NOT NULL REFERENCES groupes(id) ON DELETE CASCADE,

    -- Fingerprint the client derives from the machine, stable across reinstalls.
    -- Opaque here: the server only ever compares it, so how it is derived can
    -- change without touching this column.
    device_id       VARCHAR(128) NOT NULL,

    device_name     VARCHAR(255),
    platform        VARCHAR(20) NOT NULL DEFAULT 'windows',
    app_version     VARCHAR(50),
    last_ip_address VARCHAR(64),

    registered_at   TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,

    -- Unbinding keeps the row, so a machine that comes back is still recognised
    -- and a revoked binding stops counting towards the shop's limit.
    revoked_at      TIMESTAMP,
    revoked_reason  TEXT
);

-- One row per machine per shop: a machine returning reuses its row rather than
-- taking a second slot. This is what makes the limit hold.
CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_group_device
    ON devices (group_id, device_id);

-- Counting a shop's bound machines is the hot path during activation.
CREATE INDEX IF NOT EXISTS idx_devices_group ON devices (group_id);

COMMIT;

-- Note there is deliberately no user_id. Machines belong to the shop, not to a
-- person: tying them to individuals would lock a cashier out mid-shift when they
-- moved to another till, while doing nothing about copying, since every extra
-- person sharing a file would bring their own fresh allowance.
