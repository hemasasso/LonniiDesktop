using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Services;

/// <summary>Raised when the API answers with an error, carrying the message to show the user.</summary>
public class ApiException(string message, HttpStatusCode statusCode, string? requiredPrivilege = null)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The privilege the caller was missing, when the failure was a 403.</summary>
    public string? RequiredPrivilege { get; } = requiredPrivilege;

    /// <summary>True when the session has expired and the user must sign in again.</summary>
    public bool IsAuthFailure => StatusCode is HttpStatusCode.Unauthorized;
}

/// <summary>
/// Talks to the Lonnii Desktop API on the host laptop.
///
/// The host runs the client against localhost; the other machines point at the host's
/// LAN address. Both send the same two headers the web client uses: a bearer token and
/// <c>x-group-session</c>.
/// </summary>
public class LonniiApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private HttpClient _http = CreateHttpClient();

    private string? _accessToken;
    private string? _groupSession;

    /// <summary>Base address of the host, e.g. <c>http://192.168.1.12:5280</c>.</summary>
    public string? BaseAddress { get; private set; }

    private static HttpClient CreateHttpClient() => new()
    {
        // A till on a slow Wi-Fi link should fail visibly rather than hang for a minute.
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// Points the client at a host. Safe to call repeatedly: the sign-in window calls it
    /// again whenever the address is re-tested or an account is created.
    /// </summary>
    public void Connect(string hostAddress)
    {
        var address = hostAddress.Trim();
        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            address = $"http://{address}";
        }

        if (!address.Contains(':', StringComparison.Ordinal) ||
            address.LastIndexOf(':') < "http://".Length)
        {
            address = $"{address}:5280";
        }

        address = address.TrimEnd('/');

        // Nothing to do when the address has not moved. This matters: HttpClient refuses
        // to have BaseAddress reassigned once it has sent a request, so blindly setting
        // it here threw as soon as the user re-tested the host or created an account.
        if (BaseAddress == address) return;

        // A genuinely different host gets a fresh client rather than a mutated one.
        var previous = _http;
        _http = CreateHttpClient();
        _http.BaseAddress = new Uri(address + "/");
        BaseAddress = address;

        previous.Dispose();
    }

    /// <summary>The bearer token from the last successful sign-in.</summary>
    public void SetAccessToken(string? token) => _accessToken = token;

    /// <summary>The group session token sent as <c>x-group-session</c>.</summary>
    public void SetGroupSession(string? token) => _groupSession = token;

    /// <summary>Checks that a Lonnii host is answering at <see cref="BaseAddress"/>.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // --- Auth ---

    public Task<LoginResponse> LoginAsync(string identifier, string password, CancellationToken ct = default) =>
        PostAsync<LoginResponse>("api/auth/login",
            new LoginRequest(identifier, password, Environment.MachineName), ct);

    public Task<UserDto> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        PostAsync<UserDto>("api/auth/register", request, ct);

    /// <summary>
    /// Whether this host already has an account. Used to decide between offering sign-in
    /// and offering to create the first administrator.
    /// </summary>
    public Task<SetupStateResponse> GetSetupStateAsync(CancellationToken ct = default) =>
        GetAsync<SetupStateResponse>("api/auth/setup-state", ct);

    public Task<UserDto> MeAsync(CancellationToken ct = default) =>
        GetAsync<UserDto>("api/auth/me", ct);

    // --- Groups ---

    public Task<List<GroupeDto>> GetGroupesAsync(CancellationToken ct = default) =>
        GetAsync<List<GroupeDto>>("api/groupes", ct);

    public Task<GroupeDto> CreateGroupeAsync(string nom, CancellationToken ct = default) =>
        PostAsync<GroupeDto>("api/groupes", new CreateGroupeRequest(nom), ct);

    public Task<GroupSessionResponse> OpenGroupSessionAsync(string groupId, CancellationToken ct = default) =>
        PostAsync<GroupSessionResponse>($"api/groupes/{groupId}/session", new { }, ct);

    /// <summary>
    /// Brings a fresh installation to life from the credentials file. The file's bytes are
    /// sent rather than a path: the person setting the shop up is usually at a till, and the
    /// file is on their machine, not on the host running the API.
    /// </summary>
    public Task<ApplyCredentialsResponse> ApplyCredentialsAsync(
        byte[] credentialsFile, string passphrase, CancellationToken ct = default) =>
        PostAsync<ApplyCredentialsResponse>("api/setup/apply", new ApplyCredentialsRequest(
            Convert.ToBase64String(credentialsFile),
            passphrase,
            DeviceIdentity.Current,
            DeviceIdentity.FriendlyName,
            AppVersion), ct);

    /// <summary>Binds this machine to the workspace. Needs an administrator's credentials.</summary>
    public Task<DeviceDto> RegisterThisDeviceAsync(
        string email, string password, CancellationToken ct = default) =>
        PostAsync<DeviceDto>("api/devices/register", new RegisterDeviceRequest(
            email, password, DeviceIdentity.Current, DeviceIdentity.FriendlyName, AppVersion), ct);

    public Task<DeviceListResponse> GetDevicesAsync(CancellationToken ct = default) =>
        GetAsync<DeviceListResponse>("api/devices", ct);

    public Task RevokeDeviceAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/devices/{id}", null, ct);

    /// <summary>What this build reports to the server, for the device list.</summary>
    private static string AppVersion =>
        typeof(LonniiApiClient).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public Task<List<GroupMemberDto>> GetMembersAsync(CancellationToken ct = default) =>
        GetAsync<List<GroupMemberDto>>("api/groupe/members", ct);

    public Task<UserDto> AddMemberAsync(AddMemberRequest request, CancellationToken ct = default) =>
        PostAsync<UserDto>("api/groupe/members", request, ct);

    public Task RemoveMemberAsync(string userId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/groupe/members/{userId}", null, ct);

    /// <summary>Resets another member's password. Administrators only.</summary>
    public Task ResetMemberPasswordAsync(string userId, string newPassword, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/groupe/members/{userId}/password",
            new ResetMemberPasswordRequest(newPassword), ct);

    /// <summary>Renames the currency shown after every amount. Group-admin only.</summary>
    public Task<GroupeDto> UpdateCurrencyAsync(string currencyLabel, CancellationToken ct = default) =>
        SendAsync<GroupeDto>(HttpMethod.Put, "api/groupe/currency", new UpdateCurrencyRequest(currencyLabel), ct);

    /// <summary>
    /// Changes the signed-in user's own password. The returned token replaces the current
    /// one, which the change has just invalidated.
    /// </summary>
    public async Task<LoginResponse> ChangeOwnPasswordAsync(
        string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var response = await PostAsync<LoginResponse>("api/auth/password",
            new ChangePasswordRequest(currentPassword, newPassword), ct);

        SetAccessToken(response.AccessToken);

        // The server dropped every group session for this account, so the old one is dead.
        SetGroupSession(null);

        return response;
    }

    // --- Privileges and menu ---

    public Task<MyPrivilegesResponse> GetMyPrivilegesAsync(CancellationToken ct = default) =>
        GetAsync<MyPrivilegesResponse>("api/privileges/me", ct);

    public Task<MenuResponse> GetMenuAsync(CancellationToken ct = default) =>
        GetAsync<MenuResponse>("api/privileges/menu", ct);

    public Task<MemberPrivilegesResponse> GetMemberPrivilegesAsync(string userId, CancellationToken ct = default) =>
        GetAsync<MemberPrivilegesResponse>($"api/privileges/member/{userId}", ct);

    public Task SetGestionPrivilegeAsync(SetPrivilegeRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/privileges/gestion", request, ct);

    public Task SetOptionPrivilegeAsync(SetPrivilegeRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/privileges/option", request, ct);

    public Task SetRoleAsync(SetRoleRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/privileges/role", request, ct);

    // --- Stock ---

    public Task<List<ProductDto>> GetProductsAsync(
        string? search = null, string? categoryId = null, bool lowStockOnly = false, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(categoryId)) query.Add($"categoryId={Uri.EscapeDataString(categoryId)}");
        if (lowStockOnly) query.Add("lowStockOnly=true");

        var url = "api/stock/products" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return GetAsync<List<ProductDto>>(url, ct);
    }

    public Task<ProductDto> CreateProductAsync(SaveProductRequest request, CancellationToken ct = default) =>
        PostAsync<ProductDto>("api/stock/products", request, ct);

    public Task<ProductDto> UpdateProductAsync(string id, SaveProductRequest request, CancellationToken ct = default) =>
        SendAsync<ProductDto>(HttpMethod.Put, $"api/stock/products/{id}", request, ct);

    public Task DeleteProductAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/stock/products/{id}", null, ct);

    public Task<ProductDto> AdjustStockAsync(string id, AdjustStockRequest request, CancellationToken ct = default) =>
        PostAsync<ProductDto>($"api/stock/products/{id}/adjust", request, ct);

    public Task<List<StockHistoryDto>> GetProductHistoryAsync(string id, CancellationToken ct = default) =>
        GetAsync<List<StockHistoryDto>>($"api/stock/products/{id}/history", ct);

    public Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        GetAsync<List<CategoryDto>>("api/stock/categories", ct);

    public Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken ct = default) =>
        PostAsync<CategoryDto>("api/stock/categories", request, ct);

    public Task<CategoryDto> UpdateCategoryAsync(string id, SaveCategoryRequest request, CancellationToken ct = default) =>
        SendAsync<CategoryDto>(HttpMethod.Put, $"api/stock/categories/{id}", request, ct);

    public Task<List<SupplierDto>> GetSuppliersAsync(CancellationToken ct = default) =>
        GetAsync<List<SupplierDto>>("api/stock/suppliers", ct);

    public Task<SupplierDto> CreateSupplierAsync(SupplierDto request, CancellationToken ct = default) =>
        PostAsync<SupplierDto>("api/stock/suppliers", request, ct);

    // --- Ventes ---

    public Task<VenteDto> CreateVenteAsync(CreateVenteRequest request, CancellationToken ct = default) =>
        PostAsync<VenteDto>("api/ventes", request, ct);

    public Task<VenteDto> GetVenteAsync(string id, CancellationToken ct = default) =>
        GetAsync<VenteDto>($"api/ventes/{id}", ct);

    /// <summary>"Liste des Ventes". <paramref name="statut"/> is one of "paid", "partial",
    /// "pending", "avoir", "cancelled", or null/"all" for every status - same vocabulary as
    /// the filter dropdown, mirroring Lonnii Business.</summary>
    public Task<VentesListResponse> GetVentesAsync(
        string? statut = null, string? search = null, string? searchType = null,
        DateOnly? dateDebut = null, DateOnly? dateFin = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(statut) && statut != "all") query.Add($"statut={Uri.EscapeDataString(statut)}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(searchType)) query.Add($"searchType={Uri.EscapeDataString(searchType)}");
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        if (dateDebut is not null || dateFin is not null) query.Add($"tzOffsetMinutes={LocalTzOffsetMinutes()}");

        var url = "api/ventes" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return GetAsync<VentesListResponse>(url, ct);
    }

    public Task<VenteDto> AddPaiementAsync(string id, AddPaiementRequest request, CancellationToken ct = default) =>
        PostAsync<VenteDto>($"api/ventes/{id}/paiement", request, ct);

    public Task CancelVenteAsync(string id, CancelVenteRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"api/ventes/{id}/annuler", request, ct);

    public Task SolderAvoirAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"api/ventes/{id}/solder-avoir", null, ct);

    public Task EditVenteAsync(string id, EditVenteRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"api/ventes/{id}", request, ct);

    /// <summary>"Statistiques" tab. <paramref name="categoryId"/> and <paramref name="productId"/>
    /// narrow every figure to that category or product's sale lines - see
    /// <see cref="VentesStatsResponse"/>.</summary>
    public Task<VentesStatsResponse> GetVentesStatsAsync(
        DateOnly? dateDebut = null, DateOnly? dateFin = null,
        string? categoryId = null, string? productId = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        if (dateDebut is not null || dateFin is not null) query.Add($"tzOffsetMinutes={LocalTzOffsetMinutes()}");
        if (!string.IsNullOrWhiteSpace(categoryId)) query.Add($"categoryId={Uri.EscapeDataString(categoryId)}");
        if (!string.IsNullOrWhiteSpace(productId)) query.Add($"productId={Uri.EscapeDataString(productId)}");

        var url = "api/ventes/stats" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return GetAsync<VentesStatsResponse>(url, ct);
    }

    /// <summary>This machine's local time minus UTC, in minutes - what a date-range filter
    /// needs so the server can tell which UTC instants "today" (this machine's today) actually
    /// covers. Same sign convention the server's <c>LocalRangeToUtc</c> expects: positive
    /// east of UTC, negative west of it.</summary>
    private static int LocalTzOffsetMinutes() =>
        (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;

    // --- Paramètres: reçu et facture ---

    /// <summary>The workspace's receipt and invoice configuration, defaults already applied.
    /// Prefer <c>AppSession.GetReceiptSettingsAsync</c>, which caches this for the printing
    /// path; call it directly only when a fresh read is what is wanted.</summary>
    public Task<ReceiptSettingsDto> GetReceiptSettingsAsync(CancellationToken ct = default) =>
        GetAsync<ReceiptSettingsDto>("api/parametres/recu", ct);

    /// <summary>Saves the wording. Admin-only server-side; the logo and QR code have their
    /// own calls, so this does not re-send them.</summary>
    public Task<ReceiptSettingsDto> UpdateReceiptSettingsAsync(
        UpdateReceiptSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<ReceiptSettingsDto>(HttpMethod.Put, "api/parametres/recu", request, ct);

    public Task<ImageUploadResponse> UploadReceiptLogoAsync(
        byte[] content, string fileName, CancellationToken ct = default) =>
        UploadImageAsync("api/parametres/recu/logo", content, fileName, ct);

    public Task DeleteReceiptLogoAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "api/parametres/recu/logo", null, ct);

    public Task<ImageUploadResponse> UploadReceiptQrCodeAsync(
        byte[] content, string fileName, CancellationToken ct = default) =>
        UploadImageAsync("api/parametres/recu/qrcode", content, fileName, ct);

    public Task DeleteReceiptQrCodeAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "api/parametres/recu/qrcode", null, ct);

    // --- Images ---

    /// <summary>Uploads a product photo, replacing whatever was there before.</summary>
    public Task<ImageUploadResponse> UploadProductImageAsync(
        string productId, byte[] content, string fileName, CancellationToken ct = default) =>
        UploadImageAsync($"api/stock/products/{productId}/image", content, fileName, ct);

    public Task DeleteProductImageAsync(string productId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/stock/products/{productId}/image", null, ct);

    /// <summary>Uploads a category photo, replacing whatever was there before.</summary>
    public Task<ImageUploadResponse> UploadCategoryImageAsync(
        string categoryId, byte[] content, string fileName, CancellationToken ct = default) =>
        UploadImageAsync($"api/stock/categories/{categoryId}/image", content, fileName, ct);

    public Task DeleteCategoryImageAsync(string categoryId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/stock/categories/{categoryId}/image", null, ct);

    /// <summary>
    /// Downloads a stored photo's bytes through the same authenticated connection as
    /// everything else, rather than pointing WPF's Image control at a bare URL - the API
    /// requires a bearer token and a group session header that a plain image URI cannot
    /// carry. <paramref name="imageUrl"/> is the API-relative path a DTO's ImageUrl holds,
    /// e.g. <c>/api/images/products/&lt;file&gt;</c>.
    /// </summary>
    public async Task<byte[]> GetImageBytesAsync(string imageUrl, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, imageUrl.TrimStart('/'), null, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<ImageUploadResponse> UploadImageAsync(
        string url, byte[] content, string fileName, CancellationToken ct)
    {
        if (_http.BaseAddress is null)
            throw new ApiException("Aucun serveur configuré", HttpStatusCode.ServiceUnavailable);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new(GuessContentType(fileName));
        form.Add(fileContent, "file", fileName);
        request.Content = form;

        if (_accessToken is not null)
            request.Headers.Authorization = new("Bearer", _accessToken);
        if (_groupSession is not null)
            request.Headers.Add("x-group-session", _groupSession);
        request.Headers.Add("x-device-id", DeviceIdentity.Current);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(
                "Le serveur ne répond pas. Vérifiez que l'ordinateur hôte est allumé et connecté au réseau.",
                HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException e)
        {
            throw new ApiException(
                $"Impossible de joindre le serveur ({BaseAddress}). Vérifiez le réseau local.\n\n{e.Message}",
                HttpStatusCode.ServiceUnavailable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ApiError? error = null;
                try { error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, ct); }
                catch { /* fall through to a generic message */ }

                throw new ApiException(
                    error?.Error ?? $"Erreur serveur ({(int)response.StatusCode})",
                    response.StatusCode, error?.Required);
            }

            var result = await response.Content.ReadFromJsonAsync<ImageUploadResponse>(JsonOptions, ct);
            return result ?? throw new ApiException("Réponse vide du serveur", response.StatusCode);
        }
    }

    private static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    // --- Plumbing ---

    private Task<T> GetAsync<T>(string url, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Get, url, null, ct);

    private Task<T> PostAsync<T>(string url, object? body, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Post, url, body, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendCoreAsync(method, url, body, ct);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return result ?? throw new ApiException("Réponse vide du serveur", response.StatusCode);
    }

    private async Task SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var _ = await SendCoreAsync(method, url, body, ct);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method, string url, object? body, CancellationToken ct)
    {
        if (_http.BaseAddress is null)
            throw new ApiException("Aucun serveur configuré", HttpStatusCode.ServiceUnavailable);

        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        if (_accessToken is not null)
            request.Headers.Authorization = new("Bearer", _accessToken);

        if (_groupSession is not null)
            request.Headers.Add("x-group-session", _groupSession);

        // Sent on every call, not only when opening a workspace: the server uses it to
        // recognise the machine, and a request that arrives without it looks exactly like
        // a copied installation trying to stay quiet.
        request.Headers.Add("x-device-id", DeviceIdentity.Current);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(
                "Le serveur ne répond pas. Vérifiez que l'ordinateur hôte est allumé et connecté au réseau.",
                HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException e)
        {
            throw new ApiException(
                $"Impossible de joindre le serveur ({BaseAddress}). Vérifiez le réseau local.\n\n{e.Message}",
                HttpStatusCode.ServiceUnavailable);
        }

        if (response.IsSuccessStatusCode) return response;

        // Surface the API's own French message rather than a raw status code.
        ApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, ct);
        }
        catch
        {
            // Body was not the expected shape; fall through to a generic message.
        }

        response.Dispose();

        throw new ApiException(
            error?.Error ?? $"Erreur serveur ({(int)response.StatusCode})",
            response.StatusCode,
            error?.Required);
    }
}
