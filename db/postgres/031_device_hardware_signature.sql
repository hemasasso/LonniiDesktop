-- ============================================================================
-- Lonnii Desktop - a second fingerprint per machine
--
-- Additive only, IF NOT EXISTS, in a transaction - same rules as the others.
--
-- devices.device_id is derived from Windows' MachineGuid alone, so copying that
-- one value onto another computer reuses a machine's binding. This column holds
-- a wider fingerprint (see the client's DeviceIdentity / HardwareSignature): the
-- copy on different hardware produces a different value, which the licence refresh
-- notices and records for the dashboard. Opaque here; the server only compares it.
--
-- After this script, grant the API's role on the column (already covered by the
-- table-wide grant, but a fresh grant does no harm):
--   GRANT SELECT, INSERT, UPDATE, DELETE ON devices TO lonnii_api;
-- ============================================================================

BEGIN;

ALTER TABLE devices ADD COLUMN IF NOT EXISTS hardware_signature VARCHAR(128);

-- A hash of the host's own program files, so a build patched to dodge the licence (same version,
-- different files) is noticed at check-in. Opaque here; the server only compares it.
ALTER TABLE devices ADD COLUMN IF NOT EXISTS build_hash VARCHAR(128);

COMMIT;
