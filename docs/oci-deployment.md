# Running the .NET API on the OCI server

The API is the licence server: registration, activation, device limits, subscriptions, the offline
deadline and the cloud backups. It runs beside the existing Node/React app on the same machine, on its
own subdomain and its own port, against the same PostgreSQL. This file is the record of how.

Server as found 2026-10-04: Oracle Linux 8.10, **aarch64**, nginx 1.24 on 80/443, Node under PM2 on
3000, PostgreSQL 16 on the same machine, SELinux disabled.

## Package

Self-contained, so the server needs no .NET install:

```
dotnet publish src/Lonnii.Api/Lonnii.Api.csproj -c <config starting with Debug> -r linux-arm64 --self-contained true -o <dir>
```

`Debug`-prefixed because SixLabors.ImageSharp 4.x refuses a Release build without a licence
(`SixLaborsLicenseKey`). **Obtain the licence (or replace the library) before real customers run this.**

Installed under `/opt/lonnii-api`, run by a `lonnii` system user, data in `/var/lib/lonnii-api`
(holds `jwt.key` and `backups/` - **include it in server backups**; backup tokens derive from the key).

## Settings (`/etc/lonnii-api.env`, mode 600)

```
ASPNETCORE_ENVIRONMENT=Production
Lonnii__Port=5280
Lonnii__BindAddress=127.0.0.1          # only the reverse proxy may reach it
Lonnii__BehindProxy=true               # trust X-Forwarded-For from the local proxy
Lonnii__DataDirectory=/var/lib/lonnii-api
Lonnii__Discovery__Enabled=false       # LAN discovery is for shops' own hosts, not the server
Lonnii__Database__Provider=postgres
Lonnii__Database__ConnectionString=Host=localhost;Port=5432;Database=<db>;Username=lonnii_api;Password=<random>
Lonnii__Registration__Enabled=true     # OCI only
Lonnii__Email__Host=mail.privateemail.com
Lonnii__Email__Port=587
Lonnii__Email__EnableSsl=true
Lonnii__Email__User=<mailbox>
Lonnii__Email__Password=<mailbox password>
Lonnii__Email__From=<mailbox>
```

`Lonnii:LicenceServerUrl` stays **empty** here: this instance *is* the licence server and is never locked
by its own deadline.

## Staging first (never test against the live database)

1. Copy the live database (read-only, no locks):
   `pg_dump -Fc` with the **version 16** tools (`/usr/pgsql-16/bin`; the default `pg_dump` is 15 and
   refuses), `pg_restore --no-owner --no-privileges` into `lonnii_staging`; delete the dump file after.
2. Give the API its own login, not the React app's superuser:
   `CREATE ROLE lonnii_api LOGIN; \password lonnii_api` then `GRANT USAGE ON SCHEMA public`,
   `GRANT ALL ON ALL TABLES/SEQUENCES IN SCHEMA public` (on staging; for live, narrower).
3. Apply `db/postgres/001`..`015` to staging in order (`psql -v ON_ERROR_STOP=1 -f`). None of 001-013 had
   been applied to the live database.
