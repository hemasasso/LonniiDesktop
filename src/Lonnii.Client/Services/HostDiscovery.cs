using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Lonnii.Client.Services;

/// <summary>A Lonnii host that answered on the local network.</summary>
/// <param name="Address">Ready for <c>LonniiApiClient.Connect</c>, e.g. <c>192.168.1.20:5280</c>.</param>
public sealed record DiscoveredHost(string Address, string Name);

/// <summary>
/// Finds Lonnii hosts on the local network so nobody has to type an address.
///
/// <para>
/// Broadcasts one small UDP message and listens briefly for answers (see the API's
/// <c>LanDiscoveryService</c>). It is sent to the general broadcast address, to each network
/// card's own broadcast address (the general one is dropped by some routers and virtual
/// adapters) and to this machine - a host's own broadcast does not reliably loop back to it.
/// </para>
/// </summary>
public static class HostDiscovery
{
    private const string Request = "LONNII-DISCOVER/1";
    private const string ReplyPrefix = "LONNII-HOST/1";
    private const int Port = 5281;

    public static async Task<IReadOnlyList<DiscoveredHost>> FindAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredHost>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var message = Encoding.UTF8.GetBytes(Request);
            foreach (var target in Targets())
            {
                try { await udp.SendAsync(message, target, ct); }
                catch (SocketException) { /* a card that cannot broadcast is not fatal */ }
            }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout);

            while (!window.IsCancellationRequested)
            {
                UdpReceiveResult reply;
                try
                {
                    reply = await udp.ReceiveAsync(window.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (Parse(reply) is { } host) found[host.Address] = host;
            }
        }
        catch (SocketException)
        {
            // No usable network: an empty answer sends the caller to the manual field.
        }

        // A computer with several network cards (a PC with WSL, VMware or a VPN installed has many) answers once
        // per card, from a different address each time. That is one host, not several: count it once, and
        // prefer "localhost" when the host is this very machine.
        return [.. found.Values
            .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(h => h.Address.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
                         ?? g.First())];
    }

    private static DiscoveredHost? Parse(UdpReceiveResult reply)
    {
        var parts = Encoding.UTF8.GetString(reply.Buffer).Split('|');
        if (parts.Length < 3 || parts[0] != ReplyPrefix || !int.TryParse(parts[1], out var port)) return null;

        // A host answering from this very machine is best addressed as localhost.
        var address = IPAddress.IsLoopback(reply.RemoteEndPoint.Address)
            ? "localhost"
            : reply.RemoteEndPoint.Address.ToString();

        return new DiscoveredHost($"{address}:{port}", parts[2]);
    }

    private static IEnumerable<IPEndPoint> Targets()
    {
        yield return new IPEndPoint(IPAddress.Loopback, Port);
        yield return new IPEndPoint(IPAddress.Broadcast, Port);

        foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (card.OperationalStatus != OperationalStatus.Up ||
                card.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in card.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;

                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);

                yield return new IPEndPoint(new IPAddress(broadcast), Port);
            }
        }
    }
}
