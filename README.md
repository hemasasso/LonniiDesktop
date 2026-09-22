# Lonnii Desktop

A Windows desktop version of Lonnii Business, for a company that runs on its own local
network with no internet dependency. One laptop acts as the host: it holds the database
and runs the API. Every other machine runs the same WPF client and talks to the host
over the LAN.

The roles, permissions, menus and options are ported from the existing Lonnii Business
web app (Node/Express + PostgreSQL + React). Column and privilege names are kept
identical so data can later be imported from it without translation.

## Layout

| Project | What it is |
| --- | --- |
| `src/Lonnii.Shared` | Privilege catalogue, menu definitions and API contracts. No dependencies. |
| `src/Lonnii.Data` | EF Core entities, `LonniiDbContext`, migrations, and the privilege resolver. |
| `src/Lonnii.Api` | ASP.NET Core minimal API. Runs on the host laptop only. |
| `src/Lonnii.Client` | WPF client. Runs on the host and on every other machine. |
| `tests/Lonnii.Tests` | Tests for the privilege rules and the catalogue. |

## Running it

Start the API on the host laptop:

```
dotnet run --project src/Lonnii.Api
```

It listens on port 5280 on every network interface, creates its SQLite database on first
run, applies migrations and seeds the privilege catalogue. Then start the client:

```
dotnet run --project src/Lonnii.Client
```

On the host, the sign-in window's server field stays at `localhost:5280`. On another
machine, set it to the host's LAN address, for example `192.168.1.12:5280`. The
**Tester** button confirms the host is reachable before you type a password.

## Creating accounts

No account is seeded, and none is created with a password anybody could guess.

**The first account.** On a host with an empty database the sign-in window shows a panel
saying so, with a **Créer le compte administrateur…** button. Fill in an email and a
password of at least 8 characters. That account becomes Admin Général of whichever
workspace it then creates, which is what grants it every privilege.

**Everyone else.** Self-service registration closes as soon as that first account exists.
Afterwards an administrator adds people from **Options → Ajouter un membre**, choosing
either:

- *Créer un nouveau compte* — email, password and optional name, for someone who has
  never used Lonnii. **Générer** suggests a password that avoids characters that are
  ambiguous when read aloud (no `O` against `0`, no `l` against `1`). The password is
  shown once, after the account is created, for you to pass on.
- *Ajouter un compte existant* — for someone who already has an account on this server.

Either way the new member lands with the role `member` and the baseline access
(Programme, Chat, Formulaire), and nothing in Gestion until you grant it from
**Gérer les privilèges…**.

Lonnii Business lets anyone register because it sits on the public internet behind email
verification. Here every machine on the office network can reach the API and there is no
email to verify against, so leaving registration open would let any device on the LAN
mint accounts. To restore the web app's behaviour, drop the `db.Users.AnyAsync` guard at
the top of `RegisterAsync` in `src/Lonnii.Api/Endpoints/AuthEndpoints.cs`.

### Changing a password

There is no password reset by email, so there are two ways in:

- **Your own:** *Fichier → Changer mon mot de passe…*, available to everyone including
  members, who have no Options screen to reach it from. The current password is required.
- **Someone else's:** *Options → Mot de passe…* with a member selected. No current
  password needed — the point is that nobody knows it any more. The new one is shown once
  for you to pass on.

Either way the change takes effect immediately: the old password stops working, the
person's open group sessions are deleted, and any access token issued before the change
is refused. That last part is why `users.password_changed_at` exists — without it a
reset would leave the old token working for up to twelve hours, which is useless for the
case the feature exists to handle.

Two guards stop a reset becoming a way to climb: the group creator's password can only be
changed by the creator, and a `sub_admin` cannot reset another administrator's password.

Keep the first account's password somewhere safe — nobody can reset it but its owner.

### Windows Firewall

The first time the API starts, Windows asks whether to allow it on private networks. Say
yes, or no client but the host itself will be able to connect. Allow it on **private**
networks only — this API has no business being reachable from a public one.

### "Le port 5280 est déjà utilisé"

Only one copy of the API can run at a time. This usually means one is already running,
often started by Visual Studio. Close it, or set `Lonnii__Port` to something else. The
`Lonnii.Api (bac à sable)` launch profile does exactly that: port 5281 and a throwaway
database under `.lonnii-dev`, so you can debug without touching the real one.

