namespace Lonnii.Api.Services;

/// <summary>
/// Which database this instance of the API is talking to.
///
/// <para>
/// The same build runs in both deployments: on a shop's host laptop against SQLite, and on
/// the OCI server against PostgreSQL. Nothing but the configuration differs, which is why
/// there is no second project and no compile-time switch.
/// </para>
/// <para>
/// Read from the <c>Lonnii:Database</c> section, e.g. the environment variables
/// <c>Lonnii__Database__Provider</c> and <c>Lonnii__Database__ConnectionString</c>.
/// Leaving it unset keeps the local-mode default, so an existing install is unaffected.
/// </para>
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Lonnii:Database";

    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";

    /// <summary><see cref="Sqlite"/> or <see cref="Postgres"/>. Defaults to SQLite.</summary>
    public string Provider { get; set; } = Sqlite;

    /// <summary>
    /// Required for PostgreSQL. Ignored for SQLite, whose file location is derived from the
    /// data directory so a host laptop needs no connection string at all.
    /// </summary>
    public string? ConnectionString { get; set; }

    public bool IsPostgres =>
        string.Equals(Provider, Postgres, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this instance may apply EF migrations at start-up.
    ///
    /// <para>
    /// SQLite only, and deliberately so. The PostgreSQL database holds live customer data
    /// that Lonnii Business also writes to, and its schema was built by hand-written SQL
    /// rather than by these migrations - several tables exist there in shapes EF does not
    /// know. Letting EF migrate it on start-up would try to create tables that already
    /// exist and alter columns other software depends on.
    /// </para>
    /// <para>
    /// Nor can the schema be produced by <c>dotnet ef migrations script</c>: the migrations
    /// were authored against SQLite, so they carry the minor-units money conversion and
    /// emit <c>montant INTEGER</c> where the live schema has DECIMAL(15,2). The runtime
    /// model is right on both providers - see MoneyStorageTests - but the migration files
    /// are a SQLite artefact. PostgreSQL schema changes are hand-written additive SQL,
    /// reviewed against a dump of the live schema.
    /// </para>
    /// </summary>
    public bool AllowsAutomaticMigration => !IsPostgres;

    /// <summary>Throws when the configuration cannot produce a usable connection.</summary>
    public void Validate()
    {
        if (!IsPostgres && !string.Equals(Provider, Sqlite, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Fournisseur de base de données inconnu : '{Provider}'. Utilisez '{Sqlite}' ou '{Postgres}'.");
        }

        if (IsPostgres && string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"Le mode PostgreSQL exige {SectionName}:ConnectionString.");
        }
    }
}
