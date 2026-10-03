# Espace data transfer (.db export and import)

An espace can be downloaded as one file and loaded into another espace. **Paramètres →
Données de l'espace** (admin) opens it: *Télécharger* for the file, *Importer* for loading
one back.

The file is an ordinary SQLite database carrying the Lonnii schema, so it opens in any
SQLite tool, plus two tables of its own:

| Table | What it holds |
|---|---|
| `espace_export_manifest` | One row: format version, when it was taken, which espace it came from, how much is in it, and the espace settings to apply on import. |
| `espace_export_images` | Every product photo, category photo, receipt logo and payment QR code, as blobs keyed by the URL the rows point at. |

Photos live on the host's disk, not in the database, so a file without them would restore a
catalogue whose every image was a broken link. `EspaceArchive` writes the format,
`EspaceCopier` copies the rows, `EspaceTransferService` orchestrates both directions.

## What travels

Stock (categories, suppliers, products, movements, snapshots), ventes (sales, line items,
payments, grouped payments, caisse sessions and their transactions, clients, the reçu and
facture settings), charges and their categories, comptabilité (immobilisations and their
schedules, both account plans, écritures, settings), and the espace's own currency label and
position plus the Gestion/Prestations toggles.

## What does not

| Left out | Why |
|---|---|
| Members, passwords, privileges | Accounts belong to a server, not to a file. Add the members of the new espace from **Membres et privilèges**. |
| Work programme, activity logs, presence journal | Every row names a member who is not in the receiving espace. |
| Authorised machines, licence settings, blocked/deleted flags | A shop that could restore `max_devices` or the licence server from a file it holds could grant itself machines. |
| The espace name | The receiving espace was named by the people who will use it. |

Sales, caisse sessions and charges keep the user id that created them, so the history stays
intact — the name behind it may simply no longer resolve in the new espace.

## The rules import enforces

- **Admin Général only.** Export is admin, like the rest of Paramètres; import writes a
  whole espace in one go from a file that came from outside.
- **The receiving espace must be empty.** Any product, sale, charge, client, caisse session,
  immobilisation or écriture already there and the import is refused with a 409 naming what
  it found. Merging two espaces would mean deciding, row by row, which of two products with
  the same name is the real one. The sequence is: create a new espace, open it, import.
  The seeded defaults every new espace is given — the twelve charge categories, the
  SYSCOHADA account plans — are not treated as data; they are replaced.
- **All or nothing.** The rows go in one transaction, and photos already written to disk are
  deleted again if it rolls back.
- **Every key is renumbered.** The usual import is into a new espace *in the same database*
  as the one the file came from, so a copy that kept its keys would collide on the first
  product. Integer keys are renumbered in both directions anyway, since the database assigns
  them. Everything pointing at a renumbered key is rewritten with it, down to the facture ids
  inside `groupe_payments.factures_data`.
- **A file from a newer format is refused** rather than read half-right.

## Limits and sizes

Upload is capped at **5 GB** (`EspaceTransferService.MaxImportBytes`); export is uncapped,
since it can only ever be as large as the espace already is. Nothing is held in memory in
either direction: both sides stream in 1 MB chunks, and the client's dialog reports the bytes
as they move — at these sizes a transfer can run for the better part of an hour, and a
progress bar with nothing behind it is indistinguishable from a hang.

Both directions are built as a file under `C:\ProgramData\Lonnii\transfers\` and deleted when
the transfer ends; the download is streamed with `DeleteOnClose`, so it goes as soon as the
client finishes reading it. **Plan the host's free space accordingly:** an import of an *n*-byte
archive needs roughly *2n* free on the data drive — the uploaded copy under `transfers\`,
plus the rows and photos it then writes.

The client sends transfers through a separate `HttpClient` with a three-hour timeout: the
ordinary one gives up after twenty seconds, which is right for a till waiting on a price and
useless for a database crossing a shop's Wi-Fi. Kestrel's own 30 MB body limit is raised per
request on the import endpoint only.
