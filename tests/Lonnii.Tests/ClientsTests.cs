using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// The Ventes client space. Sales carry the customer's name and phone as text, so what is
/// worth proving is the attribution: that a saved client gets the sales typed under their
/// phone or under their name spelled differently, that what they owe is counted, and that a
/// customer nobody saved still shows up in the ranking.
/// </summary>
public class ClientsTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory));
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing over */ }
    }

    private sealed record Session(string Token, string GroupSession);

    private async Task<Session> SignUpOwnerAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"));
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("patron@lonnii.test", "MotDePasse123"))).Content.ReadFromJsonAsync<LoginResponse>())!;

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
        {
            Content = JsonContent.Create(new CreateGroupeRequest("Boutique")),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var groupe = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>())!;

        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupe.Id}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = (await (await _client.SendAsync(open)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;

        return new Session(login.AccessToken, scoped.SessionToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Session session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task SellAsync(Session owner, decimal price, decimal paid, string? nom, string? telephone = null)
    {
        var product = await (await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest($"Article {Guid.NewGuid():N}", price, Quantity: 10)))
            .Content.ReadFromJsonAsync<ProductDto>();

        var response = await SendAsync(HttpMethod.Post, "/api/ventes", owner,
            new CreateVenteRequest([new CartItemRequest(product!.Id, 1, price)], "cash", paid,
                ClientNom: nom, ClientTelephone: telephone));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<List<ClientDto>> ListAsync(Session owner) =>
        (await (await SendAsync(HttpMethod.Get, "/api/ventes/clients", owner)).Content.ReadFromJsonAsync<List<ClientDto>>())!;

    [Fact]
    public async Task ASavedClient_GetsSalesTypedUnderTheirPhoneOrTheirNameSpelledDifferently()
    {
        var owner = await SignUpOwnerAsync();
        (await SendAsync(HttpMethod.Post, "/api/ventes/clients", owner,
            new SaveClientRequest("Awa Diallo", "690 00 00 01"))).EnsureSuccessStatusCode();

        await SellAsync(owner, 10000m, 10000m, "  awa   DIALLO ");
        await SellAsync(owner, 5000m, 0m, "Madame Awa", "690000001");

        var awa = Assert.Single(await ListAsync(owner));
        Assert.True(awa.IsRegistered);
        Assert.Equal(2, awa.NombreAchats);
        Assert.Equal(15000m, awa.TotalAchats);
        Assert.Equal(5000m, awa.ResteDu);
        Assert.Equal(1, awa.FacturesImpayees);
    }

    [Fact]
    public async Task ACustomerNobodySaved_StillAppearsInTheRanking_BestBuyerFirst()
    {
        var owner = await SignUpOwnerAsync();
        (await SendAsync(HttpMethod.Post, "/api/ventes/clients", owner,
            new SaveClientRequest("Petit Client"))).EnsureSuccessStatusCode();

        await SellAsync(owner, 2000m, 2000m, "Petit Client");
        await SellAsync(owner, 30000m, 30000m, "Gros Acheteur");
        await SellAsync(owner, 1000m, 1000m, null);

        var list = await ListAsync(owner);

        Assert.Equal(["Gros Acheteur", "Petit Client"], list.Select(c => c.Nom));
        Assert.False(list[0].IsRegistered);
        Assert.Equal(30000m, list[0].TotalAchats);
    }

    [Fact]
    public async Task TwoClientsWithTheSameName_AreRefused_WhenNothingTellsThemApart()
    {
        var owner = await SignUpOwnerAsync();
        (await SendAsync(HttpMethod.Post, "/api/ventes/clients", owner,
            new SaveClientRequest("Awa Diallo"))).EnsureSuccessStatusCode();

        var duplicate = await SendAsync(HttpMethod.Post, "/api/ventes/clients", owner,
            new SaveClientRequest("AWA  diallo"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task TwoClientsWithTheSameName_AreAllowed_WhenAPhoneOrEmailTellsThemApart()
    {
        var owner = await SignUpOwnerAsync();
        (await SendAsync(HttpMethod.Post, "/api/ventes/clients", owner,
            new SaveClientRequest("Awa Diallo", Telephone: "690000001"))).EnsureSuccessStatusCode();

        var secondByPhone = await SendAsync(HttpMethod.Post, "/api/ventes/clients", owner,
            new SaveClientRequest("Awa Diallo", Telephone: "690000002"));
        Assert.True(secondByPhone.IsSuccessStatusCode, await secondByPhone.Content.ReadAsStringAsync());

        var thirdByEmail = await SendAsync(HttpMethod.Post, "/api/ventes/clients", owner,
            new SaveClientRequest("Awa Diallo", Email: "awa@example.test"));
        Assert.True(thirdByEmail.IsSuccessStatusCode, await thirdByEmail.Content.ReadAsStringAsync());

        // Sales typed under each phone must go to that Awa Diallo, not to whichever was saved first.
        await SellAsync(owner, 10000m, 10000m, "Awa Diallo", "690000001");
        await SellAsync(owner, 20000m, 20000m, "Awa Diallo", "690000002");

        var list = await ListAsync(owner);
        Assert.Equal(3, list.Count);
        Assert.Equal(10000m, list.Single(c => c.Telephone == "690000001").TotalAchats);
        Assert.Equal(20000m, list.Single(c => c.Telephone == "690000002").TotalAchats);
        Assert.Equal(0m, list.Single(c => c.Email == "awa@example.test").TotalAchats);
    }
}
