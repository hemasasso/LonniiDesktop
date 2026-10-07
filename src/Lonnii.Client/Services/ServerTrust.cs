using Lonnii.Shared.Security;

namespace Lonnii.Client.Services;

/// <summary>
/// Which server addresses this till will use without searching for them. Anything else has to
/// be found by the search on the shop's own network - the settings file can be edited by
/// anyone at the till, so a remembered address alone proves nothing.
/// </summary>
public static class ServerTrust
{
    /// <summary>This machine itself: right for the till that also runs the server.</summary>
    public static bool IsThisMachine(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        var host = RepairCode.NormalizeAddress(address);
        host = host.StartsWith('[') ? host[1..host.IndexOf(']')]
             : host.Count(c => c == ':') == 1 ? host[..host.IndexOf(':')]
             : host;

        return host is "localhost" or "::1" || host.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>The address a repair code of ours authorised for this till, if still genuine.</summary>
    public static string? RepairedAddress(ClientSettings settings)
    {
        var address = settings.RepairHost;
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(settings.RepairCode)) return null;

        // Expiry only mattered when the code was typed in; a code already accepted stays good.
        var result = RepairCode.Check(settings.RepairCode, DeviceIdentity.Current, address, DateTimeOffset.UtcNow, checkExpiry: false);
        return result == RepairCodeResult.Valid ? address : null;
    }

    /// <summary>Usable without searching: this machine, or the address we authorised.</summary>
    public static bool IsAllowedWithoutSearch(string? address, ClientSettings settings) =>
        IsThisMachine(address) ||
        (RepairedAddress(settings) is { } repaired &&
         RepairCode.NormalizeAddress(repaired) == RepairCode.NormalizeAddress(address ?? string.Empty));
}
