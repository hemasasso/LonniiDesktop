using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Covers Paramètres → Données de l'espace: downloading an espace as one <c>.db</c> file and
/// loading it into another one.
///
/// The round trip is what is worth proving, and only an end-to-end test proves it: the rows
/// are copied table by table with the integer keys renumbered as they go (see
/// <c>EspaceCopier</c>), so a mistake there does not fail - it quietly files a sale under the
/// wrong caisse. The other two tests cover the refusals, which are the whole safety story:
/// an espace that already holds data is never written over, and a file that is not an archive
/// is turned away before anything is touched.
/// </summary>
public class EspaceTransferTests : IAsyncLifetime
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

    private sealed record Session(string Token, string GroupSession, string GroupId);

    // --- Plumbing ---

    private static byte[] MakeImageBytes()
    {
        using var bitmap = new Bitmap(24, 24);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Tomato);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private async Task<string> SignUpOwnerAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(OwnerEmail, OwnerPassword, "patron"));

        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(OwnerEmail, OwnerPassword))).Content.ReadFromJsonAsync<LoginResponse>())!;

        return login.AccessToken;
    }

    /// <summary>Creates an espace and opens a session in it.</summary>
    private async Task<Session> NewEspaceAsync(string token, string nom)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
        {
            Content = JsonContent.Create(new CreateGroupeRequest(nom, GestionAccess: true)),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(create);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var groupe = (await response.Content.ReadFromJsonAsync<GroupeDto>())!;

        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupe.Id}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var scoped = (await (await _client.SendAsync(open)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;

        return new Session(token, scoped.SessionToken, groupe.Id);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, Session session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UploadImageAsync(string url, Session session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(MakeImageBytes());
        content.Headers.ContentType = new("image/png");
        form.Add(content, "file", "photo.png");
        request.Content = form;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task<byte[]> ExportAsync(Session session)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/parametres/espace/export", session);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.True(int.Parse(response.Headers.GetValues("x-lonnii-records").First()) > 0);

        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<HttpResponseMessage> ImportAsync(Session session, byte[] archive)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/parametres/espace/import");
        request.Content = new ByteArrayContent(archive);
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    /// <summary>
    /// Fills an espace with something of every shape the copier has to handle: a product with
    /// a photo, a sale with a line item and a payment, a caisse session the sale is filed
    /// under, a charge, and the espace's own currency and receipt settings.
    /// </summary>
    private async Task<ProductDto> FillAsync(Session session)
    {
        (await SendAsync(HttpMethod.Put, "/api/groupe/currency", session,
            new UpdateCurrencyRequest("EUR", CurrencyBefore: true))).EnsureSuccessStatusCode();

        var caisse = await SendAsync(HttpMethod.Post, "/api/caisse/ouvrir", session,
            new OpenCaisseRequest(50000m));
        Assert.True(caisse.IsSuccessStatusCode, await caisse.Content.ReadAsStringAsync());

        var category = (await (await SendAsync(HttpMethod.Post, "/api/stock/categories", session,
            new SaveCategoryRequest("Boissons"))).Content.ReadFromJsonAsync<CategoryDto>())!;

        var product = (await (await SendAsync(HttpMethod.Post, "/api/stock/products", session,
            new SaveProductRequest("Café", 2500m, Quantity: 40, CategoryId: category.Id)))
            .Content.ReadFromJsonAsync<ProductDto>())!;

        (await UploadImageAsync($"/api/stock/products/{product.Id}/image", session)).EnsureSuccessStatusCode();
        (await UploadImageAsync("/api/parametres/recu/logo", session)).EnsureSuccessStatusCode();

        var vente = await SendAsync(HttpMethod.Post, "/api/ventes", session,
            new CreateVenteRequest([new CartItemRequest(product.Id, 2, 2500m)], "cash", 5000m,
                ClientNom: "Awa Diallo"));
        Assert.True(vente.IsSuccessStatusCode, await vente.Content.ReadAsStringAsync());

        (await SendAsync(HttpMethod.Post, "/api/charges", session,
            new SaveChargeRequest("Loyer janvier", 120000m, "fixe", "Loyer", DateTime.Today)))
            .EnsureSuccessStatusCode();

        return product;
    }

    private async Task<List<ProductDto>> ProductsAsync(Session session) =>
        (await (await SendAsync(HttpMethod.Get, "/api/stock/products", session))
            .Content.ReadFromJsonAsync<List<ProductDto>>())!;

    // --- Tests ---

    [Fact]
    public async Task AnExportedEspace_LoadsIntoAnEmptyOne_WithItsRowsItsPhotosAndItsSettings()
    {
        var token = await SignUpOwnerAsync();
        var original = await NewEspaceAsync(token, "Boutique");
        await FillAsync(original);

        var archive = await ExportAsync(original);
        Assert.True(archive.Length > 0);

        var fresh = await NewEspaceAsync(token, "Boutique 2");

        var response = await ImportAsync(fresh, archive);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var result = (await response.Content.ReadFromJsonAsync<EspaceImportResultDto>())!;
        Assert.Equal("Boutique", result.SourceGroupName);
        Assert.True(result.RecordCount > 0);

        // The product came across with its photo, and the photo is readable in the new
        // espace - which is the part a database-only copy would get wrong.
        var products = await ProductsAsync(fresh);
        var copied = Assert.Single(products);
        Assert.Equal("Café", copied.Name);
        Assert.Equal(38, copied.Quantity);
        Assert.False(string.IsNullOrWhiteSpace(copied.ImageUrl));

        var photo = await SendAsync(HttpMethod.Get, copied.ImageUrl!, fresh);
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);

        // The sale, its line item and its payment.
        var ventes = (await (await SendAsync(HttpMethod.Get, "/api/ventes", fresh))
            .Content.ReadFromJsonAsync<VentesListResponse>())!;
        var vente = Assert.Single(ventes.Ventes);
        Assert.Equal(5000m, vente.MontantTotal);
        Assert.Equal(5000m, vente.MontantPaye);

        // The espace's own settings travelled with it; its name did not.
        var groupes = await ListGroupesAsync(token);
        var target = groupes.Single(g => g.Id == fresh.GroupId);
        Assert.Equal("Boutique 2", target.Nom);
        Assert.Equal("EUR", target.CurrencyLabel);
        Assert.True(target.CurrencyBefore);

        // The receipt logo is stored under the receiving espace's own id, so it is served to
        // this session rather than refused as another workspace's file.
        var settings = (await (await SendAsync(HttpMethod.Get, "/api/parametres/recu", fresh))
            .Content.ReadFromJsonAsync<ReceiptSettingsDto>())!;
        Assert.False(string.IsNullOrWhiteSpace(settings.LogoUrl));
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Get, settings.LogoUrl!, fresh)).StatusCode);

        // And the original is untouched.
        Assert.Single(await ProductsAsync(original));
    }

    [Fact]
    public async Task AnEspaceThatAlreadyHoldsData_RefusesAnImport_AndSaysWhatItFound()
    {
        var token = await SignUpOwnerAsync();
        var original = await NewEspaceAsync(token, "Boutique");
        await FillAsync(original);

        var archive = await ExportAsync(original);

        // Importing back into the espace it came from is the same refusal: it is not empty.
        var response = await ImportAsync(original, archive);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiError>())!;
        Assert.Contains("Produits", error.Error);

        // Nothing was doubled by the attempt.
        Assert.Single(await ProductsAsync(original));
    }

    [Fact]
    public async Task AFileThatIsNotAnArchive_IsRefusedWithoutTouchingTheEspace()
    {
        var token = await SignUpOwnerAsync();
        var espace = await NewEspaceAsync(token, "Boutique");

        var response = await ImportAsync(espace, MakeImageBytes());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<ApiError>())!;
        Assert.Contains("sauvegarde d'espace Lonnii", error.Error);

        Assert.Empty(await ProductsAsync(espace));
    }

    private async Task<List<GroupeDto>> ListGroupesAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/groupes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<List<GroupeDto>>())!;
    }
}
