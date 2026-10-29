using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// Deleting a whole espace needs the creator's own password, checked by the server: the screen asks
/// for the espace's name and the password, but a signed-in session left open or a stolen sign-in token
/// must not be enough on their own.
/// </summary>
public class DeleteEspaceTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string OwnerEmail = "patron@boutique.bf";
    private const string OwnerPassword = "Motdepasse123";

    private string _jwt = null!;
    private string _groupId = null!;

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory));
        _client = _factory.CreateClient();

        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(OwnerEmail, OwnerPassword, "patron"));
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(OwnerEmail, OwnerPassword))).Content.ReadFromJsonAsync<LoginResponse>())!;
        _jwt = login.AccessToken;

        using var create = Authorized(HttpMethod.Post, "/api/groupes");
        create.Content = JsonContent.Create(new CreateGroupeRequest("Boutique"));
        _groupId = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>())!.Id;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        return request;
    }

    private async Task<HttpResponseMessage> DeleteAsync(string? password)
    {
        var request = Authorized(HttpMethod.Delete, $"/api/groupes/{_groupId}");
        if (password is not null) request.Content = JsonContent.Create(new DeleteGroupeRequest(password));
        return await _client.SendAsync(request);
    }

    private async Task<bool> EspaceExistsAsync()
    {
        var list = (await (await _client.SendAsync(Authorized(HttpMethod.Get, "/api/groupes")))
            .Content.ReadFromJsonAsync<List<GroupeDto>>())!;
        return list.Any(g => g.Id == _groupId);
    }

    [Fact]
    public async Task Deleting_without_a_password_is_refused_and_nothing_is_deleted()
    {
        var response = await DeleteAsync(password: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await EspaceExistsAsync());
    }

    [Fact]
    public async Task A_blank_password_is_refused_too()
    {
        var response = await DeleteAsync(password: "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await EspaceExistsAsync());
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_nothing_is_deleted()
    {
        var response = await DeleteAsync("pas-le-bon-mot-de-passe");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Mot de passe incorrect", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);
        Assert.True(await EspaceExistsAsync());
    }

    [Fact]
    public async Task The_creators_own_password_deletes_the_espace()
    {
        var response = await DeleteAsync(OwnerPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await EspaceExistsAsync());
    }

    // --- Taking it off this computer only ---

    private async Task<HttpResponseMessage> RemoveLocalAsync(string password)
    {
        var request = Authorized(HttpMethod.Post, $"/api/groupes/{_groupId}/remove-local");
        request.Content = JsonContent.Create(new DeleteGroupeRequest(password));
        return await _client.SendAsync(request);
    }

    /// <summary>A local-mode shop's only copy is this computer: "removing" it would be deleting it.</summary>
    [Fact]
    public async Task A_local_mode_espace_cannot_be_taken_off_this_computer()
    {
        var response = await RemoveLocalAsync(OwnerPassword);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("mode local", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);
        Assert.True(await EspaceExistsAsync());
    }

    [Fact]
    public async Task An_online_espace_is_taken_off_this_computer_with_the_right_password()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            var groupe = await db.Groupes.SingleAsync(g => g.Id == _groupId);
            groupe.Mode = DeploymentModes.Online;
            groupe.LicenceServerUrl = "https://licences.lonnii.test";
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await RemoveLocalAsync("pas-le-bon")).StatusCode);
        Assert.True(await EspaceExistsAsync());

        // Empty, so no last backup is needed before it goes.
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveLocalAsync(OwnerPassword)).StatusCode);
        Assert.False(await EspaceExistsAsync());
    }
}
