-- ============================================================================
-- Lonnii Desktop - Gestion switch of its own
--
-- Additive only, IF NOT EXISTS, in a transaction. Apply after 034.
--
-- groupes.gestion_access belongs to the web app (Lonnii Business) and is
-- switched from the React dashboard. The desktop app read the same column, so
-- turning Gestion off for a web workspace turned it off in the shop's desktop
-- app too. Lonnii Desktop now reads desktop_gestion_access, set only from the
-- "Lonnii Desktop" side of the dashboard; the two no longer touch each other.
--
-- Starting value: what the desktop app showed until now (a copy of
-- gestion_access) for the shops Lonnii Desktop knows, so nothing changes on the
-- day this is applied. TRUE for every other row (a web workspace that later
-- activates the desktop app starts with Gestion, as a new registration does).
-- ============================================================================

BEGIN;

ALTER TABLE groupes ADD COLUMN IF NOT EXISTS desktop_gestion_access BOOLEAN NOT NULL DEFAULT TRUE;

UPDATE groupes g
SET desktop_gestion_access = COALESCE(g.gestion_access, FALSE)
WHERE g.last_licence_check_at IS NOT NULL
   OR COALESCE(g.phone_only, FALSE)
   OR EXISTS (SELECT 1 FROM devices d WHERE d.group_id = g.id);

COMMIT;
