using System.Data.Common;
using System.Text.Json;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Parametres;

/// <summary>
/// The espace settings carried alongside the rows - what the workspace itself is configured
/// to do, as opposed to what it contains. Applied to the receiving workspace on import so a
/// restored espace prices and prints like the one it came from.
/// </summary>
/// <remarks>
/// Deliberately narrow: licensing (<c>max_devices</c>, <c>max_offline_days</c>, the licence
/// server URL) and the blocked/deleted flags are left out. A shop that could restore those
/// from a file it holds could grant itself machines or unblock itself.
/// </remarks>
internal sealed record EspaceSettings(
    string CurrencyLabel,
    bool CurrencyBefore,
    bool GestionAccess,
    bool PrestationsEnabled,
    string? PrestationsLocation);

/// <summary>
/// An exported espace: an ordinary SQLite database carrying the Lonnii schema - so it opens
/// in any SQLite tool and can be read back by the same EF model that wrote it - plus two
/// tables of its own.
///
/// <list type="bullet">
/// <item><c>espace_export_manifest</c>: one row saying which espace this is, when it was
/// taken, how much is in it, and the espace settings to apply on import.</item>
/// <item><c>espace_export_images</c>: the product photos, category photos, receipt logo and
/// payment QR code as blobs, keyed by the URL the rows point at. Images live on the host's
/// disk, not in the database, so a file that did not carry them would restore a catalogue of
/// products whose photos were all broken links.</item>
/// </list>
///
/// Those two tables are created by hand rather than added to the EF model: they belong to
/// the transfer format, not to a running workspace, and nothing should ever create them in a
/// shop's live database.
/// </summary>
internal sealed class EspaceArchive : IAsyncDisposable
{
    /// <summary>
    /// Bumped whenever the layout of the file changes. Import refuses a file from a newer
    /// version rather than reading it half-right.
    /// </summary>
    public const int FormatVersion = 1;

    private const string ManifestTable = "espace_export_manifest";
    private const string ImagesTable = "espace_export_images";

    private EspaceArchive(LonniiDbContext db, string path)
    {
        Db = db;
        Path = path;
    }

    /// <summary>The archive's own database, on the same EF model as a live workspace.</summary>
    public LonniiDbContext Db { get; }

    public string Path { get; }

    /// <summary>Creates an empty archive at <paramref name="path"/>, schema and all.</summary>
    public static async Task<EspaceArchive> CreateAsync(string path, CancellationToken ct)
    {
        var archive = new EspaceArchive(OpenContext(path), path);

        // The same migrations a host laptop runs, so the file holds exactly the schema the
        // model expects - which is what lets the import side read it with the same entities.
        await archive.Db.Database.MigrateAsync(ct);

        await archive.ExecuteAsync(
            $"""
            CREATE TABLE {ManifestTable} (
                format_version   INTEGER NOT NULL,
                exported_at      TEXT    NOT NULL,
                source_group_id  TEXT    NOT NULL,
                source_group_name TEXT   NOT NULL,
                record_count     INTEGER NOT NULL,
                image_count      INTEGER NOT NULL,
                settings_json    TEXT    NOT NULL,
                sections_json    TEXT    NOT NULL
            );
            CREATE TABLE {ImagesTable} (
                url     TEXT PRIMARY KEY,
                folder  TEXT NOT NULL,
                content BLOB NOT NULL
            );
            """, ct);

        return archive;
    }

    /// <summary>Opens a file someone uploaded. Nothing is trusted until
    /// <see cref="ReadManifestAsync"/> has answered.</summary>
    public static EspaceArchive Open(string path) => new(OpenContext(path), path);

    private static LonniiDbContext OpenContext(string path)
    {
        var options = new DbContextOptionsBuilder<LonniiDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        return new LonniiDbContext(options);
    }

