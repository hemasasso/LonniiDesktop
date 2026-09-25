using Microsoft.AspNetCore.Connections;
using System.Net.Sockets;
using Lonnii.Api.Endpoints;
using Lonnii.Api.Security;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Data.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

// Note: issuing a customer's credentials file lives in tools/Lonnii.Setup, not here.
// Local-mode customers receive this program, so a licence generator inside it would let a
// shop grant itself any number of machines.

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

builder.Services.AddDbContext<LonniiDbContext>(options =>
{
    if (databaseOptions.IsPostgres)
        options.UseNpgsql(databaseOptions.ConnectionString);
    else
        // Foreign keys are off by default in SQLite; the schema relies on them.
        options.UseSqlite($"Data Source={databasePath};Foreign Keys=True");
});

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
builder.Services.AddScoped<GroupSessionService>();

// Populated per request by GroupScopeFilter, then injected into group-scoped endpoints.
builder.Services.AddScoped<GroupScope>();
builder.Services.AddScoped<GroupScopeFilter>();
builder.Services.AddScoped<RequireAdminFilter>();

// Product and category photos live on the host laptop's disk, beside the database.
builder.Services.AddSingleton(new ImageStorageService(dataDirectory));

builder.Services.AddOpenApi();

// --- Listening address ----------------------------------------------------
// The host laptop reaches the API on localhost; the other machines reach it on the host's
// LAN address, so Kestrel binds every interface. Plain HTTP on purpose: a self-signed
// certificate on a local network buys nothing and every client would have to trust it.
// Do not expose this port beyond the local network.
var port = builder.Configuration.GetValue("Lonnii:Port", 5280);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// --- Start-up work --------------------------------------------------------
using (var startupScope = app.Services.CreateScope())
{
    var seeder = startupScope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

    if (databaseOptions.AllowsAutomaticMigration)
    {
        await seeder.MigrateAndSeedAsync();
    }
    else
    {
        // Never migrate PostgreSQL from here - see DatabaseOptions.AllowsAutomaticMigration.
        // Seeding still runs: it only upserts the privilege catalogues, which is additive
        // and is how a new privilege reaches an existing workspace.
        app.Logger.LogInformation(
            "PostgreSQL : migrations non appliquées automatiquement. " +
            "Le schéma se modifie par SQL additif écrit à la main, jamais par les migrations EF " +
            "(elles sont générées pour SQLite).");

        await seeder.SeedAsync();
    }

    var sessions = startupScope.ServiceProvider.GetRequiredService<GroupSessionService>();
    await sessions.PurgeExpiredAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapActivationEndpoints();
app.MapSetupEndpoints();
app.MapLicenceEndpoints();
app.MapDeviceEndpoints();
app.MapGroupEndpoints();
app.MapPrivilegeEndpoints();
app.MapStockEndpoints();
app.MapVentesEndpoints();
app.MapParametresEndpoints();
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
