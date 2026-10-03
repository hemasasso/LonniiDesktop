using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Parametres;

/// <summary>Where the temporary <c>.db</c> files of an export or an import are built, beside
/// the database rather than in the user's temp folder, so a half-gigabyte export cannot land
/// on a system drive that has no room for it.</summary>
public sealed class EspaceTransferPaths(string dataDirectory)
{
    public string Directory { get; } = System.IO.Path.Combine(dataDirectory, "transfers");

    /// <summary>A fresh path for one transfer. The caller deletes the file when it is done.</summary>
    public string NewFile(string prefix)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return System.IO.Path.Combine(Directory, $"{prefix}-{Guid.NewGuid():N}.db");
    }
}

/// <summary>An export ready to be sent: the file on disk plus what it holds.</summary>
public sealed record EspaceExportResult(
    string FilePath, string FileName, EspaceExportManifestDto Manifest);

/// <summary>
/// The result of an import attempt. Exactly one of the three is set:
/// <see cref="Result"/> on success, <see cref="Conflict"/> when the receiving espace is not
/// empty, <see cref="Error"/> when the file itself cannot be used.
/// </summary>
public sealed record EspaceImportOutcome(
    EspaceImportResultDto? Result = null,
    string? Error = null,
    IReadOnlyList<EspaceTransferSectionDto>? Conflict = null);

