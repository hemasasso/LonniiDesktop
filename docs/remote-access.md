# Remote access (online-mode shops)

An administrator of an **online-mode** shop can read the shop's data from a phone or a laptop away
from the shop. The shop's real data stays on its own host computer; what the remote device reads is
the copy the host uploads to OCI as a cloud backup (see `cloud-backup.md`). When the shop's internet
is down the copy simply stops moving, and the screens say how old it is.

## Flow

1. The administrator signs in as usual (`POST /api/auth/login`). The account is the one registered
   on OCI when the shop was created.
2. `POST /api/remote/{groupId}/session` opens a **remote session** and returns a token, the
   shop's settings, and `snapshotAt` - when the copy was taken. Refused unless:
   - the shop is in an online mode with a **current subscription** (402 otherwise),
   - the shop has uploaded at least one snapshot (404),
   - the caller is an **administrator in the shop's own data** (403) - roles are granted and
     removed on the shop's host, so the copy is the authority, not the registration record.
   No registered machine is asked for; a phone has none.
3. The token goes in the usual `x-group-session` header. `RemoteRoutingMiddleware` sees the `rs.`
   prefix, points the request's database at the shop's copy, and every existing endpoint answers
   from it unchanged. Each response carries `X-Replica-Updated` (the snapshot time).

## Rules

- **Read-only.** Only GET and HEAD reach the copy; anything else is a 405. The copy is rebuilt from
  the next snapshot, so a write would be lost and would look as if it had worked. Changes a remote
  administrator may make (privileges, members) are meant to go through a command queue the host
  collects (`/api/remote/commands`, not built yet).
- **Stateless token**: `rs.{groupId}.{userId}.{expiresUnix}.{hmac}`, 4 hours. The request is routed
  before any database is opened, which a stored random token could not allow.
- **Account and licence checks use the server's own database** (`ControlDb`), never the copy.
- **Copies**: `replicas/{groupId}/{snapshotTime}.db`, unpacked on demand and brought to the current
  schema with the same migrations a host runs, so an older host reads correctly. A newer snapshot is
  a new file; older ones are deleted when they can be.

## Known limits

- Data is as old as the last backup (up to 15 minutes).
- Product pictures come from what the host uploaded with its backup; the phone must send the session headers to load them. Receipt logo and QR-code pictures on the phone still use the old uploads path.
- Only accounts known to OCI can sign in: the administrator registered with the shop. Staff accounts
  created later on the host, and a password changed on the host, are not reflected there yet.
