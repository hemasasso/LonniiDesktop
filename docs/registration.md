# Registration (new shops and their Admin Général)

Every new shop and its Admin Général is registered on the licence server (OCI) first, so we
know - and agree to - everyone who uses the app. Nothing is created locally until OCI says yes.

## Flow

1. `POST /api/registration/start` - shop name, email, password, device id. Validates, rate-limits,
   stores the registration (**password hashed, code hashed**) and emails a six-digit code (15 min).
   Nothing else exists yet: no user, no workspace.
2. `POST /api/registration/verify` - request id + code. On success creates the user, the groupe
   (`mode = local`, `approval_status = pending`), the membership and the admin role.
3. **Pending shops cannot activate or refresh** (`403`, "en attente d'approbation"). The message is only
   given after the credentials check out, so it is for the owner, not a probe.
4. You approve in the dashboard: `UPDATE groupes SET approval_status = 'approved' WHERE id = '…'`
   (`'rejected'` declines). Then activation works. To make a shop paid/online, set `mode = 'online'`
   and add a `dashboard_subscriptions` row - the next licence refresh applies it, no reinstall.

Waiting list: see the SELECT in `db/postgres/014_registrations.sql`.

## Protections

- Email confirmation: a code must come back before anything is created.
- Rate limit: 3 requests per address and 10 per network address per hour (in memory).
- 5 wrong codes burn the request; codes expire after 15 minutes.
- Without SMTP configured, registration answers 503 rather than accepting codes nobody receives.
  The log email sender exists only in the Development environment.
- Off by default: `Lonnii:Registration:Enabled=true` is set **only on OCI**. On a shop's own host the
  endpoints answer 404 (they would register into a database we cannot see).

## OCI configuration

`Lonnii__Registration__Enabled=true`, and `Lonnii__Email__Host`, `__Port` (587), `__User`, `__Password`,
`__From`, `__EnableSsl`. Apply `db/postgres/014_registrations.sql` first (existing shops become `approved`).
Behind a reverse proxy, forward the client address (ForwardedHeaders) or the per-address limit will
see only the proxy.

## Desktop (first launch)

Login screen, on a host with no account -> **Créer mon espace…** (`RegistrationWindow`), three steps:

1. Shop name, email, password (with the show/hide eye) -> the code is emailed.
2. The code -> the shop now exists on OCI as `pending`. The host writes `pending-registration.json`
   (group id, server URL, email, shop name - **never the password**) beside the database.
3. Waiting. The window can be closed; reopened days later it resumes here. Once approved, the owner types the
   password again -> the host activates against OCI and builds the local workspace exactly as the
   credentials-file path does, then deletes the pending file.

Host endpoints (anonymous, close once a workspace exists, like `/api/setup/apply`):
`POST /api/setup/register/start|verify|activate`, `GET|DELETE /api/setup/register/pending`.

The host reaches OCI at **`Lonnii:LicenceServerUrl`** (appsettings.json, empty by default). Set it to your OCI
address in the build you ship - until then the screen says to use a credentials file. The credentials
file path still works unchanged.

## Email

Same look, wording and subject as Lonnii Business (`utils/emailTemplates.js`), sent as "Lonnii" <address>.
Lonnii Business uses `mail.privateemail.com` on port 465 (implicit SSL); .NET's `SmtpClient` only does
STARTTLS, so use the **same host on port 587**: `Lonnii__Email__Host=mail.privateemail.com`, `__Port=587`,
`__User`, `__Password`, `__From` (same mailbox as the web app's `EMAIL_USER`), `__EnableSsl=true`.
Code validity is 15 minutes here (3 in the web app) - desktop owners type slower.

## Every espace is registered with Lonnii

- **The first shop** registers through "Créer mon espace…" (above) or a credentials file. Creating the
  first account by hand is **off** on a shipped host: `Lonnii:AllowManualSetup` defaults to false, the
  endpoint answers 403 and the sign-in window hides "Créer un compte manuellement…". It is for
  development: set `Lonnii__AllowManualSetup=true` to get it back (the tests do).
- **Another espace** (Option B, chosen 2026-10-04): the espace picker asks for the owner's password, the
  host calls `POST /api/registration/espace` on OCI, and only on success builds the espace locally
  under the id OCI issued, activated for this machine. OCI accepts it **instantly, but only from the
  Admin Général of an already approved shop** (not pending, blocked or declined), authenticated by email +
  password and rate-limited like sign-up. The espace starts local, approved, on your list at once; block
  or upgrade it from the dashboard.
- Consequence: a *member* who is not the Admin Général of an approved shop can no longer create espaces
  (before, any user could). Members are local accounts and OCI does not know them.
- Switch: `Lonnii:OnlineRegistration:Required` (default true). It only applies when a licence server
  address exists (`Lonnii:LicenceServerUrl`, or the shop's own from its credentials file).
- If OCI answers but the local save then fails, OCI keeps an espace the host does not have - harmless,
  and visible to you.

## Changing a shop afterwards (mode, machines, subscription)

In the web dashboard: **Inscriptions** -> approve with the mode/machines/subscription you choose, or the **Gérer** tab
for a shop that is already approved. The shop picks the change up the next time its host **checks in**:

- Every shop activated against the licence server checks in - **local ones too** (`POST /api/licence/sync`, at
  start-up and every few hours). Before 2026-10-05 local shops never did, so a shop made online in the dashboard stayed
  local for ever on the desktop. The server is the authority for mode, machine allowance and the blocked flag; a shop that
  never asks cannot be told.
- A local shop is never punished for failing to check in: unreachable, or refused by the server, it stays local, shows no
  error and is never locked. Only an online shop has the offline deadline (14 days).
- A shop with no licence server address (the server itself, or a host that was never activated against ours) never calls one.
- When a shop becomes online, the same answer carries its cloud-backup key, so backups start without another step.

## Not built yet

- A shop that already exists on OCI and installs on a new machine still needs its credentials file
  (there is no "sign in to my existing espace" on a blank host).
- The dashboard screens (list pending, approve/reject, set mode/subscription). The SQL above is the interim.
- Notifying you by email when a shop registers.