    /// <summary>
    /// Lets the file be moved or deleted after the context is disposed. Microsoft.Data.Sqlite
    /// pools connections, so without this the handle on a temporary export survives the
    /// using-block and the delete fails on Windows.
    /// </summary>
    public static void ReleaseFiles() => SqliteConnection.ClearAllPools();

    public async Task WriteManifestAsync(
        EspaceExportManifestDto manifest,
        EspaceSettings settings,
        IReadOnlyList<EspaceTransferSectionDto> sections,
        CancellationToken ct)
    {
        await using var command = Db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO {ManifestTable}
                (format_version, exported_at, source_group_id, source_group_name,
                 record_count, image_count, settings_json, sections_json)
            VALUES ($version, $exportedAt, $groupId, $groupName, $records, $images, $settings, $sections);
            """;

        Add(command, "$version", manifest.FormatVersion);
        Add(command, "$exportedAt", manifest.ExportedAt.ToString("O"));
        Add(command, "$groupId", manifest.SourceGroupId);
        Add(command, "$groupName", manifest.SourceGroupName);
        Add(command, "$records", manifest.RecordCount);
        Add(command, "$images", manifest.ImageCount);
        Add(command, "$settings", JsonSerializer.Serialize(settings));
        Add(command, "$sections", JsonSerializer.Serialize(sections));

        await OpenConnectionAsync(ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The archive's own description of itself, or null when this is not a Lonnii export at
    /// all - a renamed photo, a shop's live <c>lonnii.db</c> copied by hand, or a corrupted
    /// download. The three cases are not distinguished: none of them can be imported.
    /// </summary>
    public async Task<(EspaceExportManifestDto Manifest, EspaceSettings Settings,
        IReadOnlyList<EspaceTransferSectionDto> Sections)?> ReadManifestAsync(CancellationToken ct)
    {
        try
        {
            await OpenConnectionAsync(ct);

            await using var command = Db.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                $"""
                SELECT format_version, exported_at, source_group_id, source_group_name,
                       record_count, image_count, settings_json, sections_json
                FROM {ManifestTable} LIMIT 1;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            var settings = JsonSerializer.Deserialize<EspaceSettings>(reader.GetString(6));
            var sections = JsonSerializer.Deserialize<List<EspaceTransferSectionDto>>(reader.GetString(7));
            if (settings is null || sections is null) return null;

            var manifest = new EspaceExportManifestDto(
                FormatVersion: reader.GetInt32(0),
                ExportedAt: DateTime.Parse(reader.GetString(1), null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                SourceGroupId: reader.GetString(2),
                SourceGroupName: reader.GetString(3),
                RecordCount: reader.GetInt32(4),
                ImageCount: reader.GetInt32(5));

            return (manifest, settings, sections);
        }
        catch (Exception e) when (e is SqliteException or JsonException or FormatException)
        {
            // Not a readable archive. The caller turns this into one plain refusal.
            return null;
        }
    }

    /// <summary>Stores an image's bytes under the URL the exported rows still point at.</summary>
    public async Task PutImageAsync(string url, string folder, byte[] content, CancellationToken ct)
    {
        await using var command = Db.Database.GetDbConnection().CreateCommand();

        // The same photo can be shared by two rows only if something went wrong, but OR
        // REPLACE keeps that from failing an otherwise good export.
        command.CommandText =
            $"INSERT OR REPLACE INTO {ImagesTable} (url, folder, content) VALUES ($url, $folder, $content);";

        Add(command, "$url", url);
        Add(command, "$folder", folder);
        Add(command, "$content", content);

        await OpenConnectionAsync(ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The bytes stored for a URL, or null when the archive does not carry them.</summary>
    public async Task<byte[]?> GetImageAsync(string url, CancellationToken ct)
    {
        await using var command = Db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT content FROM {ImagesTable} WHERE url = $url;";
        Add(command, "$url", url);

        await OpenConnectionAsync(ct);
        var value = await command.ExecuteScalarAsync(ct);
        return value as byte[];
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await Db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    private async Task OpenConnectionAsync(CancellationToken ct)
    {
        var connection = Db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