## Where the data lives

| What | Where |
| --- | --- |
| Database | `C:\ProgramData\Lonnii\lonnii.db` on the host |
| JWT signing key | `C:\ProgramData\Lonnii\jwt.key`, generated on first run |
| Client settings | `%APPDATA%\Lonnii\client-settings.json` per Windows user |
| Client error log | `%APPDATA%\Lonnii\client-errors.log` |

Nothing is stored on the client machines except the remembered host address and the last
username. Back up `C:\ProgramData\Lonnii\` and you have backed up the business.

Override the location with `Lonnii:DataDirectory` in configuration or the
`Lonnii__DataDirectory` environment variable — useful for running a throwaway instance
during development.

## The permission model

Lonnii Business has three parallel permission systems, and all three are ported:

1. **Core group privileges** — `privileges`, `user_roles`, `role_privileges`.
   Roles are `admin`, `sub_admin`, `moderator`, `member`.
2. **Option privileges** — `option_privileges`, for Programme, Chat and Formulaire.
3. **Gestion privileges** — `gestion_privileges`, for Stock, Ventes, Charges, Marges,
   Amortissement, Bilan, Prestations, Finance, Analytics and Admin.

`PrivilegeResolver` reproduces the web app's resolution rules exactly, including the ones
that look like quirks. They are quirks on purpose — changing them would make the desktop
and the web app disagree about who can do what:

- The **group creator** (`groupes.iduser_admin`, "Admin Général") gets every privilege
  without any grant row being consulted.
- **Option privileges come only from individual grants.** The web app's `PrivilegeService`
  reads `option_user_privileges` and never consults `role_privileges`, so holding a role
  grants nothing by itself.
- A privilege marked **`is_admin_only` resolves to false** for anyone who is not the
  creator, even if a grant row exists. The API refuses to create such a grant.
- **`can_view_audit` and `can_view_parametres` are not rows in any table.** The web app
  synthesises them for any admin role, and so does this port.

The client never decides what to show on its own: `GET /api/privileges/menu` returns the
menu already filtered, so the shell cannot offer something the API would then refuse.

### Two deliberate departures from the web app

**Options is admin-only here.** Lonnii Business shows the Options card to every member.
That screen exists to manage members and privileges, all of which an ordinary member can
only look at, so the desktop hides the entry rather than advertising a door that does not
open. Members change their own password from the Fichier menu instead.

**Roles are stored in English and displayed in French.** The stored values stay `admin`,
`sub_admin`, `moderator` and `member`, matching Lonnii Business so the future importer
needs no translation. `GroupRoles.DisplayName` turns them into *Administrateur*,
*Administrateur délégué*, *Modérateur* and *Membre* for the screen, and the group creator
is shown as *Administrateur Général*.

## Two things worth knowing about the schema

**Money is stored as integer minor units.** SQLite has no decimal type, and EF Core's
default is to store `decimal` as TEXT, which makes SQL-side `ORDER BY` and `SUM` either
wrong or lossy. Every `decimal` is converted to `value * 100` and stored as INTEGER. The
rule that follows: never write LINQ that multiplies two converted columns together in the
database — the result would be scaled twice. Multiply in C# and store the computed total,
which is what the schema does anyway.

**`ventes` is the current definition, not the old one.** Lonnii Business defines `ventes`
twice: a single-product version in `gestion_stock_schema.sql`, and the current multi-item
version in `setup_ventes_tables.sql` with `ventes_items`, `paiements_ventes` and
`caisses`. This port follows the latter.

## What is built so far

Working end to end: authentication, workspaces and group sessions, the full three-system
permission engine, member management, privilege granting with audit trails, the
privilege-filtered menu, Gestion de Stock (products, categories, suppliers, stock
movements with history) and Paramètres.

Every other menu entry appears for users whose privileges allow it and opens a screen
saying the module is still being built. The navigation is already correct; the screens
behind them are the remaining work.

## Still to do

- Ventes and Caisse, Charges, Marges, Amortissement, Bilan, Prestations, Audit
- Programme (calendar), Chat, Formulaire (form builder)
- The PostgreSQL-to-SQLite importer for existing Lonnii Business data
- Backups
- Machine-locked licensing with RSA-signed keys
