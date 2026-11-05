using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lonnii.Data;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Notifications;

/// <summary>
/// Puts the bell's alerts on administrators' phones, lock screen included. Runs on the shop's own
/// host, which has the live data; every few minutes it works out each admin's alerts with
/// <see cref="NotificationEndpoints.BuildAsync"/> and sends only what is new since the last pass,
/// through Expo's push service (the phone app's tokens are Expo tokens).
///
/// <para>"New" means: an alert that was not there before, or a stock line whose count went up
/// (2 then 3 products out of stock pushes again; 3 then 2 does not). An alert that goes away and
/// comes back later is new again. What was sent is only kept in memory, and the first pass after
/// start-up just takes note, so restarting the host does not repeat every open alert.</para>
///
/// <para>Needs internet at the shop; without it a pass fails quietly and the next one tries again.
/// The alerts still show in the desktop's bell either way.</para>
/// </summary>
public sealed partial class PushAlertService(
    IServiceScopeFactory scopes,
    IHttpClientFactory http,
    IConfiguration configuration,
    ILogger<PushAlertService> logger) : BackgroundService
{
    public const string HttpClientName = "expo-push";

    /// <summary>Per espace|admin|alert: the count last pushed (1 for alerts without a count).</summary>
    private Dictionary<string, int> _sent = [];
    private bool _primed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Lonnii:Push:IntervalMinutes", 5)));

        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Notifications téléphone : passage échoué ({Message})", ex.Message);
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var tokens = await db.PushDevices.AsNoTracking().ToListAsync(ct);
        if (tokens.Count == 0)
        {
            _primed = true;
            return;
        }
        var users = tokens.Select(t => t.UserId).Distinct().ToList();

        // Espaces where a user with a phone is the owner or holds an admin role.
        var owned = await db.Groupes.AsNoTracking()
            .Where(g => users.Contains(g.IdUserAdmin))
            .Select(g => new { GroupId = g.Id, UserId = g.IdUserAdmin, g.Nom })
            .ToListAsync(ct);
        var roles = await db.UserRoles.AsNoTracking()
            .Where(r => users.Contains(r.UserId) && r.IsActive && (r.Role == GroupRoles.Admin || r.Role == GroupRoles.SubAdmin))
            .Join(db.Groupes, r => r.GroupId, g => g.Id, (r, g) => new { GroupId = g.Id, r.UserId, g.Nom })
            .ToListAsync(ct);
        var admins = owned.Concat(roles).DistinctBy(a => (a.GroupId, a.UserId)).ToList();

        var current = new Dictionary<string, int>();
        var messages = new List<ExpoMessage>();

        foreach (var admin in admins)
        {
            var items = await NotificationEndpoints.BuildAsync(admin.GroupId, admin.UserId, isAdmin: true, db, ct);
            foreach (var item in items)
            {
                // Stock titles carry a count ("3 produits en rupture"): keyed without it, compared by it.
                var count = item.Kind == "stock" && LeadingNumber().Match(item.Title) is { Success: true } m ? int.Parse(m.Value) : 1;
                var key = item.Kind == "stock"
                    ? $"{admin.GroupId}|{admin.UserId}|stock|{item.Severity}"
                    : $"{admin.GroupId}|{admin.UserId}|{item.Kind}|{item.Title}";
                current[key] = count;

                if (!_primed || (_sent.TryGetValue(key, out var before) && count <= before)) continue;

                foreach (var token in tokens.Where(t => t.UserId == admin.UserId))
                    messages.Add(new ExpoMessage(
                        To: token.PushToken,
                        Title: $"{admin.Nom} · {item.Title}",
                        Body: item.Message,
                        Sound: "default",
                        Priority: "high",
                        ChannelId: "default",
                        Data: new { type = "alert", kind = item.Kind, module = item.Module, groupId = admin.GroupId }));
            }
        }

        if (messages.Count > 0) await SendAsync(messages, ct);

        // Only once sent: a failed send leaves the old state, so the next pass tries again.
        _sent = current;
        _primed = true;
    }

    /// <summary>Expo takes up to 100 messages per request.</summary>
    private async Task SendAsync(List<ExpoMessage> messages, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        foreach (var batch in messages.Chunk(100))
        {
            using var response = await client.PostAsJsonAsync("https://exp.host/--/api/v2/push/send", batch, ct);
            response.EnsureSuccessStatusCode();
        }
        logger.LogInformation("Notifications téléphone : {Count} envoyée(s)", messages.Count);
    }

    private sealed record ExpoMessage(string To, string Title, string Body, string Sound, string Priority, string ChannelId, object Data);

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumber();
}
