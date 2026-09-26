using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Every endpoint that hands back a <see cref="ProductDto"/> must name the product's
/// category and supplier, not just carry their ids. <c>Product.Category</c>/
/// <c>Product.Supplier</c> are never eager-loaded by EF Core on their own, and
/// <c>ToDto</c>'s call is not something the query provider can turn into a JOIN by
/// itself - without an explicit <c>Include</c> (or an explicit reload after a save),
/// <c>CategoryName</c>/<c>SupplierName</c> come back null regardless of what
/// <c>CategoryId</c>/<c>SupplierId</c> are actually set to, which is exactly what made a
/// "Valeur du Stock par Catégorie" chart read 100% "Sans catégorie" no matter how the
/// products were actually categorised.
/// </summary>
public class ProductCategoryLookupTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string OwnerEmail = "patron@lonnii.test";
    private const string OwnerPassword = "MotDePasse123";

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
            new RegisterRequest(OwnerEmail, OwnerPassword, "patron"));

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(OwnerEmail, OwnerPassword));
        loginResponse.EnsureSuccessStatusCode();
        var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
        {
            Content = JsonContent.Create(new CreateGroupeRequest("Boutique")),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var groupe = await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>();

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupe!.Id}/session");
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = await (await _client.SendAsync(sessionRequest)).Content.ReadFromJsonAsync<GroupSessionResponse>();

        return new Session(login.AccessToken, scoped!.SessionToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Session session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task<string> CreateCategoryAsync(Session owner, string name)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/stock/categories", owner, new SaveCategoryRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!.Id;
    }

    [Fact]
    public async Task Create_NamesTheCategoryOnTheProductItJustReturned()
    {
        var owner = await SignUpOwnerAsync();
        var categoryId = await CreateCategoryAsync(owner, "Boissons");

        var response = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Coca-Cola", 1000, CategoryId: categoryId));
        response.EnsureSuccessStatusCode();
        var product = (await response.Content.ReadFromJsonAsync<ProductDto>())!;

        Assert.Equal("Boissons", product.CategoryName);
    }

    [Fact]
    public async Task List_NamesEveryProductsCategory()
    {
        var owner = await SignUpOwnerAsync();
        var categoryId = await CreateCategoryAsync(owner, "Boissons");
        await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Coca-Cola", 1000, CategoryId: categoryId));

        var products = await (await SendAsync(HttpMethod.Get, "/api/stock/products", owner))
            .Content.ReadFromJsonAsync<List<ProductDto>>();

        Assert.Equal("Boissons", Assert.Single(products!).CategoryName);
    }

    [Fact]
    public async Task Get_NamesTheProductsCategory()
    {
        var owner = await SignUpOwnerAsync();
        var categoryId = await CreateCategoryAsync(owner, "Boissons");
        var created = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Coca-Cola", 1000, CategoryId: categoryId));
        var productId = (await created.Content.ReadFromJsonAsync<ProductDto>())!.Id;

        var fetched = await (await SendAsync(HttpMethod.Get, $"/api/stock/products/{productId}", owner))
            .Content.ReadFromJsonAsync<ProductDto>();

        Assert.Equal("Boissons", fetched!.CategoryName);
    }

    [Fact]
    public async Task Update_NamesTheNewCategoryAfterChangingIt()
    {
        var owner = await SignUpOwnerAsync();
        var firstCategoryId = await CreateCategoryAsync(owner, "Boissons");
        var secondCategoryId = await CreateCategoryAsync(owner, "Épicerie");
        var created = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Coca-Cola", 1000, CategoryId: firstCategoryId));
        var productId = (await created.Content.ReadFromJsonAsync<ProductDto>())!.Id;

        var updated = await SendAsync(HttpMethod.Put, $"/api/stock/products/{productId}", owner,
            new SaveProductRequest("Coca-Cola", 1000, CategoryId: secondCategoryId));
        var product = await updated.Content.ReadFromJsonAsync<ProductDto>();

        Assert.Equal("Épicerie", product!.CategoryName);
    }

    [Fact]
    public async Task AdjustStock_StillNamesTheCategoryOnTheProductItReturns()
    {
        var owner = await SignUpOwnerAsync();
        var categoryId = await CreateCategoryAsync(owner, "Boissons");
        var created = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest("Coca-Cola", 1000, CategoryId: categoryId, Quantity: 10));
        var productId = (await created.Content.ReadFromJsonAsync<ProductDto>())!.Id;

        var adjusted = await SendAsync(HttpMethod.Post, $"/api/stock/products/{productId}/adjust", owner,
            new AdjustStockRequest(5, "ajout"));
        var product = await adjusted.Content.ReadFromJsonAsync<ProductDto>();

        Assert.Equal("Boissons", product!.CategoryName);
    }
}
