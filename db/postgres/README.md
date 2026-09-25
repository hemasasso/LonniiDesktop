# PostgreSQL schema changes

Scripts here are applied **by hand, after reading them**, to the live database that
Lonnii Business and Lonnii Desktop share.

## Why these are not EF migrations

The migrations in `src/Lonnii.Data/Migrations` were authored against SQLite. Replaying
them here would create money columns as `INTEGER`, because SQLite has no decimal type and
the model converts amounts to integer minor units for it. This database already stores
those columns as `NUMERIC(15,2)`. Running `dotnet ef migrations script` against it would
therefore corrupt the schema — verified, not assumed.

The runtime model is correct on both providers: `LonniiDbContext` applies the money
converter only under SQLite (`MoneyStorageTests` pins this). It is the migration *files*
that are a SQLite artefact.

The API enforces the same rule at startup: `DatabaseOptions.AllowsAutomaticMigration` is
false on PostgreSQL, so it never migrates this database even if someone points it here.

## Rules for every script in this folder

- **Additive only.** `ADD COLUMN`, `CREATE INDEX`. Never `RENAME`, `DROP` or `ALTER TYPE` —
  Lonnii Business is live against these tables.
- Every added column is **nullable or has a DEFAULT**, so existing rows stay valid.
- `IF NOT EXISTS` throughout, so a script can be re-run safely.
- Wrapped in `BEGIN`/`COMMIT`.
- Numbered, and applied in order.

## Applying one

```bash
psql -h <host> -p 5432 -U <user> -d <database> -f 001_groupes_desktop_columns.sql
```

## Status

| Script | Covers | Verified against live schema |
|---|---|---|
| `001_groupes_desktop_columns.sql` | `groupes`: mode, max_devices, currency_label, prestations_location | yes, 2026-09-24 |
| `002_desktop_columns.sql` | `users.password_changed_at`, `categories.image_url`, `products`: stock_illimite, unite_affichage, deleted_at, deleted_by | yes, 2026-09-24 |
| `006_ventes_parametres.sql` | `ventes_parametres`: the whole table and every receipt/facture column | no — see below |

## `ventes_parametres` is Lonnii Business's, not ours

`006` adds nothing the web app invented: every column in it comes from one of
`create_ventes_parametres_table.sql`, `add_facture_text_columns.sql`,
`add_receipt_footer_text.sql`, `add_avoir_notice_columns.sql` or
`add_font_config_columns.sql` in the source repo, with the same DEFAULTs. It exists so the
desktop can be pointed at a database where some of those were never applied — the same
situation the `ventes` section above describes. Running it against a database that already
has them is a no-op.

Its columns have **not** been compared against the live schema yet. Do that before the API
is pointed at production, the same way `groupes` was.

The model deliberately does not map the table's `id`: it keys on `groupe_id`, which is
UNIQUE and the only column anything looks a row up by, so an INSERT omits `id` and the
sequence default supplies it.

The desktop stores the logo and QR code through `ImageStorageService`, so `logo_path` and
`qr_code_path` hold an `/api/images/...` URL rather than the web app's static
`/uploads/gestion/...` path. Both are a URL the client resolves against its own server, so
the column is genuinely shared — but a shop that configured its logo in Lonnii Business will
have a path the desktop cannot fetch, and will need to upload it again here.

## `ventes` needed no script

The live `ventes` table is the older `gestion_stock_schema.sql` shape with English column
names, not the French `setup_ventes_tables.sql` the entity was modelled from — that file
was never applied to production. Lonnii Business hides this by aliasing in SQL
(`v.sale_number as numero_vente`).

Rather than add French duplicates of columns that already exist, the model now maps onto
the live names (see `ColumnNames` in `LonniiDbContext`), and the SQLite schema was renamed
to match so both sides hold identically named columns.

`montant_paye` and `montant_restant` were **dropped** rather than added: no such columns
exist live, because Lonnii Business computes both from `SUM(paiements_ventes.montant)`.
They are now derived in the model for the same reason — storing a copy would give one
number two sources of truth, and a sale recorded by the web app would read back wrong in
the desktop.

`dashboard_subscriptions` needed **no** script: the live table already has every column the
model reads. The model was corrected to match it instead — its primary key is an
auto-incrementing `integer`, not the GUID string that was inferred from route code.

## Not yet verified

Only `groupes` and `dashboard_subscriptions` have been checked against the live schema.
The desktop model also expects columns on other tables that may not exist there yet —
`categories.image_url`, `products.sales_mode`, and several on `users` among them. Those
need the same column-by-column comparison before the API is pointed at this database.
