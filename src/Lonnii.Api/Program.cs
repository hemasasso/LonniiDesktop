using Microsoft.AspNetCore.Connections;
using System.Net.Sockets;
using Lonnii.Data;
using Lonnii.Data.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

// Note: issuing a customer's credentials file lives in tools/Lonnii.Setup, not here.
// Local-mode customers receive this program, so a licence generator inside it would let a
// shop grant itself any number of machines.

// The live PostgreSQL holds Lonnii Business's plain "timestamp" columns, written by Node with no
// time zone attached. Npgsql 6+ refuses to write a DateTime whose Kind is Unspecified (every
// date built from a calendar date, e.g. a filter range) or Utc into a column of the other kind,
// which turned every dated query and every new sale into a 500. The legacy behaviour accepts
// both, as Npgsql 5 did. Must be set before the first Npgsql use. SQLite is unaffected.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// --- Where the data lives -------------------------------------------------
// The database stays on the host laptop and is never copied to a client. ProgramData is
// used rather than a user profile so the API can run as a service later without the file
// moving, and so backups have one fixed path to target.
var dataDirectory = builder.Configuration["Lonnii:DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lonnii");
Directory.CreateDirectory(dataDirectory);

var databasePath = Path.Combine(dataDirectory, "lonnii.db");

// Local mode unless told otherwise, so an existing host laptop keeps working untouched.
var databaseOptions = new DatabaseOptions();
builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);
databaseOptions.Validate();

builder.Services.AddSingleton(databaseOptions);

// The server's own database. On a shop's host that is its SQLite file; on the licence server it
// is the live PostgreSQL.
void ConfigureControlDb(DbContextOptionsBuilder options)
{
    if (databaseOptions.IsPostgres)
        options.UseNpgsql(databaseOptions.ConnectionString);
    else
        // Foreign keys are off by default in SQLite; the schema relies on them.
        options.UseSqlite($"Data Source={databasePath};Foreign Keys=True");
}

builder.Services.AddHttpContextAccessor();

// A remote session (an administrator viewing an online shop from afar) reads the shop's copy of
// its data instead: RemoteRoutingMiddleware leaves the copy's path on the request, and every
// context created for that request is pointed at it. Everything else uses the server's database.
builder.Services.AddDbContext<LonniiDbContext>((services, options) =>
{
    var replicaPath = services.GetService<IHttpContextAccessor>()?.HttpContext?
        .Items[Lonnii.Api.Features.Remote.RemoteKeys.ReplicaPath] as string;

    if (replicaPath is not null)
        options.UseSqlite($"Data Source={replicaPath};Foreign Keys=True");
    else
        ConfigureControlDb(options);
});

var controlOptionsBuilder = new DbContextOptionsBuilder<LonniiDbContext>();
ConfigureControlDb(controlOptionsBuilder);
builder.Services.AddSingleton(new Lonnii.Api.Features.Remote.ControlDbOptions(controlOptionsBuilder.Options));
builder.Services.AddScoped<Lonnii.Api.Features.Remote.ControlDb>();

// --- Authentication -------------------------------------------------------
var jwtOptions = new JwtOptions();
builder.Configuration.GetSection(JwtOptions.SectionName).Bind(jwtOptions);

if (string.IsNullOrWhiteSpace(jwtOptions.Secret))
    jwtOptions.Secret = TokenService.LoadOrCreateSecret(Path.Combine(dataDirectory, "jwt.key"));

var tokenService = new TokenService(jwtOptions);
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(tokenService);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = tokenService.ValidationParameters);

builder.Services.AddAuthorization();

// --- Application services -------------------------------------------------
// Reaches our licence server during first launch. Short timeout: a shop waiting on setup
// needs an answer or a clear failure, not a two-minute hang.
builder.Services.AddHttpClient<ILicenceServer, HttpLicenceServer>(
    client => client.Timeout = TimeSpan.FromSeconds(20));

builder.Services.AddScoped<PrivilegeResolver>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddSingleton<Lonnii.Api.Features.Live.ShopChangeNotifier>();
builder.Services.AddScoped<GroupSessionService>();
// Who the caller is and whether the shop's licence is current are facts of the server's own
// database, even when the request's data comes from a shop's copy.
builder.Services.AddScoped<Lonnii.Api.Features.Remote.RemoteCommandApplier>();
builder.Services.AddScoped<LicenceGuard>(services =>
    new LicenceGuard(services.GetRequiredService<Lonnii.Api.Features.Remote.ControlDb>().Db));

