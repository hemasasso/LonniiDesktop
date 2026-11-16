using Lonnii.Api.Features.Infrastructure;
using Lonnii.Data;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Licensing;

/// <summary>
/// The shop's host renewing its espaces' licences by itself, as the shop computer it runs on.
///
/// <para>
/// Until now only the desktop app renewed (every five minutes, while it was open and signed in), so a
/// shop whose app had been closed for a day - overnight, a weekend, the computer left on with Lonnii
/// shut - showed "Serveur central Lonnii : Hors ligne" in Configuration until someone pressed
/// Actualiser, and an online shop crept towards its reconnect deadline for no reason. Renewing here,
/// one minute after start and then every hour, keeps the licence fresh whether anyone is signed in.
/// </para>
/// <para>
/// It asks exactly as the app on this computer would - same device id (see
/// <see cref="HostMachineIdentity.DeviceId"/>), same rules (<see cref="LicenceEndpoints.RenewAsync"/>),
/// so it can never earn a shop more than the app could. A host whose own computer is not one of the
/// espace's registered machines (the app runs on a till only) is refused by our server and simply
/// leaves the renewing to the app, as before.
/// </para>
/// </summary>
public sealed class LicenceRenewalService(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<LicenceRenewalService> logger) : BackgroundService
{
    /// <summary>
    /// The settings a shipped host ignores (see ShippedHost) but someone set anyway in appsettings.json
    /// or the environment: nothing to enforce - they have no effect - but worth telling us, since nobody
    /// edits them by accident.
    /// </summary>
    private string? TamperedSettings()
    {
        if (!ShippedHost.IsShipped) return null;
        var found = new List<string>();
        if (config.GetValue("Lonnii:AllowManualSetup", false)) found.Add("AllowManualSetup");
        if (!config.GetValue("Lonnii:OnlineRegistration:Required", true)) found.Add("OnlineRegistration:Required");
        if (config["Lonnii:LicenceServerUrl"] is { Length: > 0 } url
            && !string.Equals(url.Trim().TrimEnd('/'), ShippedHost.LicenceServerUrl, StringComparison.OrdinalIgnoreCase))
            found.Add($"LicenceServerUrl = {url}");
        return found.Count == 0 ? null : string.Join(", ", found);
    }

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostMachineIdentity.DeviceId is not { } deviceId)
        {
            logger.LogInformation("Renouvellement automatique de la licence désactivé : identifiant de l'ordinateur illisible.");
            return;
        }

        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewAllAsync(deviceId, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                logger.LogError(e, "Renouvellement automatique de la licence interrompu");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RenewAllAsync(string deviceId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<LonniiDbContext>();
        var guard = services.GetRequiredService<LicenceGuard>();
        var licences = services.GetRequiredService<ILicenceServer>();

        // On a shipped host every espace checks in, an emptied licence_server_url included.
        var shipped = ShippedHost.IsShipped;
        var groupes = await db.Groupes
            .Where(g => (shipped || (g.LicenceServerUrl != null && g.LicenceServerUrl != "")) && !g.IsDeleted)
            .ToListAsync(ct);

        var tampered = TamperedSettings();

        foreach (var groupe in groupes)
        {
            if (tampered is not null) guard.Security?.Record(groupe.Id, SecurityEventKinds.ConfigTampered, tampered);

            // This computer is not one of the espace's machines (the app runs on a till only): our server
            // would refuse it - and now notes every such refusal as an unknown machine - so leave the
            // renewing to the app, as a refusal used to.
            if (!await db.Devices.AnyAsync(d => d.GroupId == groupe.Id && d.DeviceId == deviceId && d.RevokedAt == null, ct))
                continue;

            var refused = await LicenceEndpoints.RenewAsync(groupe, deviceId, db, guard, licences, ct);
            if (refused is null)
                logger.LogDebug("Licence renouvelée pour l'espace {GroupId}", groupe.Id);
            else
                logger.LogInformation("Licence de l'espace {GroupId} non renouvelée automatiquement (l'application s'en charge)", groupe.Id);
        }
    }
}
