using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lonnii.Api.Features.Infrastructure;

/// <summary>
/// Answers "is there a Lonnii host on this network?" so a till never has to be told the host's
/// address.
///
/// <para>
/// A till broadcasts <see cref="Request"/> over UDP; every host that hears it replies, to the
/// sender only, with the API port and the computer's name. Nothing else is revealed - no shop
/// name, no data - and the reply is the only thing it does: signing in still needs an account.
/// </para>
/// <para>
/// Best effort by design. If the port is taken, or the network blocks broadcasts, the host
/// carries on and the sign-in window falls back to typing the address. Windows Firewall must
/// allow inbound UDP on the discovery port as well as the API's TCP port; without it the host
/// is simply not found, which is the same symptom as before this existed.
/// </para>
/// </summary>
public sealed class LanDiscoveryService(IConfiguration configuration, ILogger<LanDiscoveryService> logger)
    : BackgroundService
{
    /// <summary>What a till sends. Versioned so the protocol can change without old hosts answering wrongly.</summary>
    public const string Request = "LONNII-DISCOVER/1";

    /// <summary>The reply prefix; the rest is <c>|{apiPort}|{machineName}</c>.</summary>
    public const string ReplyPrefix = "LONNII-HOST/1";

    public const int DefaultPort = 5281;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Lonnii:Discovery:Enabled", true)) return;

        var discoveryPort = configuration.GetValue("Lonnii:Discovery:Port", DefaultPort);
        var apiPort = configuration.GetValue("Lonnii:Port", 5280);

        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
        }
        catch (SocketException e)
        {
            // Another copy of the host, or something else, holds the port. Not worth stopping for.
            logger.LogWarning("Découverte réseau désactivée : port UDP {Port} indisponible ({Message})", discoveryPort, e.Message);
            return;
        }

        using (udp)
        {
            // Windows reports an ICMP "port unreachable" from an earlier reply as a socket error
            // on the next receive. That is noise here, not a failure.
            if (OperatingSystem.IsWindows())
            {
                try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0], null); }
                catch (SocketException) { }
            }

            logger.LogInformation("Découverte réseau : écoute sur le port UDP {Port}", discoveryPort);

            var reply = Encoding.UTF8.GetBytes($"{ReplyPrefix}|{apiPort}|{Environment.MachineName}");

            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (Encoding.UTF8.GetString(received.Buffer) != Request) continue;

                try
                {
                    await udp.SendAsync(reply, received.RemoteEndPoint, stoppingToken);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    // The asker went away; nothing to do.
                }
            }
        }
    }
}
