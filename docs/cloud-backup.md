# Cloud backup (online mode)

An online-mode shop's sales and stock live in the host's `lonnii.db`. The OCI server holds the
licence **and, since this feature, a recent copy of that data and its photos**. Delete the file
and the shop's history is no longer lost.

It is a backup and restore, **not a live sync**: the host stays the one place the shop writes
to. A snapshot goes up once the shop has been quiet for a minute after a change
(`Lonnii:CloudBackup:QuietSeconds`, 60), and never later than five minutes after the first unsent
change (`Lonnii:CloudBackup:MaxDelayMinutes`, 5). The same five minutes is the retry delay after a
failure and the period of a fallback check for writes made with raw SQL.

What wakes it is `DataChangeSignal` (`Features/Sync/DataChanges.cs`): an EF Core interceptor reports
every saved row - table, key, added/modified/deleted - in one place.

Row-level sync (needed for Expo and several independent caches) is a separate, later piece
(planned within 1-2 years). It plugs into that same signal:
1. An outbox table, written by the interceptor in the same transaction as the change.
2. A sender that pushes the outbox to OCI within seconds, retried until acknowledged.
3. An endpoint on OCI that applies the rows in order to the shop's copy.
4. Snapshots drop to a daily safety copy.

Keys (checked 2026-10-28): the core tables (products, ventes, items, payments, clients,
stock_history, categories, suppliers) already use GUID keys. Caisses, charges, immobilisations,
bilan and privilege tables use integers. Phones will save on OCI directly (and OCI passes the
change on to the shop's host, if there is one), so OCI and the host both create rows. The
integer-keyed tables phones write - caisses, caisse_transactions, charges, charges_categories,
immobilisations, amortissement_echeances - therefore carry a `sync_id` GUID (`ISyncable`, migration
`AddSyncIds`, `db/postgres/021_sync_ids.sql`), added alongside the integer key, not replacing it,
since they mirror live Postgres. Live sync matches these rows by `sync_id`; a link between them
(e.g. `caisse_transactions.caisse_id`) must be translated through the parent's `sync_id`.

## How it works

Host (`CloudBackupService` → `CloudBackupRunner`), for each workspace that is online-mode, has a
licence server and a backup token:

1. `POST /api/backup/begin` – confirms the shop's line of backups (the *epoch*).
2. Builds a snapshot: the espace `.db` archive (same format as Paramètres → Données de l'espace),
   rows only, keys unchanged, photos **listed, not embedded**; gzips it.
3. `POST /api/backup/images/missing`, then `PUT /api/backup/images/{folder}/{name}` for each photo the
   server lacks. Photo names carry a timestamp and are never reused, so a name already present
   is final – each photo travels once, not in every snapshot. Covers products, categories,
   receipt logo, payment QR code and the espace cover photo.
4. `PUT /api/backup/snapshot` – last, so a snapshot never refers to a photo the server lacks.

Skipped when nothing has been written since the last success (database/WAL write time).

Server (`BackupEndpoints`, `BackupStore`): files under `{data}/backups/{groupId}/` – `meta.json`,
`snapshots/` (newest 5 kept, then one per day for 30 days), `images/`. Uploads are written to a
temp file and only replace the current snapshot if they are gzip of a SQLite database.

## Security

- Machine token, not a user login: `HMAC(server key, groupId|deviceId)`, issued at activation and
  with every licence refresh (`BackupToken`). Derived, so no column was added to the live
  `devices` table. Every call re-checks that the device is still bound, the workspace not
  blocked, and the subscription current – revoke a machine or let a subscription lapse and access ends.
- Image names are restricted to `[A-Za-z0-9._-]`, folders to the known list; group ids must be GUIDs.
- **Epoch guard.** The server only accepts writes from a host presenting the current epoch. A freshly
  installed host has none, so its first (empty) backup is refused with 409 and cannot replace the
  real one. The host's epoch is stored in its own database (`cloud_backup_state`), so deleting
  `lonnii.db` also forgets it – exactly the case it must catch. `POST /api/backup/reset` (host:
  `POST /api/parametres/cloud-backup/restart`, Admin Général) sets the old backup aside in
  `{groupId}.retired-<time>` and starts a new epoch.

## Host endpoints (Paramètres)

`GET /api/parametres/cloud-backup` status · `POST …/run` back up now · `POST …/restart` start again.

## PostgreSQL

No script needed: backups are files, and `cloud_backup_state` exists only in each host's SQLite.

## Restore

`CloudRestoreService` (host): reads the newest snapshot's info, downloads it and every photo its rows
point at, then `EspaceTransferService.RestoreAsync` loads it in one transaction and the host adopts
the backup's epoch, so scheduled backups carry on from the restored line.

- **When:** at first launch (after activation, if the server already holds a backup the window
  offers it: `POST /api/setup/restore`, anonymous like `/apply`, single-espace host only) or later
  from Paramètres → Sauvegarde en ligne (`POST /api/parametres/cloud-backup/restore`, Admin Général).
  Answering "no" at first launch keeps the backup; the host's uploads are refused (409) until the
  admin restores or chooses "Repartir de zéro".
- **Only into an empty workspace** (same conflict check as import) and only a backup of that same
  workspace id. All or nothing; photos fetched for a failed restore are removed again.
- **Keys kept:** unlike the .db import, nothing is renumbered, so photos keep their names and rows
  keep their ids.
- **Accounts come back** (`AccountsSnapshot`): users, memberships, roles, the three privilege
  grant tables, password hashes. Setup has just created an admin with a *new* id; that row is
  renumbered to the backed-up admin's id (foreign keys deferred to commit, since deleting it would
  cascade into the groupe), so every sale's `created_by` resolves again. Privilege grants are mapped
  by privilege **name**, not id, because ids differ between installations. The snapshot carries
  no licence fields. Password hashes (BCrypt) are stored on the licence server with the snapshot.
- After a restore the admin signs in with the password from the backup, which is the latest one
  they set - not necessarily the one in the credentials file.

## Not built yet

- Back up right after closing the caisse, not only on the timer.
- Prune photos no snapshot refers to any more.
- Work programme, activity logs and the presence journal are not in the snapshot (as in the .db export).
- Restoring an older snapshot than the newest (support can do it by hand from `snapshots/`).
