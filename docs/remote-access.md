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
     The caller's account on the server and their account in the shop are two rows with two ids when
     the shop's account was made on the shop's computer, so they are **matched by email** - and only
     when the server has verified that email (403 otherwise). Inside the shop, including in the
     requests the host applies, the person is known by the shop's own id.
   No registered machine is asked for; a phone has none.
3. The token goes in the usual `x-group-session` header. `RemoteRoutingMiddleware` sees the `rs.`
   prefix, points the request's database at the shop's copy, and every existing endpoint answers
   from it unchanged. Each response carries `X-Replica-Updated` (the snapshot time).

## Rules

- **Read-only.** Only GET and HEAD reach the copy; anything else is a 405. The copy is rebuilt from
  the next snapshot, so a write would be lost and would look as if it had worked. The one thing a
  remote administrator may do is ask for a change - see *Remote requests* below.
- **Stateless token**: `rs.{groupId}.{accountId}.{shopUserId}.{expiresUnix}.{hmac}`, 4 hours. The request is routed
  before any database is opened, which a stored random token could not allow.
- **Account and licence checks use the server's own database** (`ControlDb`), never the copy.
- **Copies**: `replicas/{groupId}/{snapshotTime}.db`, unpacked on demand and brought to the current
  schema with the same migrations a host runs, so an older host reads correctly. A newer snapshot is
  a new file; older ones are deleted when they can be.

## Remote requests (privileges, roles and products)

A remote administrator cannot change the copy, but can **ask the shop to make a change**:

1. `POST /api/remote/commands` with `{type: "privilege", userId, privilegeName, catalog, granted}` or
   `{type: "role", userId, role}`, or a product change: `product.create` (`product`), `product.update`
   (`productId` + `product` - details and prices), `product.adjust` (`productId` + `stockAdjustment`, a
   signed quantity with its reason) and `product.deactivate` (`productId`, off sale, history kept).
   Product requests run through the Stock screens' own handlers, with the privilege each screen asks for. The server first checks it against the copy by making the change
   inside a transaction and rolling it back, so it is refused at once with the shop's own reason
   (unknown member, admin-only privilege, only the creator may change admin roles). The copy is never
   changed. A valid request is queued (202) in `remote-commands/{groupId}.json`.
2. The shop's host **asks** OCI every ~10 seconds (`GET /api/backup/commands`, with the machine token it
   already uses for backups - OCI never pushes to a shop). It applies each request with the same code
   as its own screens (`PrivilegeChanges`), writes the audit trail marked "À distance : ", bumps the
   shop's live counter so tills refresh within seconds, reports the outcome
   (`POST /api/backup/commands/{id}/result`) and runs a backup so the copy shows the change soon.
3. `GET /api/remote/commands` lists recent requests (pending / applied / failed / expired) for the screen.

Safety: the host applies a request **only if its sender still holds an administrator role in the shop's own
data** at that moment, so an administrator removed after asking cannot have a queued request carried out.
A request that waits more than 7 days for an offline shop expires. A shop with no internet collects its
requests when it reconnects, so a remote revoke is not instant for an offline shop - the screen shows it as
waiting.

## Known limits

- Data is as old as the last backup (up to 15 minutes).
- Product pictures come from what the host uploaded with its backup; the phone must send the session headers to load them. Receipt logo and QR-code pictures on the phone still use the old uploads path.
- Only accounts known to OCI can sign in: the administrator registered with the shop. Staff accounts
  created later on the host, and a password changed on the host, are not reflected there yet.
