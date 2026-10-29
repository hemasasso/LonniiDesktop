using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Lonnii.Api.Features.Updates;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lonnii.Tests;

/// <summary>
/// "Installer maintenant" on the shop's main computer: only an admin, only from that computer,
/// and what is launched is a verified copy of the version the shop downloaded.
/// </summary>
public class UpdateInstallTests : IAsyncLifetime
{
    private const string FileName = "LonniiBusiness-Setup-1.0.1.exe";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeLauncher _launcher = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    /// <summary>Where the request seems to come from: this computer unless a test says otherwise.</summary>
    private IPAddress _caller = IPAddress.Loopback;

    private sealed class FakeLauncher : IUpdateLauncher
    {
        public string? Launched { get; private set; }
        public byte[]? LaunchedBytes { get; private set; }

        public string? Launch(string installerPath, string logPath)
        {
            Launched = installerPath;
            LaunchedBytes = File.ReadAllBytes(installerPath);
            return null;
        }
    }

    private sealed class CallerFilter(Func<IPAddress> caller) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextMiddleware) =>
            {
                http.Connection.RemoteIpAddress = caller();
                http.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.10");
                return nextMiddleware(http);
            });
            next(app);
        };
    }

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        var publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(new UpdatePaths(_dataDirectory, publicKey)));
                services.Replace(ServiceDescriptor.Singleton<IUpdateLauncher>(_launcher));
                services.AddSingleton<IStartupFilter>(new CallerFilter(() => _caller));
            });
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _key.Dispose();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Session(string Token, string GroupSession);

    private async Task<Session> SignInAsync(string email, string? groupId = null)
    {
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(email, "MotDePasse123"))).Content.ReadFromJsonAsync<LoginResponse>())!;

        if (groupId is null)
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
            {
                Content = JsonContent.Create(new CreateGroupeRequest("Boutique")),
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
            groupId = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>())!.Id;
        }

        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupId}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = (await (await _client.SendAsync(open)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;
        return new Session(login.AccessToken, scoped.SessionToken);
    }

    private async Task<(Session Owner, string GroupId)> OwnerAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"));
        var owner = await SignInAsync("patron@lonnii.test");

        var groupes = await Send(HttpMethod.Get, "/api/groupes", owner);
        var groupId = (await groupes.Content.ReadFromJsonAsync<List<GroupeDto>>())!.Single().Id;
        return (owner, groupId);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, Session session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task<byte[]> PublishAsync(string version = "1.0.1")
    {
        var updates = Path.Combine(_dataDirectory, "updates");
        Directory.CreateDirectory(updates);
        var name = $"{UpdateManifest.FilePrefix}{version}.exe";
        var bytes = RandomNumberGenerator.GetBytes(2048);
        var path = Path.Combine(updates, name);
        await File.WriteAllBytesAsync(path, bytes);
        var manifest = UpdateManifest.Sign(_key.ExportPkcs8PrivateKey(), version, name, bytes.Length,
            await UpdateManifest.HashFileAsync(path), DateTime.UtcNow, null);
        await File.WriteAllTextAsync(Path.Combine(updates, "latest.json"),
            JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web));
        return bytes;
    }

    [Fact]
    public async Task An_admin_at_the_main_computer_launches_a_verified_copy()
    {
        var (owner, _) = await OwnerAsync();
        var installer = await PublishAsync();

        var response = await Send(HttpMethod.Post, "/api/updates/install", owner, new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(_launcher.Launched);
        Assert.NotEqual(Path.Combine(_dataDirectory, "updates", FileName), _launcher.Launched);
        Assert.Equal(installer, _launcher.LaunchedBytes);
    }

    [Fact]
    public async Task A_till_cannot_restart_the_main_computer()
    {
        var (owner, _) = await OwnerAsync();
        await PublishAsync();
        _caller = IPAddress.Parse("192.168.1.23");

        var response = await Send(HttpMethod.Post, "/api/updates/install", owner, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_launcher.Launched);
    }

    [Fact]
    public async Task A_member_cannot_install()
    {
        var (owner, groupId) = await OwnerAsync();
        await Send(HttpMethod.Post, "/api/groupe/members", owner,
            new AddMemberRequest("caisse@lonnii.test", "MotDePasse123", "caisse"));
        var member = await SignInAsync("caisse@lonnii.test", groupId);
        await PublishAsync();

        var response = await Send(HttpMethod.Post, "/api/updates/install", member, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_launcher.Launched);
    }

    [Fact]
    public async Task Nothing_to_install_when_nothing_newer_is_held()
    {
        var (owner, _) = await OwnerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/updates/install", owner, new { })).StatusCode);

        // The tests run as version 1.0.0: offering it again installs nothing.
        await PublishAsync("1.0.0");
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, "/api/updates/install", owner, new { })).StatusCode);
        Assert.Null(_launcher.Launched);
    }
}