// Populated per request by GroupScopeFilter, then injected into group-scoped endpoints.
builder.Services.AddScoped<GroupScope>();
builder.Services.AddScoped<GroupScopeFilter>(services => new GroupScopeFilter(
    services.GetRequiredService<Lonnii.Api.Features.Remote.ControlDb>().Db,
    services.GetRequiredService<GroupSessionService>(),
    services.GetRequiredService<PrivilegeResolver>(),
    services.GetRequiredService<LicenceGuard>(),
    services.GetRequiredService<GroupScope>()));
builder.Services.AddScoped<RequireAdminFilter>();

// Product and category photos live on the host laptop's disk, beside the database.
builder.Services.AddSingleton(new ImageStorageService(dataDirectory));

// Exporting or importing a whole espace builds its .db file here first - beside the
// database, not in the system temp folder, since it can be hundreds of megabytes.
builder.Services.AddSingleton(new EspaceTransferPaths(dataDirectory));
builder.Services.AddScoped<EspaceTransferService>();

// Cloud backup. On the OCI server these are the receiving end (BackupEndpoints); on a shop's
// host the same binary also runs the sending side (CloudBackupService). Which one does
// anything depends only on whether the workspace has a licence server to answer to.
builder.Services.AddSingleton(new BackupTokens(jwtOptions.Secret));
builder.Services.AddSingleton(new BackupStore(dataDirectory));
builder.Services.AddSingleton(new Lonnii.Api.Features.Remote.RemoteSessionTokens(jwtOptions.Secret));
builder.Services.AddSingleton(new Lonnii.Api.Features.Remote.RemoteCommandStore(dataDirectory));
builder.Services.AddSingleton(sp => new Lonnii.Api.Features.Remote.ReplicaStore(
    sp.GetRequiredService<BackupStore>(), dataDirectory));
builder.Services.AddSingleton(new CloudBackupPaths(databasePath));
builder.Services.AddHttpClient(CloudBackupClient.HttpClientName, client =>
{
    // A snapshot crossing a shop's connection can take a while; the ordinary 20 seconds is
    // right for a licence check and wrong for this.
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddScoped<CloudBackupClient>();
builder.Services.AddScoped<CloudBackupRunner>();
builder.Services.AddScoped<CloudRestoreService>();
// The sending side of the backup belongs to a shop's own host (SQLite). The licence server runs on
// PostgreSQL and is the one that *receives* backups: it has no cloud_backup_state table, and a job
// asking for it would only log a failure every interval.
if (!databaseOptions.IsPostgres)
{
    builder.Services.AddHostedService<CloudBackupService>();
    // The same host also looks for changes administrators asked for from afar (see RemoteCommandApplier).
    builder.Services.AddHostedService<Lonnii.Api.Features.Remote.RemoteCommandService>();
}
// Lets a till on the same network find this host without being told its address.
builder.Services.AddHostedService<LanDiscoveryService>();

// Registration (OCI only - see RegistrationEndpoints). The confirmation code goes out by SMTP;
// the log sender exists for development and is never used in production.
var emailOptions = new EmailOptions();
builder.Configuration.GetSection(EmailOptions.SectionName).Bind(emailOptions);
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton(new RegistrationLimiter());
builder.Services.AddSingleton(new PendingRegistrationStore(dataDirectory));

if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(emailOptions.Host))
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

builder.Services.AddOpenApi();

// --- Listening address ----------------------------------------------------
// The host laptop reaches the API on localhost; the other machines reach it on the host's
// LAN address, so Kestrel binds every interface. Plain HTTP on purpose: a self-signed
// certificate on a local network buys nothing and every client would have to trust it.
// Do not expose this port beyond the local network.
var port = builder.Configuration.GetValue("Lonnii:Port", 5280);
// On the OCI server set Lonnii__BindAddress=127.0.0.1 so only the reverse proxy in front of it can
// reach the API; a shop's host keeps the default, since its tills connect over the LAN.
var bindAddress = builder.Configuration["Lonnii:BindAddress"] is { Length: > 0 } configured ? configured : "0.0.0.0";
builder.WebHost.UseUrls($"http://{bindAddress}:{port}");

// Behind a reverse proxy every request arrives from the proxy, so the client's real address (which the
// sign-up rate limit and the access log depend on) is only in X-Forwarded-For. Trusted only when told
// the proxy exists - otherwise anyone could forge it.
if (builder.Configuration.GetValue("Lonnii:BehindProxy", false))
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                                   | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(System.Net.IPAddress.Loopback);
        options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
    });
}

var app = builder.Build();