4. Run `schema_check.sql` (generated from the model's own column list): it lists columns the model
   expects that the database lacks. Only the tables the OCI API reads matter: `groupes`, `users`,
   `devices`, `dashboard_subscriptions`, `groupe_membres`, `user_roles`, `password_history`,
   `groupe_sessions`, `registration_requests`.
5. Start the API by hand against staging; `curl http://127.0.0.1:5280/api/health`; probe
   `/api/activation` with a wrong password (expect 403, not 500).
6. Only then repeat the scripts on the live database and switch the connection string.

Findings from step 4 on 2026-10-04: `users.first_name/last_name` do not exist live (it is
`firstname/lastname`), so the model was mapped onto them; `groupes.photo_url` had no script (`015`).
The remaining gaps are desktop-only tables/columns (`clients`, `caisses`, `programme_*`...) the OCI API
never reads.

## Draft for the next stage (not yet applied)

`/etc/systemd/system/lonnii-api.service`:

```
[Unit]
Description=Lonnii API (licence server)
After=network.target postgresql-16.service

[Service]
User=lonnii
WorkingDirectory=/opt/lonnii-api
ExecStart=/opt/lonnii-api/Lonnii.Api
EnvironmentFile=/etc/lonnii-api.env
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

nginx - a **new** server block for the API's own subdomain; the existing site's block is not edited, and
`nginx -t` must pass before `systemctl reload nginx`:

```
server {
    listen 443 ssl;
    server_name api.<domain>;
    # ssl_certificate / ssl_certificate_key: a certificate for api.<domain>
    client_max_body_size 2g;               # cloud backup snapshots
    proxy_request_buffering off;           # stream uploads instead of buffering them to disk
    location / {
        proxy_pass http://127.0.0.1:5280;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 3600s;
    }
}
```

## Things noticed

- PostgreSQL listens on `0.0.0.0:5432` and the React app's `.env` reaches it over the public IP. Restrict
  it (OCI security list / firewall to known IPs, or `listen_addresses = 'localhost'` plus an SSH tunnel).
- The React app connects as a PostgreSQL **superuser**. Give it its own limited login later.

## Cloudflare

`www.lonnii.com` sits behind Cloudflare, so nginx's `$remote_addr` is a Cloudflare address shared by every
visitor. `deploy/oci/enable-nginx-routes.sh` therefore forwards `CF-Connecting-IP` as `X-Forwarded-For`
(overwritten, never appended); without it the per-address sign-up limit would count everyone as one.
Cloudflare's free plan also caps a single upload at about 100 MB, so a very large cloud-backup snapshot
could be refused at its edge. Fix if it happens: give the API its own sub-domain that bypasses Cloudflare.

## Scripts

`deploy/oci/install-api-service.sh` (systemd unit) and `deploy/oci/enable-nginx-routes.sh` (nginx routes,
with backup, `nginx -t` and automatic restore on failure). Both are safe to run again.

## Going live: `deploy/oci/prepare-live-database.sh`

The first schema check on a copy of the live database (2026-10-04) showed the live database had **none of
`001`-`013` or `015`** (only `014`): no `devices` table, eleven missing `groupes` columns. The script
backs the live database up, applies `001`..`015` (each in a transaction, stopping at the first error),
then grants the API's login `lonnii_api` read/insert/update/delete **after** the scripts so it also covers
the tables they create (granting before is what broke `devices` on staging), and finally lists any column the
model still lacks. It does not switch the API: that is one line in `/etc/lonnii-api.env`.

Found while registering a shop against the copy, all fixed in the code:
`password_history` has a foreign key to `users` (insert order matters - the model does not describe it);
`user_roles.role` is the enum `role_type` (written with an explicit cast on PostgreSQL only); and at start-up
the API must not touch PostgreSQL at all (no privilege seeding, no session purge).

## Backups and monitoring (2026-10-06)

- **Nightly** (`deploy/oci/lonnii-backup.sh`, cron 02:15 UTC): dump of `lonnii_DaB_43` as a **read-only** role
  (`backup_ro`, member of `pg_read_all_data`, password in `/root/.pgpass`), verified with `pg_restore --list`, plus
  `jwt.key`; 14 days kept in `/var/backups/lonnii` (mode 700). Same disk as the database, so it protects against a
  mistake, not against losing the server.
- **Off-server:** Oracle Cloud boot-volume backup policy (console: boot volume -> Backup Policy), plus a copy pulled
  to another machine now and then. The customers' cloud backups (`/var/lib/lonnii-api/backups`) are covered by the
  boot-volume backup, not by the nightly script.
- **Restore test:** `pg_restore -d <scratch db> /var/backups/lonnii/db-<stamp>.dump` into an empty database; an
  untested backup is a guess.
- **aaPanel's security scan** (2026-10-06) is a generic checklist: ignore TCP Wrappers (not in Oracle Linux 8), SSH
  password-change interval (SSH passwords are off), SSH idle timeout (set in sshd_config), empty-password sudo
  (only Oracle's cloud agent, limited commands - needed), and do not use "One-click repair".
