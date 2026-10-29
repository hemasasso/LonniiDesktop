using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Several tills validating at the same instant. Every one of them is served by the same API, so
/// what must hold is: stock never sold twice, never left wrong, and no two sales sharing a number.
/// </summary>
public class SimultaneousSalesTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private string _token = null!, _groupSession = null!;

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory));
        _client = _factory.CreateClient();

        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"));
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("patron@lonnii.test", "MotDePasse123"))).Content.ReadFromJsonAsync<LoginResponse>())!;
        _token = login.AccessToken;

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
        {
            Content = JsonContent.Create(new CreateGroupeRequest("Boutique")),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var groupe = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>())!;

        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupe.Id}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _groupSession = (await (await _client.SendAsync(open)).Content.ReadFromJsonAsync<GroupSessionResponse>())!.SessionToken;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Add("x-group-session", _groupSession);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Ten_tills_selling_the_last_five_never_oversell_or_share_a_number()
    {
        var product = await (await SendAsync(HttpMethod.Post, "/api/stock/products",
            new SaveProductRequest("Savon", 500m, Quantity: 5))).Content.ReadFromJsonAsync<ProductDto>();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            SendAsync(HttpMethod.Post, "/api/ventes",
                new CreateVenteRequest([new CartItemRequest(product!.Id, 1, 500m)], "cash", 500m))));

        var sold = responses.Where(r => r.IsSuccessStatusCode).ToList();
        Assert.Equal(5, sold.Count);
        Assert.All(responses.Where(r => !r.IsSuccessStatusCode),
            r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));

        var numbers = new List<string>();
        foreach (var r in sold) numbers.Add((await r.Content.ReadFromJsonAsync<VenteDto>())!.NumeroVente);
        Assert.Equal(5, numbers.Distinct().Count());

        var after = await (await SendAsync(HttpMethod.Get, $"/api/stock/products/{product!.Id}"))
            .Content.ReadFromJsonAsync<ProductDto>();
        Assert.Equal(0, after!.Quantity);
    }

    [Fact]
    public async Task Simultaneous_sales_of_unlimited_stock_all_go_through_with_their_own_numbers()
    {
        var product = await (await SendAsync(HttpMethod.Post, "/api/stock/products",
            new SaveProductRequest("Pain", 200m, Quantity: 1000))).Content.ReadFromJsonAsync<ProductDto>();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            SendAsync(HttpMethod.Post, "/api/ventes",
                new CreateVenteRequest([new CartItemRequest(product!.Id, 2, 200m)], "cash", 400m))));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode));
        var numbers = new List<string>();
        foreach (var r in responses) numbers.Add((await r.Content.ReadFromJsonAsync<VenteDto>())!.NumeroVente);
        Assert.Equal(12, numbers.Distinct().Count());

        var after = await (await SendAsync(HttpMethod.Get, $"/api/stock/products/{product!.Id}"))
            .Content.ReadFromJsonAsync<ProductDto>();
        Assert.Equal(1000 - 24, after!.Quantity);
    }
}