/// <summary>
/// "Données de l'espace": downloading a whole espace as a single <c>.db</c> file, images
/// included, and loading that file into another espace.
///
/// <para>
/// The format is a SQLite database rather than a zip of CSVs because the thing being moved is
/// a database: one file that already holds the relationships, the money at full precision and
/// the photos as blobs, and that the same EF model can read straight back. See
/// <see cref="EspaceArchive"/> for its layout and <see cref="EspaceCopier"/> for what travels.
/// </para>
/// <para>
/// Import only ever <em>adds</em> to an empty espace. It refuses outright when the receiving
/// workspace already holds products, sales, charges or any other business data, and says what
/// it found - merging two espaces would mean deciding, row by row, which of two products with
/// the same name is the real one, and no answer to that is safe to guess. Moving data into a
/// workspace in use is therefore a deliberate two-step: create a new espace, then import.
/// </para>
/// </summary>
public sealed class EspaceTransferService(
    LonniiDbContext db,
    ImageStorageService images,
    EspaceTransferPaths paths,
    ILogger<EspaceTransferService> logger)
{
    /// <summary>
    /// Largest archive accepted on upload. Generous on purpose: a supermarket's espace with
    /// years of sales and a photo on every product line is the case this has to carry, and a
    /// refusal at the end of a long upload is the worst possible place to find a limit. It is
    /// still a limit rather than none at all - an unbounded upload is a way to fill the
    /// host's system drive, and the file lands on the drive the database itself lives on.
    /// </summary>
    public const long MaxImportBytes = 5L * 1024 * 1024 * 1024;

    // --- Export ------------------------------------------------------------------

    /// <summary>
    /// Builds the archive for one espace and returns the file to send. The caller is
    /// responsible for deleting it - the endpoint streams it with
    /// <see cref="FileOptions.DeleteOnClose"/>, so it goes as soon as the download ends.
    /// </summary>
    public async Task<EspaceExportResult?> ExportAsync(string groupId, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (groupe is null) return null;

        var path = paths.NewFile("export");

        var manifest = await WriteArchiveAsync(groupId, groupe.Nom, SettingsOf(groupe), path, ct);

        return new EspaceExportResult(path, FileNameFor(groupe.Nom), manifest);
    }

    private async Task<EspaceExportManifestDto> WriteArchiveAsync(
        string groupId, string groupName, EspaceSettings settings, string path, CancellationToken ct)
    {
        EspaceExportManifestDto manifest;

        try
        {
            await using var archive = await EspaceArchive.CreateAsync(path, ct);

            var sink = new ExportImageSink(images, archive);
            // renewIds: false - the archive keeps the espace's own keys, so its rows and the
            // image URLs stored on them still agree with each other.
            var copier = new EspaceCopier(db, archive.Db, groupId, groupId, sink, renewIds: false);

            await copier.RunAsync(ct);

            manifest = new EspaceExportManifestDto(
                FormatVersion: EspaceArchive.FormatVersion,
                ExportedAt: DateTime.UtcNow,
                SourceGroupId: groupId,
                SourceGroupName: groupName,
                RecordCount: copier.RecordCount,
                ImageCount: sink.Count);

            await archive.WriteManifestAsync(manifest, settings, copier.Sections, ct);
        }
        catch
        {
            // Release before deleting: the pooled connection still holds the file otherwise.
            EspaceArchive.ReleaseFiles();
            Discard(path);
            throw;
        }

        // Lets the finished file be streamed and deleted - see EspaceArchive.ReleaseFiles.
        EspaceArchive.ReleaseFiles();

        logger.LogInformation(
            "Export de l'espace {GroupId} : {Records} enregistrements, {Images} images",
            groupId, manifest.RecordCount, manifest.ImageCount);

        return manifest;
    }

    /// <summary>A filename a shop can recognise months later: the espace and the date.</summary>
    private static string FileNameFor(string groupName)
    {
        var safe = new string(groupName
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray())
            .Trim('-');

        if (safe.Length == 0) safe = "espace";
        if (safe.Length > 40) safe = safe[..40].Trim('-');

        return $"lonnii-{safe}-{DateTime.Now:yyyy-MM-dd}.db";
    }

    // --- Import ------------------------------------------------------------------

    /// <summary>
    /// Loads an archive into <paramref name="groupId"/>, which must be an espace with no
    /// business data in it yet. Nothing is written unless the whole import succeeds: the rows
    /// go in one transaction, and the photos written to disk are removed again if it rolls back.
    /// </summary>
    public async Task<EspaceImportOutcome> ImportAsync(string groupId, string archivePath, CancellationToken ct)
    {
        try
        {
            return await ImportCoreAsync(groupId, archivePath, ct);
        }
        finally
        {
            EspaceArchive.ReleaseFiles();
            Discard(archivePath);
        }
    }

    private async Task<EspaceImportOutcome> ImportCoreAsync(
        string groupId, string archivePath, CancellationToken ct)
    {
        if (!await db.Groupes.AnyAsync(g => g.Id == groupId, ct))
            return new EspaceImportOutcome(Error: "Espace introuvable.");

        await using var archive = EspaceArchive.Open(archivePath);

        if (await archive.ReadManifestAsync(ct) is not { } file)
        {
            return new EspaceImportOutcome(
                Error: "Ce fichier n'est pas une sauvegarde d'espace Lonnii, ou il est endommagé.");
        }

        if (file.Manifest.FormatVersion > EspaceArchive.FormatVersion)
        {
            return new EspaceImportOutcome(
                Error: $"Ce fichier a été créé par une version plus récente de Lonnii " +
                       $"(format {file.Manifest.FormatVersion}, cette version lit le format " +
                       $"{EspaceArchive.FormatVersion}). Mettez l'application à jour.");
        }

        if (await FindConflictsAsync(groupId, ct) is { Count: > 0 } conflicts)
            return new EspaceImportOutcome(Conflict: conflicts);

        var sink = new ImportImageSink(images, archive);

        // renewIds: true - the espace being imported into usually lives in the same database
        // as the one the file came from, so every key has to be a new one.
        var copier = new EspaceCopier(
            archive.Db, db, file.Manifest.SourceGroupId, groupId, sink, renewIds: true);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            await ClearReplaceableAsync(groupId, ct);
            await copier.RunAsync(ct);

            // The copier turns change detection off for the bulk inserts and clears the
            // tracker between tables, so the espace's own row is loaded here, after the copy,
            // and updated the ordinary way.
            db.ChangeTracker.AutoDetectChangesEnabled = true;

            var groupe = await db.Groupes.FirstAsync(g => g.Id == groupId, ct);
            Apply(file.Settings, groupe);
            await db.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
        }
        catch (Exception e)
        {
            await transaction.RollbackAsync(ct);

            // The transaction took the rows back; the photos are files on disk and need
            // removing by hand, or a retried import would leave orphans behind.
            sink.Rollback();

            logger.LogError(e, "Import de l'espace {GroupId} abandonné", groupId);

            return new EspaceImportOutcome(
                Error: "L'import a échoué et rien n'a été enregistré. " +
                       "Vérifiez le fichier, puis réessayez.");
        }

        logger.LogInformation(
            "Import dans l'espace {GroupId} depuis « {Source} » : {Records} enregistrements, {Images} images",
            groupId, file.Manifest.SourceGroupName, copier.RecordCount, sink.Count);

        return new EspaceImportOutcome(Result: new EspaceImportResultDto(
            SourceGroupName: file.Manifest.SourceGroupName,
            ExportedAt: file.Manifest.ExportedAt,
            RecordCount: copier.RecordCount,
            ImageCount: sink.Count,
            Sections: copier.Sections));
    }

    /// <summary>
    /// What is already in the receiving espace that an import must not walk over. Empty means
    /// the espace is free to receive one.
    ///
    /// <para>
    /// The seeded defaults are deliberately absent from this list: a brand-new espace whose
    /// owner merely opened the Charges or Bilan screen already holds the twelve default charge
    /// categories and the SYSCOHADA account plan, and so does every espace that has never been
    /// used. Blocking on those would make the feature unusable. They are replaced instead -
    /// see <see cref="ClearReplaceableAsync"/>.
    /// </para>
    /// </summary>
    private async Task<List<EspaceTransferSectionDto>> FindConflictsAsync(string g, CancellationToken ct)
    {
        (string Label, int Count)[] counts =
        [
            ("Produits", await db.Products.CountAsync(x => x.GroupId == g, ct)),
            ("Catégories", await db.Categories.CountAsync(x => x.GroupId == g, ct)),
            ("Fournisseurs", await db.Suppliers.CountAsync(x => x.GroupId == g, ct)),
            ("Historique stock", await db.StockHistories.CountAsync(x => x.GroupId == g, ct)),
            ("Snapshots stock", await db.StockSnapshots.CountAsync(x => x.GroupId == g, ct)),
            ("Ventes", await db.Ventes.CountAsync(x => x.GroupId == g, ct)),
            ("Clients", await db.Clients.CountAsync(x => x.GroupId == g, ct)),
            ("Sessions caisse", await db.Caisses.CountAsync(x => x.GroupId == g, ct)),
            ("Transactions caisse", await db.CaisseTransactions.CountAsync(x => x.GroupId == g, ct)),
            ("Paiements groupés", await db.GroupePayments.CountAsync(x => x.GroupId == g, ct)),
            ("Charges", await db.Charges.CountAsync(x => x.GroupId == g, ct)),
            ("Immobilisations", await db.Immobilisations.CountAsync(x => x.GroupId == g, ct)),
            ("Échéances d'amortissement", await db.AmortissementEcheances.CountAsync(x => x.GroupId == g, ct)),
            ("Écritures comptables", await db.BilanEcritures.CountAsync(x => x.GroupId == g, ct)),
        ];

        return counts
            .Where(c => c.Count > 0)
            .Select(c => new EspaceTransferSectionDto(c.Label, c.Count))
            .ToList();
    }

    /// <summary>
    /// Removes the rows an espace is given for free - the seeded charge categories and
    /// account plans - and its own settings rows, so the archive's versions take their place
    /// instead of colliding with them (<c>charges_categories</c> and
    /// <c>ventes_parametres</c> are both unique per espace).
    /// </summary>
    private async Task ClearReplaceableAsync(string g, CancellationToken ct)
    {
        await db.ChargeCategories.Where(x => x.GroupId == g).ExecuteDeleteAsync(ct);
        await db.BilanComptes.Where(x => x.GroupId == g).ExecuteDeleteAsync(ct);
        await db.ResultatComptes.Where(x => x.GroupId == g).ExecuteDeleteAsync(ct);
        await db.StockSettings.Where(x => x.GroupId == g).ExecuteDeleteAsync(ct);
        await db.ComptabiliteParametres.Where(x => x.GroupId == g).ExecuteDeleteAsync(ct);

        // The row goes, but its logo and QR code files stay: deleting them here would be
        // outside the transaction, so a rollback would restore a settings row pointing at
        // photos that no longer exist. At worst two unreferenced files remain on the host.
        await db.VentesParametres.Where(x => x.GroupeId == g).ExecuteDeleteAsync(ct);
    }

    // --- Espace settings ---------------------------------------------------------

    private static EspaceSettings SettingsOf(Lonnii.Data.Entities.Groupe g) => new(
        CurrencyLabel: g.CurrencyLabel,
        CurrencyBefore: g.CurrencyBefore,
        GestionAccess: g.GestionAccess,
        PrestationsEnabled: g.PrestationsEnabled,
        PrestationsLocation: g.PrestationsLocation);

    private static void Apply(EspaceSettings settings, Lonnii.Data.Entities.Groupe g)
    {
        // The name is not applied: the receiving espace was created and named by the people
        // who will use it, and silently renaming it from a file would be a surprise.
        g.CurrencyLabel = settings.CurrencyLabel;
        g.CurrencyBefore = settings.CurrencyBefore;
        g.GestionAccess = settings.GestionAccess;
        g.PrestationsEnabled = settings.PrestationsEnabled;
        g.PrestationsLocation = settings.PrestationsLocation;
    }

    /// <summary>Deletes a transfer file, ignoring a failure: an undeleted temporary file is
    /// worth a stale file on disk, not a failed request.</summary>
    private void Discard(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "Fichier de transfert non supprimé : {Path}", path);
        }
    }
}
