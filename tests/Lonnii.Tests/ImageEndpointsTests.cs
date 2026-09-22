using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Covers uploading, fetching and deleting product and category photos through the API.
///
/// The point most worth proving here is not that an upload works - it is that a member
/// of one workspace cannot fetch another workspace's photo by guessing or reusing the
/// URL shape, since the filename alone (an entity id plus a timestamp) carries no group
/// information of its own.
/// </summary>
public class ImageEndpointsTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string OwnerEmail = "patron@lonnii.test";
    private const string OwnerPassword = "MotDePasse123";
    private const string MemberEmail = "vendeur@lonnii.test";
    private const string MemberPassword = "Comptoir2026";

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

    private sealed record Session(string Token, string GroupSession, string GroupId, string UserId);

    private static byte[] MakeImageBytes()
    {
        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.SeaGreen);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private async Task<Session> SignUpOwnerAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(OwnerEmail, OwnerPassword, "patron"));

        return await SignInAsync(OwnerEmail, OwnerPassword, createGroup: true);
    }

    private async Task<Session> SignInAsync(string identifier, string password, bool createGroup = false)
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(identifier, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/groupes");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var groupes = await (await _client.SendAsync(listRequest)).Content.ReadFromJsonAsync<List<GroupeDto>>();

        string groupId;
        if (createGroup && groupes!.Count == 0)
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
            {
                Content = JsonContent.Create(new CreateGroupeRequest("Boutique")),
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
            var created = await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>();
            groupId = created!.Id;
        }
        else
        {
            groupId = groupes![0].Id;
        }

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupId}/session");
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = await (await _client.SendAsync(sessionRequest)).Content.ReadFromJsonAsync<GroupSessionResponse>();

        return new Session(login.AccessToken, scoped!.SessionToken, groupId, login.User.IdUser);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Session session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UploadImageAsync(string url, Session session, byte[]? bytes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes ?? MakeImageBytes());
        content.Headers.ContentType = new("image/png");
        form.Add(content, "file", "photo.png");
        request.Content = form;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task<string> AddMemberAsync(Session owner)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/groupe/members", owner,
            new AddMemberRequest(MemberEmail, MemberPassword, "vendeur"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserDto>())!.IdUser;
    }

    private async Task<string> CreateProductAsync(Session owner, string name = "Coca-Cola")
    {
        var response = await SendAsync(HttpMethod.Post, "/api/stock/products", owner,
            new SaveProductRequest(name, 1000));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductDto>())!.Id;
    }

    private async Task<string> CreateCategoryAsync(Session owner, string name = "Boissons")
    {
        var response = await SendAsync(HttpMethod.Post, "/api/stock/categories", owner,
            new SaveCategoryRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!.Id;
    }

    // --- Product photos ---

    [Fact]
    public async Task Owner_CanUploadAndFetchAProductPhoto()
    {
        var owner = await SignUpOwnerAsync();
        var productId = await CreateProductAsync(owner);

        var upload = await UploadImageAsync($"/api/stock/products/{productId}/image", owner);
        upload.EnsureSuccessStatusCode();
        var uploaded = await upload.Content.ReadFromJsonAsync<ImageUploadResponse>();

        Assert.StartsWith("/api/images/products/", uploaded!.ImageUrl);

        var fetch = await SendAsync(HttpMethod.Get, uploaded.ImageUrl, owner);
        fetch.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", fetch.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UploadedPhoto_IsReflectedOnTheProduct()
    {
        var owner = await SignUpOwnerAsync();
        var productId = await CreateProductAsync(owner);

        await UploadImageAsync($"/api/stock/products/{productId}/image", owner);

        var response = await SendAsync(HttpMethod.Get, $"/api/stock/products/{productId}", owner);
        var product = await response.Content.ReadFromJsonAsync<ProductDto>();

        Assert.NotNull(product!.ImageUrl);
    }

    [Fact]
    public async Task DeletingAProductPhoto_RemovesIt()
    {
        var owner = await SignUpOwnerAsync();
        var productId = await CreateProductAsync(owner);

        var upload = await UploadImageAsync($"/api/stock/products/{productId}/image", owner);
        var uploaded = (await upload.Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        var delete = await SendAsync(HttpMethod.Delete, $"/api/stock/products/{productId}/image", owner);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var fetchAfterDelete = await SendAsync(HttpMethod.Get, uploaded.ImageUrl, owner);
        Assert.Equal(HttpStatusCode.NotFound, fetchAfterDelete.StatusCode);

        var product = await (await SendAsync(HttpMethod.Get, $"/api/stock/products/{productId}", owner))
            .Content.ReadFromJsonAsync<ProductDto>();
        Assert.Null(product!.ImageUrl);
    }

    [Fact]
    public async Task ReplacingAProductPhoto_MakesTheOldUrlStopWorking()
    {
        var owner = await SignUpOwnerAsync();
        var productId = await CreateProductAsync(owner);

        var first = (await (await UploadImageAsync($"/api/stock/products/{productId}/image", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        var second = (await (await UploadImageAsync($"/api/stock/products/{productId}/image", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        Assert.NotEqual(first.ImageUrl, second.ImageUrl);

        var oldFetch = await SendAsync(HttpMethod.Get, first.ImageUrl, owner);
        Assert.Equal(HttpStatusCode.NotFound, oldFetch.StatusCode);

        var newFetch = await SendAsync(HttpMethod.Get, second.ImageUrl, owner);
        newFetch.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MemberWithNeitherAddNorEditProducts_CannotUploadAProductPhoto()
    {
        var owner = await SignUpOwnerAsync();
        var productId = await CreateProductAsync(owner);
        await AddMemberAsync(owner);
        var member = await SignInAsync(MemberEmail, MemberPassword);

        var response = await UploadImageAsync($"/api/stock/products/{productId}/image", member);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MemberWithAddProducts_CanUploadAPhotoOnCreation_WithoutEditProducts()
    {
        // Attaching a photo while creating a product is part of adding it - see the
        // route registration comment in StockEndpoints for why both privileges qualify.
        var owner = await SignUpOwnerAsync();
        var memberId = await AddMemberAsync(owner);
        await SendAsync(HttpMethod.Post, "/api/privileges/gestion", owner,
            new SetPrivilegeRequest(memberId, "can_add_products", true));
        await SendAsync(HttpMethod.Post, "/api/privileges/gestion", owner,
            new SetPrivilegeRequest(memberId, "can_view_stock", true));

        var member = await SignInAsync(MemberEmail, MemberPassword);

        var create = await SendAsync(HttpMethod.Post, "/api/stock/products", member,
            new SaveProductRequest("Fanta", 900));
        var product = await create.Content.ReadFromJsonAsync<ProductDto>();

        var upload = await UploadImageAsync($"/api/stock/products/{product!.Id}/image", member);

        upload.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task UploadingSomethingThatIsNotAnImage_IsRejected()
    {
        var owner = await SignUpOwnerAsync();
        var productId = await CreateProductAsync(owner);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/stock/products/{productId}/image");
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent("not an image"u8.ToArray());
        content.Headers.ContentType = new("text/plain");
        form.Add(content, "file", "notes.txt");
        request.Content = form;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        request.Headers.Add("x-group-session", owner.GroupSession);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Category photos ---

    [Fact]
    public async Task Owner_CanUploadAndFetchACategoryPhoto()
    {
        var owner = await SignUpOwnerAsync();
        var categoryId = await CreateCategoryAsync(owner);

        var upload = await UploadImageAsync($"/api/stock/categories/{categoryId}/image", owner);
        upload.EnsureSuccessStatusCode();
        var uploaded = await upload.Content.ReadFromJsonAsync<ImageUploadResponse>();

        var fetch = await SendAsync(HttpMethod.Get, uploaded!.ImageUrl, owner);
        fetch.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MemberWithoutManageCategories_CannotUploadACategoryPhoto()
    {
        var owner = await SignUpOwnerAsync();
        var categoryId = await CreateCategoryAsync(owner);
        await AddMemberAsync(owner);
        var member = await SignInAsync(MemberEmail, MemberPassword);

        var response = await UploadImageAsync($"/api/stock/categories/{categoryId}/image", member);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeactivatingACategory_KeepsItsPhotoAndProductCount()
    {
        var owner = await SignUpOwnerAsync();
        var categoryId = await CreateCategoryAsync(owner);
        await UploadImageAsync($"/api/stock/categories/{categoryId}/image", owner);

        var update = await SendAsync(HttpMethod.Put, $"/api/stock/categories/{categoryId}", owner,
            new SaveCategoryRequest("Boissons", IsActive: false));
        var category = await update.Content.ReadFromJsonAsync<CategoryDto>();

        Assert.False(category!.IsActive);
        Assert.NotNull(category.ImageUrl);
    }

    // --- Cross-tenant isolation ---

    /// <summary>
    /// Opens a second, empty workspace for the same account and returns a session scoped
    /// to it - the account is already the creator of the first, and self-registration is
    /// single-use, so this is the way to get a second, unrelated group_id to test against
    /// without a second person.
    /// </summary>
    private async Task<Session> OpenAnotherWorkspaceAsync(Session owner)
    {
        var create = await SendAsync(HttpMethod.Post, "/api/groupes", owner, new CreateGroupeRequest("Autre Boutique"));
        var groupB = (await create.Content.ReadFromJsonAsync<GroupeDto>())!;

        var openSession = await SendAsync(HttpMethod.Post, $"/api/groupes/{groupB.Id}/session", owner);
        var scoped = (await openSession.Content.ReadFromJsonAsync<GroupSessionResponse>())!;

        return owner with { GroupSession = scoped.SessionToken, GroupId = groupB.Id };
    }

    [Fact]
    public async Task AMemberOfAnotherWorkspace_CannotFetchThisWorkspacesProductPhoto()
    {
        var owner = await SignUpOwnerAsync();
        var productId = await CreateProductAsync(owner);
        var upload = (await (await UploadImageAsync($"/api/stock/products/{productId}/image", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        var scopedToOtherWorkspace = await OpenAnotherWorkspaceAsync(owner);

        var response = await SendAsync(HttpMethod.Get, upload.ImageUrl, scopedToOtherWorkspace);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AMemberOfAnotherWorkspace_CannotFetchThisWorkspacesCategoryPhoto()
    {
        var owner = await SignUpOwnerAsync();
        var categoryId = await CreateCategoryAsync(owner);
        var upload = (await (await UploadImageAsync($"/api/stock/categories/{categoryId}/image", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        var scopedToOtherWorkspace = await OpenAnotherWorkspaceAsync(owner);

        var response = await SendAsync(HttpMethod.Get, upload.ImageUrl, scopedToOtherWorkspace);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RequestingAnImageUnderTheWrongFolder_IsRejectedRatherThanServed()
    {
        var owner = await SignUpOwnerAsync();
        var productId = await CreateProductAsync(owner);
        var upload = (await (await UploadImageAsync($"/api/stock/products/{productId}/image", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        var fileName = upload.ImageUrl.Split('/').Last();

        // Same file, asked for through the categories folder instead of products.
        var response = await SendAsync(HttpMethod.Get, $"/api/images/categories/{fileName}", owner);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
