using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Vente Mixte: a product sold in two units - a base/detail unit (tracked by
/// <c>Product.Quantity</c>) and a bulk one (e.g. "Carton"), related by a conversion factor.
/// Selling by the bulk unit must charge the bulk price and take the base stock down by the
/// factor, not by 1 - and the product form must not accept the flag without the fields that
/// make it meaningful.
/// </summary>
public class VenteMixteTests : IAsyncLifetime
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

    private async Task<ProductDto> CreateSodaAsync(Session owner, int quantity = 120)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest(
                "Soda", Price: 12000, Quantity: quantity, PrixFixe: true,
                VenteMixte: true, UniteVente: "Carton", FacteurConversion: 12, PrixVenteDetail: 1000));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    [Fact]
    public async Task VenteMixte_WithoutTheBulkFields_IsRejected()
    {
        var owner = await SignUpOwnerAsync();

        var missingUnit = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Soda", 12000, VenteMixte: true, FacteurConversion: 12, PrixVenteDetail: 1000));
        Assert.Equal(HttpStatusCode.BadRequest, missingUnit.StatusCode);

        var tooSmallFactor = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Soda", 12000, VenteMixte: true, UniteVente: "Carton", FacteurConversion: 1, PrixVenteDetail: 1000));
        Assert.Equal(HttpStatusCode.BadRequest, tooSmallFactor.StatusCode);

        var missingDetailPrice = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Soda", 12000, VenteMixte: true, UniteVente: "Carton", FacteurConversion: 12));
        Assert.Equal(HttpStatusCode.BadRequest, missingDetailPrice.StatusCode);
    }

    [Fact]
    public async Task SellingByTheDetailUnit_ChargesTheDetailPrice_AndTakesOneUnitOffStock()
    {
        var owner = await SignUpOwnerAsync();
        var soda = await CreateSodaAsync(owner);

        var sale = await SendAsync(HttpMethod.Post, "/api/ventes", owner,
            new CreateVenteRequest([new CartItemRequest(soda.Id, 3)], "cash", 3000));
        sale.EnsureSuccessStatusCode();
        var vente = (await sale.Content.ReadFromJsonAsync<VenteDto>())!;

        Assert.Equal(1000m, vente.Items.Single().PrixUnitaire);
        Assert.Equal(3000m, vente.MontantTotal);
        Assert.Equal("unité", vente.Items.Single().Unite);

        var product = await (await SendAsync(HttpMethod.Get, $"/api/stock/products/{soda.Id}", owner))
            .Content.ReadFromJsonAsync<ProductDto>();
        Assert.Equal(117, product!.Quantity);
    }

    [Fact]
    public async Task SellingByTheBulkUnit_ChargesTheBulkPrice_AndTakesTheConversionFactorOffStock()
    {
        var owner = await SignUpOwnerAsync();
        var soda = await CreateSodaAsync(owner);

        var sale = await SendAsync(HttpMethod.Post, "/api/ventes", owner,
            new CreateVenteRequest([new CartItemRequest(soda.Id, 2, VenteEnGros: true)], "cash", 24000));
        sale.EnsureSuccessStatusCode();
        var vente = (await sale.Content.ReadFromJsonAsync<VenteDto>())!;

        var line = vente.Items.Single();
        Assert.Equal(12000m, line.PrixUnitaire);
        Assert.Equal(24000m, vente.MontantTotal);
        Assert.Equal("Carton", line.Unite);

        var product = await (await SendAsync(HttpMethod.Get, $"/api/stock/products/{soda.Id}", owner))
            .Content.ReadFromJsonAsync<ProductDto>();
        Assert.Equal(96, product!.Quantity); // 120 - 2*12
    }

    [Fact]
    public async Task SellingByTheBulkUnit_WithNotEnoughBaseStock_IsRejected()
    {
        var owner = await SignUpOwnerAsync();
        var soda = await CreateSodaAsync(owner, quantity: 10); // less than one carton (12)

        var sale = await SendAsync(HttpMethod.Post, "/api/ventes", owner,
            new CreateVenteRequest([new CartItemRequest(soda.Id, 1, VenteEnGros: true)], "cash", 12000));

        Assert.Equal(HttpStatusCode.BadRequest, sale.StatusCode);

        var product = await (await SendAsync(HttpMethod.Get, $"/api/stock/products/{soda.Id}", owner))
            .Content.ReadFromJsonAsync<ProductDto>();
        Assert.Equal(10, product!.Quantity); // untouched
    }

    [Fact]
    public void QuantityDisplay_SplitsBaseUnitsIntoWholeBulkUnitsAndARemainder()
    {
        var product = new ProductDto(
            "id", "Paracetamol", null, null, null, null, null, null, null,
            Quantity: 192, MinimumThreshold: 5, CostPrice: null, Price: 1000, PrixFixe: true,
            VenteLibre: false, StockIllimite: false, UniteAffichage: null, IsActive: true,
            StorageLocation: null, ExpiryDate: null, ImageUrl: null, UpdatedAt: DateTime.UtcNow,
            VenteMixte: true, UniteVente: "Paquet", FacteurConversion: 10);

        Assert.Equal("19 Paquet & 2 unité", product.QuantityDisplay);
    }

    [Fact]
    public void QuantityDisplay_ShowsOnlyTheBulkUnit_WhenThereIsNoRemainder()
    {
        var product = new ProductDto(
            "id", "Paracetamol", null, null, null, null, null, null, null,
            Quantity: 200, MinimumThreshold: 5, CostPrice: null, Price: 1000, PrixFixe: true,
            VenteLibre: false, StockIllimite: false, UniteAffichage: null, IsActive: true,
            StorageLocation: null, ExpiryDate: null, ImageUrl: null, UpdatedAt: DateTime.UtcNow,
            VenteMixte: true, UniteVente: "Paquet", FacteurConversion: 10);

        Assert.Equal("20 Paquet", product.QuantityDisplay);
    }

    [Fact]
    public void QuantityDisplay_ShowsOnlyTheDetailUnit_WhenThereIsNotEvenOneBulkUnit()
    {
        var product = new ProductDto(
            "id", "Paracetamol", null, null, null, null, null, null, null,
            Quantity: 4, MinimumThreshold: 5, CostPrice: null, Price: 1000, PrixFixe: true,
            VenteLibre: false, StockIllimite: false, UniteAffichage: "sachet", IsActive: true,
            StorageLocation: null, ExpiryDate: null, ImageUrl: null, UpdatedAt: DateTime.UtcNow,
            VenteMixte: true, UniteVente: "Paquet", FacteurConversion: 10);

        Assert.Equal("4 sachet", product.QuantityDisplay);
    }

    [Fact]
    public async Task AnOrdinaryProduct_HasNoUnitOnItsSaleLine()
    {
        var owner = await SignUpOwnerAsync();
        var product = (await (await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Savon", 500, Quantity: 10, PrixFixe: true)))
            .Content.ReadFromJsonAsync<ProductDto>())!;

        var sale = await SendAsync(HttpMethod.Post, "/api/ventes", owner,
            new CreateVenteRequest([new CartItemRequest(product.Id, 1)], "cash", 500));
        sale.EnsureSuccessStatusCode();
        var vente = (await sale.Content.ReadFromJsonAsync<VenteDto>())!;

        Assert.Null(vente.Items.Single().Unite);
    }
}
