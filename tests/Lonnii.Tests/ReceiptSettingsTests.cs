using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Covers "Paramètre Reçu et Facture" - the one <c>ventes_parametres</c> row per workspace
/// that decides what a printed reçu or facture says.
///
/// The points worth proving are the ones the web app's own route gets wrong or leaves
/// implicit: that a workspace with no row still reads back a complete, printable
/// configuration; that a till operator can read it but not rewrite it; that the QR code is
/// stored losslessly; and that one shop's logo is not reachable from another's session.
/// </summary>
public class ReceiptSettingsTests : IAsyncLifetime
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

    // --- Plumbing, same shape as ImageEndpointsTests ---

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

    private async Task<HttpResponseMessage> UploadAsync(string url, Session session, byte[]? bytes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes ?? MakeImageBytes());
        content.Headers.ContentType = new("image/png");
        form.Add(content, "file", "image.png");
        request.Content = form;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task AddMemberAsync(Session owner)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/groupe/members", owner,
            new AddMemberRequest(MemberEmail, MemberPassword, "vendeur"));
        response.EnsureSuccessStatusCode();
    }

    private async Task<ReceiptSettingsDto> GetAsync(Session session)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/parametres/recu", session);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReceiptSettingsDto>())!;
    }

    /// <summary>A request that changes only what the caller names, leaving everything else
    /// at whatever <paramref name="from"/> holds - so a test does not have to restate
    /// fifteen fields to change one.</summary>
    private static UpdateReceiptSettingsRequest From(
        ReceiptSettingsDto from,
        string? companyName = null,
        string? noteUnderQr = null,
        string? receiptTitle = null,
        string? factureTitle = null,
        string? sellerLabel = null,
        string? fontFamily = null,
        int? fontSize = null) => new(
            CompanyName: companyName ?? from.CompanyName,
            NoteUnderQr: noteUnderQr ?? from.NoteUnderQr,
            ReceiptTitle: receiptTitle ?? from.ReceiptTitle,
            FactureTitle: factureTitle ?? from.FactureTitle,
            FactureNoticeTitle: from.FactureNoticeTitle,
            FactureNoticeText: from.FactureNoticeText,
            FactureFooterText: from.FactureFooterText,
            ReceiptFooterText: from.ReceiptFooterText,
            SellerLabel: sellerLabel ?? from.SellerLabel,
            AvoirNoticeTitle: from.AvoirNoticeTitle,
            AvoirNoticeText: from.AvoirNoticeText,
            FontFamily: fontFamily ?? from.FontFamily,
            FontSize: fontSize ?? from.FontSize,
            ReceiptTitleFontSize: from.ReceiptTitleFontSize,
            FactureTitleFontSize: from.FactureTitleFontSize);

    // --- Defaults ---

    /// <summary>
    /// A workspace only gets a row the first time someone saves. Until then the read has to
    /// answer with something printable, or the very first sale of a new shop produces a
    /// receipt with blanks where its titles belong.
    /// </summary>
    [Fact]
    public async Task AWorkspaceThatHasNeverSavedAnything_ReadsBackACompleteConfiguration()
    {
        var owner = await SignUpOwnerAsync();

        var settings = await GetAsync(owner);

        Assert.Null(settings.CompanyName);
        Assert.Null(settings.LogoUrl);
        Assert.Null(settings.QrCodeUrl);

        Assert.Equal(ReceiptSettingsDefaults.ReceiptTitle, settings.ReceiptTitle);
        Assert.Equal(ReceiptSettingsDefaults.FactureTitle, settings.FactureTitle);
        Assert.Equal(ReceiptSettingsDefaults.FactureNoticeTitle, settings.FactureNoticeTitle);
        Assert.Equal(ReceiptSettingsDefaults.FactureNoticeText, settings.FactureNoticeText);
        Assert.Equal(ReceiptSettingsDefaults.FactureFooterText, settings.FactureFooterText);
        Assert.Equal(ReceiptSettingsDefaults.ReceiptFooterText, settings.ReceiptFooterText);
        Assert.Equal(ReceiptSettingsDefaults.SellerLabel, settings.SellerLabel);
        Assert.Equal(ReceiptSettingsDefaults.AvoirNoticeTitle, settings.AvoirNoticeTitle);
        Assert.Equal(ReceiptSettingsDefaults.AvoirNoticeText, settings.AvoirNoticeText);
        Assert.Equal(ReceiptSettingsDefaults.FontFamily, settings.FontFamily);
        Assert.Equal(ReceiptSettingsDefaults.FontSize, settings.FontSize);
        Assert.Equal(ReceiptSettingsDefaults.TitleFontSize, settings.ReceiptTitleFontSize);
        Assert.Equal(ReceiptSettingsDefaults.TitleFontSize, settings.FactureTitleFontSize);
    }

    [Fact]
    public async Task SavedWording_IsReadBack()
    {
        var owner = await SignUpOwnerAsync();
        var initial = await GetAsync(owner);

        var save = await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(initial,
                companyName: "Pharmacie du Centre",
                factureTitle: "MINI FACTURE",
                sellerLabel: "Préparateur",
                noteUnderQr: "Scannez pour payer"));
        save.EnsureSuccessStatusCode();

        var settings = await GetAsync(owner);

        Assert.Equal("Pharmacie du Centre", settings.CompanyName);
        Assert.Equal("MINI FACTURE", settings.FactureTitle);
        Assert.Equal("Préparateur", settings.SellerLabel);
        Assert.Equal("Scannez pour payer", settings.NoteUnderQr);
    }

    /// <summary>
    /// The editor has no "restore default" button, so clearing a title is the only way a
    /// shop can express "I do not want my own wording here". Storing the empty string would
    /// print a receipt with nothing where REÇU DE VENTE belongs.
    /// </summary>
    [Fact]
    public async Task BlankingATitle_RestoresTheDefaultRatherThanStoringNothing()
    {
        var owner = await SignUpOwnerAsync();
        var initial = await GetAsync(owner);

        await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(initial, receiptTitle: "TICKET"));
        await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(await GetAsync(owner), receiptTitle: "   "));

        var settings = await GetAsync(owner);

        Assert.Equal(ReceiptSettingsDefaults.ReceiptTitle, settings.ReceiptTitle);
    }

    /// <summary>
    /// The host laptop and the till that prints are different machines. A typeface only one
    /// of them has would silently print as a substitute, so an unknown one is replaced here
    /// rather than stored.
    /// </summary>
    [Fact]
    public async Task AFontTheOtherTillsMayNotHave_IsReplacedByTheDefault()
    {
        var owner = await SignUpOwnerAsync();
        var initial = await GetAsync(owner);

        await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(initial, fontFamily: "Comic Sans MS"));

        var settings = await GetAsync(owner);

        Assert.Equal(ReceiptSettingsDefaults.FontFamily, settings.FontFamily);
    }

    [Theory]
    [InlineData(2, ReceiptSettingsDefaults.MinFontSize)]
    [InlineData(400, ReceiptSettingsDefaults.MaxFontSize)]
    public async Task AFontSizeOutsideTheLegibleRange_IsClamped(int requested, int expected)
    {
        var owner = await SignUpOwnerAsync();
        var initial = await GetAsync(owner);

        await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(initial, fontSize: requested));

        var settings = await GetAsync(owner);

        Assert.Equal(expected, settings.FontSize);
    }

    [Fact]
    public async Task AValueLongerThanTheColumn_IsRefusedWithAMessage()
    {
        var owner = await SignUpOwnerAsync();
        var initial = await GetAsync(owner);

        var response = await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(initial, receiptTitle: new string('A', 200)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Contains("100", error!.Error);
    }

    // --- Who may read and who may write ---

    /// <summary>
    /// A cashier who may ring up a sale has to be able to print what they sold, so the read
    /// needs no privilege beyond being in the workspace.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryMember_CanReadTheConfiguration()
    {
        var owner = await SignUpOwnerAsync();
        await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(await GetAsync(owner), companyName: "Boutique Lonnii"));
        await AddMemberAsync(owner);

        var member = await SignInAsync(MemberEmail, MemberPassword);
        var settings = await GetAsync(member);

        Assert.Equal("Boutique Lonnii", settings.CompanyName);
    }

    /// <summary>The company name and the payment QR on a customer-facing document are not
    /// something a till operator should be able to change quietly - same reasoning as the
    /// currency label.</summary>
    [Fact]
    public async Task AnOrdinaryMember_CannotChangeTheConfiguration()
    {
        var owner = await SignUpOwnerAsync();
        var initial = await GetAsync(owner);
        await AddMemberAsync(owner);

        var member = await SignInAsync(MemberEmail, MemberPassword);
        var response = await SendAsync(HttpMethod.Put, "/api/parametres/recu", member,
            From(initial, companyName: "Détourné"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnOrdinaryMember_CannotReplaceTheLogo()
    {
        var owner = await SignUpOwnerAsync();
        await AddMemberAsync(owner);

        var member = await SignInAsync(MemberEmail, MemberPassword);
        var response = await UploadAsync("/api/parametres/recu/logo", member);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Logo and QR code ---

    [Fact]
    public async Task TheLogo_CanBeUploadedFetchedAndRemoved()
    {
        var owner = await SignUpOwnerAsync();

        var upload = await UploadAsync("/api/parametres/recu/logo", owner);
        upload.EnsureSuccessStatusCode();
        var uploaded = (await upload.Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        Assert.StartsWith("/api/images/receipt-logos/", uploaded.ImageUrl);
        Assert.Equal(uploaded.ImageUrl, (await GetAsync(owner)).LogoUrl);

        var fetch = await SendAsync(HttpMethod.Get, uploaded.ImageUrl, owner);
        fetch.EnsureSuccessStatusCode();

        var delete = await SendAsync(HttpMethod.Delete, "/api/parametres/recu/logo", owner);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Null((await GetAsync(owner)).LogoUrl);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, uploaded.ImageUrl, owner)).StatusCode);
    }

    /// <summary>
    /// A QR code is all hard black/white edges, and JPEG's ringing around them survives the
    /// shrink to a receipt's small square badly enough to stop a phone reading it. The logo
    /// has no such constraint and stays JPEG.
    /// </summary>
    [Fact]
    public async Task TheQrCode_IsStoredLosslesslyWhileTheLogoIsNot()
    {
        var owner = await SignUpOwnerAsync();

        var qr = (await (await UploadAsync("/api/parametres/recu/qrcode", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;
        var logo = (await (await UploadAsync("/api/parametres/recu/logo", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        Assert.EndsWith(".png", qr.ImageUrl);
        Assert.EndsWith(".jpg", logo.ImageUrl);

        var qrFetch = await SendAsync(HttpMethod.Get, qr.ImageUrl, owner);
        Assert.Equal("image/png", qrFetch.Content.Headers.ContentType?.MediaType);

        var logoFetch = await SendAsync(HttpMethod.Get, logo.ImageUrl, owner);
        Assert.Equal("image/jpeg", logoFetch.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Saving the wording must not disturb images the shop did not re-send - which
    /// is the whole reason the text and the files are separate requests.</summary>
    [Fact]
    public async Task SavingTheWording_LeavesTheLogoAlone()
    {
        var owner = await SignUpOwnerAsync();
        var uploaded = (await (await UploadAsync("/api/parametres/recu/logo", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(await GetAsync(owner), companyName: "Renommée"));

        var settings = await GetAsync(owner);

        Assert.Equal(uploaded.ImageUrl, settings.LogoUrl);
        (await SendAsync(HttpMethod.Get, uploaded.ImageUrl, owner)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task RemovingALogoThatWasNeverSet_Succeeds()
    {
        var owner = await SignUpOwnerAsync();

        var response = await SendAsync(HttpMethod.Delete, "/api/parametres/recu/logo", owner);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task UploadingSomethingThatIsNotAnImage_IsRejected()
    {
        var owner = await SignUpOwnerAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/parametres/recu/logo");
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

    // --- Cross-tenant isolation ---

    [Fact]
    public async Task AnotherWorkspacesSession_SeesNeitherTheWordingNorTheLogo()
    {
        var owner = await SignUpOwnerAsync();
        await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner,
            From(await GetAsync(owner), companyName: "Pharmacie du Centre"));
        var logo = (await (await UploadAsync("/api/parametres/recu/logo", owner))
            .Content.ReadFromJsonAsync<ImageUploadResponse>())!;

        // A second workspace for the same account: self-registration is single-use, so this
        // is how to get an unrelated group_id without a second person.
        var create = await SendAsync(HttpMethod.Post, "/api/groupes", owner, new CreateGroupeRequest("Autre Boutique"));
        var groupB = (await create.Content.ReadFromJsonAsync<GroupeDto>())!;
        var openSession = await SendAsync(HttpMethod.Post, $"/api/groupes/{groupB.Id}/session", owner);
        var scoped = (await openSession.Content.ReadFromJsonAsync<GroupSessionResponse>())!;
        var other = owner with { GroupSession = scoped.SessionToken, GroupId = groupB.Id };

        var settings = await GetAsync(other);
        Assert.Null(settings.CompanyName);
        Assert.Null(settings.LogoUrl);

        var fetch = await SendAsync(HttpMethod.Get, logo.ImageUrl, other);
        Assert.Equal(HttpStatusCode.NotFound, fetch.StatusCode);
    }
}
