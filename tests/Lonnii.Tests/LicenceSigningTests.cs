using System.Net.Http.Json;
using System.Security.Cryptography;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// The licence server's signature on a refresh (see LicenceToken), which is what lets a shop's host
/// ignore its own, editable, database. The host's side is in LicenceEnforcementTests.
/// </summary>
public class LicenceSigningTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        var keyFile = Path.Combine(_dataDirectory, "licence-signing.key");
        await File.WriteAllTextAsync(keyFile, Convert.ToBase64String(_key.ExportPkcs8PrivateKey()));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.UseSetting("Lonnii:LicenceSigningKeyFile", keyFile);
        });
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var admin = new User { Email = "admin@pharmacie-nord.bf", IsVerified = true };
        db.Users.Add(admin);
        db.Groupes.Add(new Groupe { Id = GroupId, Nom = "Pharmacie Nord", IdUserAdmin = admin.IdUser, Mode = DeploymentModes.Local });
        db.Devices.Add(new Device { GroupId = GroupId, DeviceId = "machine-1" });
        db.Devices.Add(new Device { GroupId = GroupId, DeviceId = "machine-2" });
        db.Devices.Add(new Device { GroupId = GroupId, DeviceId = "removed", RevokedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_refresh_carries_a_licence_signed_for_the_espace_and_its_bound_machines()
    {
        var response = await _client.PostAsJsonAsync("/api/licence/refresh", new LicenceRefreshRequest(GroupId, "machine-1"));
        var body = await response.Content.ReadFromJsonAsync<LicenceRefreshResponse>();

        var claims = LicenceToken.Verify(body!.LicenceToken, PublicKey);
        Assert.NotNull(claims);
        Assert.Equal(GroupId, claims.GroupId);
        Assert.Equal(DeploymentModes.Local, claims.Mode);
        Assert.Null(claims.MustReconnectBy);
        Assert.Equal(["machine-1", "machine-2"], claims.Devices.Order());
    }

    [Fact]
    public void A_licence_from_another_key_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = LicenceToken.Issue(other.ExportPkcs8PrivateKey(),
            new LicenceClaims(GroupId, DeploymentModes.Local, null, DateTime.UtcNow, [], false, null));

        Assert.Null(LicenceToken.Verify(token, PublicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("abc.def")]
    public void Garbage_is_rejected_rather_than_throwing(string token) =>
        Assert.Null(LicenceToken.Verify(token, PublicKey));

    [Fact]
    public void The_built_in_key_is_a_real_public_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(LicenceToken.PublicKey), out _);
        Assert.Equal(256, key.KeySize);
    }
}
