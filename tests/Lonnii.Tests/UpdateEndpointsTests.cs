using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Lonnii.Api.Features.Updates;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lonnii.Tests;

/// <summary>
/// What our server hands to computers looking for a new version: only a notice we signed, and
/// only the installer that notice names.
/// </summary>
public class UpdateEndpointsTests : IAsyncLifetime
{
    private const string FileName = "LonniiBusiness-Setup-1.0.1.exe";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private string Updates => Path.Combine(_dataDirectory, "updates");

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "updates"));

        var publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton(new UpdatePaths(_dataDirectory, publicKey))));
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

    private async Task<UpdateManifest> PublishAsync(byte[] installer, ECDsa? signer = null)
    {
        var path = Path.Combine(Updates, FileName);
        await File.WriteAllBytesAsync(path, installer);

        var manifest = UpdateManifest.Sign(
            (signer ?? _key).ExportPkcs8PrivateKey(), "1.0.1", FileName, installer.Length,
            await UpdateManifest.HashFileAsync(path), DateTime.UtcNow, "Corrections");
        await File.WriteAllTextAsync(Path.Combine(Updates, "latest.json"),
            JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web));
        return manifest;
    }

    [Fact]
    public async Task A_signed_version_is_offered_and_its_installer_downloads()
    {
        byte[] installer = [1, 2, 3, 4, 5];
        var published = await PublishAsync(installer);

        var manifest = await _client.GetFromJsonAsync<UpdateManifest>("/api/updates/latest");

        Assert.NotNull(manifest);
        Assert.Equal("1.0.1", manifest!.Version);
        Assert.Equal(published.Sha256, manifest.Sha256);
        Assert.True(manifest.IsNewerThan("1.0.0"));
        Assert.False(manifest.IsNewerThan("1.0.1"));

        var bytes = await _client.GetByteArrayAsync("/api/updates/files/" + FileName);
        Assert.Equal(installer, bytes);
    }

    [Fact]
    public async Task Nothing_is_offered_when_nothing_is_published()
    {
        var response = await _client.GetAsync("/api/updates/latest");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_notice_signed_with_another_key_is_never_served()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await PublishAsync([1, 2, 3], stranger);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.GetAsync("/api/updates/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/updates/files/" + FileName)).StatusCode);
    }

    [Fact]
    public async Task An_installer_swapped_after_signing_is_not_served()
    {
        await PublishAsync([1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(Updates, FileName), [9, 9, 9, 9]);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.GetAsync("/api/updates/latest")).StatusCode);
    }

    [Fact]
    public async Task Only_the_named_installer_can_be_downloaded()
    {
        await PublishAsync([1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(Updates, "LonniiBusiness-Setup-0.9.exe"), "old");

        var response = await _client.GetAsync("/api/updates/files/LonniiBusiness-Setup-0.9.exe");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void A_tampered_notice_fails_its_signature()
    {
        var manifest = UpdateManifest.Sign(
            _key.ExportPkcs8PrivateKey(), "1.0.1", FileName, 3, new string('a', 64), DateTime.UtcNow, null);
        var publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        Assert.True(manifest.IsGenuine(publicKey));
        Assert.False((manifest with { Sha256 = new string('b', 64) }).IsGenuine(publicKey));
        Assert.False((manifest with { Version = "9.0.0" }).IsGenuine(publicKey));
        Assert.False((manifest with { FileName = "../evil.exe" }).IsGenuine(publicKey));
    }
}
