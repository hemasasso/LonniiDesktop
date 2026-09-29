-- 008: Audit attendance (member_work_log desktop columns) and currency position.
--
-- Additive only, re-runnable. Read before applying; see README.md.
--
-- member_work_log is Lonnii Business's own table (backend/migrations/add_work_schedule_tables.sql);
-- the CREATE below is that file's definition verbatim, so on a database where it was applied
-- it is a no-op. ip_address is written by backend/utils/groupSessions.js but is missing from
-- that migration, so it is added here too - on the live database it most likely exists already.
--
-- last_seen_at, current_module and device_name are new to the desktop: it has no socket
-- connection to tell who is online, so each client sends a heartbeat instead. Lonnii Business
-- neither reads nor writes them.

BEGIN;

-- Where the currency label goes: FALSE = after the amount (1 000 FCFA, every existing
-- workspace), TRUE = before it ($1 000).
ALTER TABLE groupes ADD COLUMN IF NOT EXISTS currency_before BOOLEAN NOT NULL DEFAULT FALSE;

-- Mobile-money balance counted when a caisse closes. New to the desktop; montant_final stays
-- the cash count, so Lonnii Business keeps reading it unchanged. NULL on every existing row,
-- which the desktop reads as "closed on a cash count alone" - their écart stays cash only.
ALTER TABLE caisses ADD COLUMN IF NOT EXISTS montant_final_mobile DECIMAL(15, 2);

-- Product type: the desktop maps it onto Lonnii Business's own products.stock_type
-- (marchandise / produit_fini / matiere_premiere, default 'marchandise'), which the live
-- database already has. An earlier version of this script added a separate type_produit
-- column instead; if it was applied, that column is unused and harmless. Added here only
-- for a database that predates stock_type.
ALTER TABLE products ADD COLUMN IF NOT EXISTS stock_type VARCHAR(50) DEFAULT 'marchandise';

CREATE TABLE IF NOT EXISTS member_work_log (
  id              SERIAL       PRIMARY KEY,
  user_id         VARCHAR(36)  NOT NULL,
  group_id        VARCHAR(255) NOT NULL,
  session_date    DATE         NOT NULL DEFAULT CURRENT_DATE,
  login_at        TIMESTAMP    NOT NULL,
  logout_at       TIMESTAMP,
  duration_minutes INTEGER,
  session_token   VARCHAR(128),
  created_at      TIMESTAMP    DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_mwl_user_group_date ON member_work_log(user_id, group_id, session_date);
CREATE INDEX IF NOT EXISTS idx_mwl_group_date       ON member_work_log(group_id, session_date);
CREATE INDEX IF NOT EXISTS idx_mwl_session_token    ON member_work_log(session_token);

ALTER TABLE member_work_log ADD COLUMN IF NOT EXISTS ip_address     VARCHAR(64);
ALTER TABLE member_work_log ADD COLUMN IF NOT EXISTS last_seen_at   TIMESTAMP;
ALTER TABLE member_work_log ADD COLUMN IF NOT EXISTS current_module VARCHAR(64);
ALTER TABLE member_work_log ADD COLUMN IF NOT EXISTS device_name    VARCHAR(255);

COMMIT;