// --- Start-up work --------------------------------------------------------
using (var startupScope = app.Services.CreateScope())
{
    var seeder = startupScope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

    if (databaseOptions.AllowsAutomaticMigration)
    {
        await seeder.MigrateAndSeedAsync();

        var sessions = startupScope.ServiceProvider.GetRequiredService<GroupSessionService>();
        await sessions.PurgeExpiredAsync();
    }
    else
    {
        // PostgreSQL is the licence server, sharing its database with Lonnii Business. It writes
        // only what licensing needs (shops, devices, registrations, backups), and at start-up it
        // writes nothing at all:
        //  - no schema changes: EF never migrates it (see DatabaseOptions.AllowsAutomaticMigration);
        //  - no privilege seeding: that would rewrite the display names, categories and the
        //    is_admin_only flag of rows the web app enforces, and add desktop-only privileges to
        //    its lists - and the server has no use for them, privileges only matter on a shop's host;
        //  - no session purge: groupe_sessions belongs to the web app.
        app.Logger.LogInformation(
            "PostgreSQL : aucune migration ni écriture au démarrage (serveur de licences). " +
            "Le schéma se modifie par SQL additif écrit à la main, jamais par les migrations EF.");
    }
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

if (builder.Configuration.GetValue("Lonnii:BehindProxy", false)) app.UseForwardedHeaders();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<Lonnii.Api.Features.Remote.RemoteRoutingMiddleware>();

app.MapAuthEndpoints();
app.MapActivationEndpoints();
app.MapSetupEndpoints();
app.MapLicenceEndpoints();
app.MapDeviceEndpoints();
app.MapGroupEndpoints();
app.MapPrivilegeEndpoints();
Lonnii.Api.Features.Remote.RemoteEndpoints.MapRemoteEndpoints(app);
Lonnii.Api.Features.Live.LiveEndpoints.MapLiveEndpoints(app);
app.MapStockEndpoints();
app.MapVentesEndpoints();
app.MapGroupePaymentEndpoints();
app.MapClientsEndpoints();
app.MapCaisseEndpoints();
app.MapChargesEndpoints();
app.MapMargesEndpoints();
app.MapAmortissementEndpoints();
app.MapBilanEndpoints();
app.MapProgrammeEndpoints();
app.MapParametresEndpoints();
app.MapEspaceTransferEndpoints();
app.MapConsommationEndpoints();
app.MapAuditEndpoints();
app.MapBackupEndpoints();
app.MapRegistrationEndpoints();
app.MapHostRegistrationEndpoints();
app.MapCloudBackupEndpoints();
app.MapImageEndpoints();

/// <summary>Lets a client confirm it is talking to a Lonnii host before signing in.</summary>
app.MapGet("/api/health", () => Results.Ok(new
{
    Service = "Lonnii Desktop API",
    Status = "ok",
    // Names the provider, not the connection string: health is unauthenticated, and a
    // PostgreSQL connection string carries the server address and password.
    Database = databaseOptions.IsPostgres
        ? DatabaseOptions.Postgres
        : Path.GetFileName(databasePath),
    Time = DateTime.UtcNow,
})).WithTags("Health");

app.Logger.LogInformation(
    "Lonnii Desktop API listening on port {Port}; provider {Provider}; {Location}",
    port,
    databaseOptions.IsPostgres ? DatabaseOptions.Postgres : DatabaseOptions.Sqlite,
    databaseOptions.IsPostgres ? "base distante" : databasePath);

try
{
    app.Run();
}
catch (IOException e) when (IsPortAlreadyInUse(e))
{
    // By far the most likely cause on the host laptop is a copy already running, often
    // one started from an IDE. An unexplained stack trace sends people hunting for a bug
    // that is not there, so say what happened and what to do about it.
    //
    // Written straight to stderr rather than through app.Logger: by the time a bind
    // failure surfaces, the host has disposed its logging providers and logging here
    // throws an ObjectDisposedException over the top of the real message.
    Console.Error.WriteLine(
        $"""

        Le port {port} est déjà utilisé.

        Une autre instance du serveur Lonnii est probablement déjà démarrée sur cet
        ordinateur - vérifiez notamment si Visual Studio en exécute une.

        Fermez-la, ou choisissez un autre port en définissant Lonnii__Port, puis relancez.

        """);

    return 1;
}

return 0;

/// <summary>
/// True when a start-up failure was caused by the port already being taken.
/// Kestrel reports this as IOException -> AddressInUseException -> SocketException, so
/// the whole chain is walked rather than only the first inner exception.
/// </summary>
static bool IsPortAlreadyInUse(Exception? exception)
{
    for (var e = exception; e is not null; e = e.InnerException)
    {
        if (e is AddressInUseException) return true;
        if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
    }

    return false;
}

/// <summary>Exposed so the integration tests can spin the API up in-process.</summary>
public partial class Program;
