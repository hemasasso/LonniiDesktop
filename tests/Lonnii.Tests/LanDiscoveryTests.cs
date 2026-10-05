using System.Net;
using System.Net.Sockets;
using System.Text;
using Lonnii.Api.Features.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lonnii.Tests;

/// <summary>
/// The host's side of "find the server without typing its address", over a real UDP socket on
/// loopback. A till that cannot be found is simply asked for an address, so what matters is that
/// the host answers the right message, with the right port, and nothing else.
/// </summary>
public class LanDiscoveryTests
{
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static LanDiscoveryService Service(int discoveryPort, int apiPort = 5280, bool enabled = true) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lonnii:Discovery:Port"] = discoveryPort.ToString(),
            ["Lonnii:Discovery:Enabled"] = enabled.ToString(),
            ["Lonnii:Port"] = apiPort.ToString(),
        }).Build(), NullLogger<LanDiscoveryService>.Instance);

    /// <summary>
    /// Asks, and keeps asking for two seconds. The service binds its socket a moment after
    /// StartAsync returns, and Windows turns a datagram sent before that into a connection-reset
    /// error on the asker's next receive - so one try is a race, and a silent host looks the same
    /// as one that has not started yet. Both end as null here, which is what the callers want.
    /// </summary>
    private static async Task<string?> AskAsync(int port, string message)
    {
        using var asker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var request = Encoding.UTF8.GetBytes(message);
        var deadline = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await asker.SendAsync(request, new IPEndPoint(IPAddress.Loopback, port));

                using var attempt = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                var reply = await asker.ReceiveAsync(attempt.Token);
                return Encoding.UTF8.GetString(reply.Buffer);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                await Task.Delay(50);
            }
        }

        return null;
    }

    [Fact]
    public async Task A_host_answers_a_discovery_request_with_its_api_port_and_name()
    {
        var port = FreeUdpPort();
        using var service = Service(port, apiPort: 5999);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var reply = await AskAsync(port, LanDiscoveryService.Request);

            Assert.Equal($"{LanDiscoveryService.ReplyPrefix}|5999|{Environment.MachineName}", reply);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Anything_else_gets_no_answer()
    {
        var port = FreeUdpPort();
        using var service = Service(port);
        await service.StartAsync(CancellationToken.None);

        try
        {
            Assert.Null(await AskAsync(port, "hello"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_disabled_host_stays_silent()
    {
        var port = FreeUdpPort();
        using var service = Service(port, enabled: false);
        await service.StartAsync(CancellationToken.None);

        try
        {
            Assert.Null(await AskAsync(port, LanDiscoveryService.Request));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_taken_port_does_not_stop_the_host()
    {
        var port = FreeUdpPort();
        using var holder = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        using var service = Service(port);

        // Starting must not throw: discovery is a convenience, not a reason to fail to start.
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
