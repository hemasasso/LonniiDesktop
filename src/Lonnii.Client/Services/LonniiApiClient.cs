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

    /// <summary>
    /// Used only by the espace export and import, which move a whole database and cannot
    /// live inside <see cref="_http"/>'s twenty-second timeout. Kept as a second client
    /// rather than raised on the shared one, so one long transfer cannot make an ordinary
    /// call hang for ten minutes.
    /// </summary>
    private HttpClient _transfers = CreateTransferClient();

    private string? _accessToken;
    private string? _groupSession;

    /// <summary>Base address of the host, e.g. <c>http://192.168.1.12:5280</c>.</summary>
    public string? BaseAddress { get; private set; }

    private static HttpClient CreateHttpClient() => new()
    {
        // A till on a slow Wi-Fi link should fail visibly rather than hang for a minute.
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static HttpClient CreateTransferClient() => new()
    {
        // An espace archive may be several gigabytes, and this timeout covers the whole
        // upload - a 5 GB file over a shop's Wi-Fi is comfortably an hour's work. The user
        // can cancel by closing the dialog; what this guards against is a transfer that has
        // silently stopped moving, not a slow one.
        Timeout = TimeSpan.FromHours(3),
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
        var previousTransfers = _transfers;

        _http = CreateHttpClient();
        _http.BaseAddress = new Uri(address + "/");

        _transfers = CreateTransferClient();
        _transfers.BaseAddress = new Uri(address + "/");

        BaseAddress = address;

        previous.Dispose();
        previousTransfers.Dispose();
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
    /// Permanently deletes an espace and all its data. Only the Admin Général (the creator)
    /// of the espace may call this; the API refuses anyone else with 403.
    /// </summary>
    public Task DeleteGroupeAsync(string groupId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/groupes/{groupId}", null, ct);

    /// <summary>Closes the current group session (the x-group-session token).</summary>
    public Task CloseGroupSessionAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "api/groupe/session", null, ct);

    /// <summary>Uploads a cover photo for the current espace, replacing the previous one.</summary>
    public Task<GroupeDto> UploadEspacePhotoAsync(
        byte[] content, string fileName, CancellationToken ct = default) =>
        UploadImageAsync<GroupeDto>("api/groupe/photo", content, fileName, ct);

    /// <summary>Removes the current espace's cover photo.</summary>
    public Task DeleteEspacePhotoAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "api/groupe/photo", null, ct);

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
    public Task<GroupeDto> UpdateCurrencyAsync(string currencyLabel, bool currencyBefore, CancellationToken ct = default) =>
        SendAsync<GroupeDto>(HttpMethod.Put, "api/groupe/currency", new UpdateCurrencyRequest(currencyLabel, currencyBefore), ct);

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

    /// <summary>"Mouvements de stock" on the Analyse tab - every movement in the group, grouped
    /// by type. <paramref name="categoryId"/> mirrors Analyse's own category filter.</summary>
    public Task<StockMovementStatsResponse> GetStockMovementStatsAsync(
        DateOnly? dateDebut = null, DateOnly? dateFin = null, string? categoryId = null,
        string? supplierId = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(categoryId)) query.Add($"categoryId={Uri.EscapeDataString(categoryId)}");
        if (!string.IsNullOrWhiteSpace(supplierId)) query.Add($"supplierId={Uri.EscapeDataString(supplierId)}");

        var url = "api/stock/movements/stats" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return GetAsync<StockMovementStatsResponse>(url, ct);
    }

    /// <summary>The individual movements behind <see cref="GetStockMovementStatsAsync"/>'s
    /// totals, newest first, each naming its product, category and supplier.</summary>
    public Task<StockMovementDetailsResponse> GetStockMovementDetailsAsync(
        DateOnly? dateDebut = null, DateOnly? dateFin = null, string? categoryId = null,
        string? supplierId = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(categoryId)) query.Add($"categoryId={Uri.EscapeDataString(categoryId)}");
        if (!string.IsNullOrWhiteSpace(supplierId)) query.Add($"supplierId={Uri.EscapeDataString(supplierId)}");

        var url = "api/stock/movements/detail" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return GetAsync<StockMovementDetailsResponse>(url, ct);
    }

    public Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        GetAsync<List<CategoryDto>>("api/stock/categories", ct);

    public Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken ct = default) =>
        PostAsync<CategoryDto>("api/stock/categories", request, ct);

    public Task<CategoryDto> UpdateCategoryAsync(string id, SaveCategoryRequest request, CancellationToken ct = default) =>
        SendAsync<CategoryDto>(HttpMethod.Put, $"api/stock/categories/{id}", request, ct);

    public Task<List<SupplierDto>> GetSuppliersAsync(CancellationToken ct = default) =>
        GetAsync<List<SupplierDto>>("api/stock/suppliers", ct);

    public Task<SupplierDto> CreateSupplierAsync(SaveSupplierRequest request, CancellationToken ct = default) =>
        PostAsync<SupplierDto>("api/stock/suppliers", request, ct);

    public Task<SupplierDto> UpdateSupplierAsync(string id, SaveSupplierRequest request, CancellationToken ct = default) =>
        SendAsync<SupplierDto>(HttpMethod.Put, $"api/stock/suppliers/{id}", request, ct);

    // --- Clients ---

    public Task<List<ClientDto>> GetClientsAsync(CancellationToken ct = default) =>
        GetAsync<List<ClientDto>>("api/ventes/clients", ct);

    public Task<ClientDto> CreateClientAsync(SaveClientRequest request, CancellationToken ct = default) =>
        PostAsync<ClientDto>("api/ventes/clients", request, ct);

    public Task<ClientDto> UpdateClientAsync(string id, SaveClientRequest request, CancellationToken ct = default) =>
        SendAsync<ClientDto>(HttpMethod.Put, $"api/ventes/clients/{id}", request, ct);

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

    /// <summary>"Paiement Groupé": settles several unpaid factures at once.</summary>
    public Task<GroupePaiementDto> PayGroupeAsync(GroupePaiementRequest request, CancellationToken ct = default) =>
        PostAsync<GroupePaiementDto>("api/ventes/groupe-payments", request, ct);

    /// <summary>History of group receipts, newest first.</summary>
    public Task<GroupePaiementsResponse> GetGroupePaymentsAsync(
        int page, int limit, DateOnly? dateDebut = null, DateOnly? dateFin = null, CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"limit={limit}" };
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        if (dateDebut is not null || dateFin is not null) query.Add($"tzOffsetMinutes={LocalTzOffsetMinutes()}");
        return GetAsync<GroupePaiementsResponse>("api/ventes/groupe-payments?" + string.Join("&", query), ct);
    }

    public Task SolderGroupeAvoirAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"api/ventes/groupe-payments/{id}/solder-avoir", null, ct);

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

    // --- Caisse ---

    /// <summary>The caller's own open session, or null when nothing is open right now.</summary>
    public Task<CaisseStatusResponse> GetCaisseStatusAsync(CancellationToken ct = default) =>
        GetAsync<CaisseStatusResponse>("api/caisse/status", ct);

    public Task<CaisseDto> OpenCaisseAsync(OpenCaisseRequest request, CancellationToken ct = default) =>
        PostAsync<CaisseDto>("api/caisse/ouvrir", request, ct);

    public Task<CaisseDto> CloseCaisseAsync(CloseCaisseRequest request, CancellationToken ct = default) =>
        PostAsync<CaisseDto>("api/caisse/fermer", request, ct);

    public Task<CaisseHistoryResponse> GetCaisseHistoryAsync(
        DateOnly? dateDebut = null, DateOnly? dateFin = null, string? userId = null,
        int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(userId)) query.Add($"userId={Uri.EscapeDataString(userId)}");

        return GetAsync<CaisseHistoryResponse>("api/caisse/historique?" + string.Join("&", query), ct);
    }

    public Task<List<CaisseVendeurDto>> GetCaisseVendeursAsync(CancellationToken ct = default) =>
        GetAsync<List<CaisseVendeurDto>>("api/caisse/vendeurs", ct);

    public Task<CaisseDto> ResolveCaisseEcartAsync(int id, ResolveEcartRequest request, CancellationToken ct = default) =>
        PostAsync<CaisseDto>($"api/caisse/{id}/resolve-ecart", request, ct);

    public Task<CaisseDto> WithdrawCaisseAsync(WithdrawCaisseRequest request, CancellationToken ct = default) =>
        PostAsync<CaisseDto>("api/caisse/retrait", request, ct);

    // --- Charges ---

    public Task<ChargesListResponse> GetChargesAsync(
        DateOnly? dateDebut = null, DateOnly? dateFin = null, string? categorie = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(categorie)) query.Add($"categorie={Uri.EscapeDataString(categorie)}");

        var url = "api/charges" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return GetAsync<ChargesListResponse>(url, ct);
    }

    public Task<ChargeDetailsResponse> GetChargeDetailsAsync(int id, CancellationToken ct = default) =>
        GetAsync<ChargeDetailsResponse>($"api/charges/{id}", ct);

    public Task<ChargeDto> CreateChargeAsync(SaveChargeRequest request, CancellationToken ct = default) =>
        PostAsync<ChargeDto>("api/charges", request, ct);

    public Task<ChargeDto> UpdateChargeAsync(int id, SaveChargeRequest request, CancellationToken ct = default) =>
        SendAsync<ChargeDto>(HttpMethod.Put, $"api/charges/{id}", request, ct);

    public Task DeleteChargeAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/charges/{id}", null, ct);

    public Task<List<ChargeCategoryDto>> GetChargeCategoriesAsync(CancellationToken ct = default) =>
        GetAsync<List<ChargeCategoryDto>>("api/charges/categories", ct);

    public Task<ChargeCategoryDto> SaveChargeCategoryAsync(SaveChargeCategoryRequest request, CancellationToken ct = default) =>
        PostAsync<ChargeCategoryDto>("api/charges/categories", request, ct);

    public Task DeleteChargeCategoryAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/charges/categories/{id}", null, ct);

    public Task<ChargesListResponse> GetRecurringChargesAsync(CancellationToken ct = default) =>
        GetAsync<ChargesListResponse>("api/charges/recurring", ct);

    public Task<ChargeDto> StopRecurringChargeAsync(int id, CancellationToken ct = default) =>
        SendAsync<ChargeDto>(HttpMethod.Put, $"api/charges/{id}/stop-recurring", null, ct);

    public Task<ChargeDto> ReactivateRecurringChargeAsync(
        int id, ReactivateRecurringRequest request, CancellationToken ct = default) =>
        SendAsync<ChargeDto>(HttpMethod.Put, $"api/charges/{id}/reactivate-recurring", request, ct);

    public Task<ChargesStatsResponse> GetChargesStatsAsync(int? annee = null, CancellationToken ct = default) =>
        GetAsync<ChargesStatsResponse>("api/charges/stats" + (annee is { } y ? $"?annee={y}" : string.Empty), ct);

    /// <summary>Marges. Both dates null means every sale ever made; <paramref name="categoryId"/>
    /// narrows the sales figures, never the charges - see <see cref="MargesResponse"/>.</summary>
    public Task<MargesResponse> GetMargesAsync(
        DateOnly? dateDebut = null, DateOnly? dateFin = null, string? categoryId = null, CancellationToken ct = default)
    {
        // Always sent: the trailing-twelve-months chart needs this machine's "this month"
        // even when the period filter itself is "Toutes".
        var query = new List<string> { $"tzOffsetMinutes={LocalTzOffsetMinutes()}" };
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(categoryId)) query.Add($"categoryId={Uri.EscapeDataString(categoryId)}");

        return GetAsync<MargesResponse>("api/marges?" + string.Join("&", query), ct);
    }

    // --- Amortissement ---

    /// <summary>Every fixed asset and the summary tiles, figured for this machine's current
    /// year.</summary>
    public Task<AmortissementListResponse> GetImmobilisationsAsync(CancellationToken ct = default) =>
        GetAsync<AmortissementListResponse>($"api/amortissement?annee={DateTime.Now.Year}", ct);

    public Task<ImmobilisationDetailsResponse> GetImmobilisationAsync(int id, CancellationToken ct = default) =>
        GetAsync<ImmobilisationDetailsResponse>($"api/amortissement/{id}?annee={DateTime.Now.Year}", ct);

    public Task<ImmobilisationDetailsResponse> CreateImmobilisationAsync(
        SaveImmobilisationRequest request, CancellationToken ct = default) =>
        PostAsync<ImmobilisationDetailsResponse>($"api/amortissement?annee={DateTime.Now.Year}", request, ct);

    public Task<ImmobilisationDetailsResponse> UpdateImmobilisationAsync(
        int id, SaveImmobilisationRequest request, CancellationToken ct = default) =>
        SendAsync<ImmobilisationDetailsResponse>(HttpMethod.Put, $"api/amortissement/{id}?annee={DateTime.Now.Year}", request, ct);

    public Task<ImmobilisationDetailsResponse> CederImmobilisationAsync(
        int id, CederImmobilisationRequest request, CancellationToken ct = default) =>
        SendAsync<ImmobilisationDetailsResponse>(HttpMethod.Put, $"api/amortissement/{id}/ceder?annee={DateTime.Now.Year}", request, ct);

    public Task DeleteImmobilisationAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/amortissement/{id}", null, ct);

    // --- Bilan ---

    public Task<BilanResponse> GetBilanAsync(int annee, CancellationToken ct = default) =>
        GetAsync<BilanResponse>($"api/bilan?annee={annee}&tzOffsetMinutes={LocalTzOffsetMinutes()}", ct);

    public Task<ResultatResponse> GetResultatAsync(int annee, CancellationToken ct = default) =>
        GetAsync<ResultatResponse>($"api/bilan/resultat?annee={annee}&tzOffsetMinutes={LocalTzOffsetMinutes()}", ct);

    public Task<BilanComptesResponse> GetBilanComptesAsync(CancellationToken ct = default) =>
        GetAsync<BilanComptesResponse>("api/bilan/comptes", ct);

    public Task<BilanCompteDto> CreateBilanCompteAsync(SaveBilanCompteRequest request, CancellationToken ct = default) =>
        PostAsync<BilanCompteDto>("api/bilan/comptes", request, ct);

    public Task<BilanCompteDto> UpdateBilanCompteAsync(int id, SaveBilanCompteRequest request, CancellationToken ct = default) =>
        SendAsync<BilanCompteDto>(HttpMethod.Put, $"api/bilan/comptes/{id}", request, ct);

    public Task DeleteBilanCompteAsync(int id, string tableType, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/bilan/comptes/{id}?tableType={Uri.EscapeDataString(tableType)}", null, ct);

    public Task<List<BilanEcritureDto>> GetBilanEcrituresAsync(
        int? compteId = null, string? tableType = null, DateOnly? dateDebut = null, DateOnly? dateFin = null,
        CancellationToken ct = default)
    {
        var query = new List<string>();
        if (compteId is { } c) query.Add($"compteId={c}");
        if (tableType is { Length: > 0 }) query.Add($"tableType={Uri.EscapeDataString(tableType)}");
        if (dateDebut is { } debut) query.Add($"dateDebut={debut:yyyy-MM-dd}");
        if (dateFin is { } fin) query.Add($"dateFin={fin:yyyy-MM-dd}");
        return GetAsync<List<BilanEcritureDto>>("api/bilan/ecritures" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty), ct);
    }

    public Task<BilanEcritureDto> CreateBilanEcritureAsync(SaveBilanEcritureRequest request, CancellationToken ct = default) =>
        PostAsync<BilanEcritureDto>("api/bilan/ecritures", request, ct);

    public Task<BilanEcritureDto> UpdateBilanEcritureAsync(int id, SaveBilanEcritureRequest request, CancellationToken ct = default) =>
        SendAsync<BilanEcritureDto>(HttpMethod.Put, $"api/bilan/ecritures/{id}", request, ct);

    public Task DeleteBilanEcritureAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/bilan/ecritures/{id}", null, ct);

    public Task SetStockDebutAsync(StockSnapshotRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, "api/bilan/stock-snapshot", request, ct);

    public Task<ComptabiliteParametresDto> GetComptabiliteParametresAsync(CancellationToken ct = default) =>
        GetAsync<ComptabiliteParametresDto>("api/bilan/parametres", ct);

    public Task<ComptabiliteParametresDto> SaveComptabiliteParametresAsync(
        SaveComptabiliteParametresRequest request, CancellationToken ct = default) =>
        SendAsync<ComptabiliteParametresDto>(HttpMethod.Put, "api/bilan/parametres", request, ct);

    /// <summary>This machine's local time minus UTC, in minutes - what a date-range filter
    /// needs so the server can tell which UTC instants "today" (this machine's today) actually
    /// covers. Same sign convention the server's <c>LocalRangeToUtc</c> expects: positive
    /// east of UTC, negative west of it.</summary>
    private static int LocalTzOffsetMinutes() =>
        (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;

    // --- Programme ---

    /// <summary>Every entry and announcement between <paramref name="start"/> and
    /// <paramref name="end"/> (inclusive). <paramref name="userId"/> narrows the board to one
    /// worker - what the printable sheet and a non-admin's own view both use.</summary>
    public Task<ProgrammeResponse> GetProgrammeAsync(
        DateOnly start, DateOnly end, string? userId = null, CancellationToken ct = default)
    {
        var query = new List<string> { $"start={start:yyyy-MM-dd}", $"end={end:yyyy-MM-dd}" };
        if (!string.IsNullOrWhiteSpace(userId)) query.Add($"userId={Uri.EscapeDataString(userId)}");
        return GetAsync<ProgrammeResponse>("api/programme?" + string.Join("&", query), ct);
    }

    public Task<ProgrammeEntryDto> SaveProgrammeEntryAsync(
        SaveProgrammeEntryRequest request, CancellationToken ct = default) =>
        PostAsync<ProgrammeEntryDto>("api/programme/entries", request, ct);

    public Task DeleteProgrammeEntryAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/programme/entries/{id}", null, ct);

    public Task<ProgrammeAnnouncementDto> CreateProgrammeAnnouncementAsync(
        SaveProgrammeAnnouncementRequest request, CancellationToken ct = default) =>
        PostAsync<ProgrammeAnnouncementDto>("api/programme/announcements", request, ct);

    public Task<ProgrammeAnnouncementDto> UpdateProgrammeAnnouncementAsync(
        int id, SaveProgrammeAnnouncementRequest request, CancellationToken ct = default) =>
        SendAsync<ProgrammeAnnouncementDto>(HttpMethod.Put, $"api/programme/announcements/{id}", request, ct);

    public Task DeleteProgrammeAnnouncementAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/programme/announcements/{id}", null, ct);

    // --- Audit (présences) ---

    /// <summary>This machine's offset from UTC right now, so the server files attendance
    /// under the shop's calendar day and reads Programme hours as local time.</summary>
    private static int UtcOffsetMinutes => (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;

    public Task SendPresenceAsync(string? module, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/audit/presence",
            new PresenceHeartbeatRequest(module, Environment.MachineName, UtcOffsetMinutes), ct);

    public Task EndPresenceAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/audit/presence/end", null, ct);

    public Task<AttendanceResponse> GetAttendanceAsync(CancellationToken ct = default) =>
        GetAsync<AttendanceResponse>($"api/audit/attendance?offset={UtcOffsetMinutes}", ct);

    public Task<MemberWorkHistoryResponse> GetMemberWorkHistoryAsync(string userId, CancellationToken ct = default) =>
        GetAsync<MemberWorkHistoryResponse>(
            $"api/audit/attendance/{Uri.EscapeDataString(userId)}/history?offset={UtcOffsetMinutes}", ct);

    // --- Paramètres: consommation données ---

    public Task<DataConsumptionResponse> GetDataConsumptionAsync(int? year, CancellationToken ct = default) =>
        GetAsync<DataConsumptionResponse>(
            "api/parametres/consommation" + (year is { } y ? $"?year={y}" : string.Empty), ct);

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

    // --- Paramètres: données de l'espace (export / import) ---

    /// <summary>Read and write size for an espace transfer. Larger than the usual buffer
    /// because these files run to gigabytes.</summary>
    private const int TransferChunkBytes = 1024 * 1024;

    /// <summary>How far an espace transfer has got. <paramref name="Total"/> is zero when the
    /// size is not known in advance.</summary>
    public sealed record TransferProgress(long Transferred, long Total);

    /// <summary>
    /// Downloads the whole espace - rows and photos - into <paramref name="destinationPath"/>
    /// as a single SQLite <c>.db</c> file, and returns what the host says it put in there.
    ///
    /// Streamed straight to disk rather than through a byte[]: an espace with a photo on
    /// every product runs to gigabytes.
    /// </summary>
    public async Task<(int Records, int Images)> DownloadEspaceAsync(
        string destinationPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var response = await SendTransferAsync(
            HttpMethod.Get, "api/parametres/espace/export", null, ct);

        var total = response.Content.Headers.ContentLength ?? 0;

        await using (var file = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, TransferChunkBytes, useAsync: true))
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);

            // Copied by hand rather than with CopyToAsync so the dialog can say how far it
            // has got: at these sizes a progress bar with nothing behind it looks like a hang.
            var buffer = new byte[TransferChunkBytes];
            long written = 0;

            while (true)
            {
                var read = await body.ReadAsync(buffer, ct);
                if (read == 0) break;

                await file.WriteAsync(buffer.AsMemory(0, read), ct);

                written += read;
                progress?.Report(new TransferProgress(written, total));
            }
        }

        return (Header("x-lonnii-records"), Header("x-lonnii-images"));

        int Header(string name) =>
            response.Headers.TryGetValues(name, out var values) &&
            int.TryParse(values.FirstOrDefault(), out var value) ? value : 0;
    }

    /// <summary>
    /// Loads an exported <c>.db</c> file into the espace this session is in. The host refuses
    /// it unless that espace still has no data of its own, so the normal sequence is: create a
    /// new espace, open it, then import.
    /// </summary>
    public async Task<EspaceImportResultDto> UploadEspaceAsync(
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        await using var file = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, TransferChunkBytes, useAsync: true);

        // HttpClient pulls the body out of this stream as it sends, so counting what it reads
        // is what the upload has actually put on the wire.
        var content = new StreamContent(new ProgressStream(file, progress), TransferChunkBytes);
        content.Headers.ContentType = new("application/octet-stream");

        // StreamContent cannot work out the length through the wrapper, and without it the
        // request goes out chunked - which costs the server its early size check.
        content.Headers.ContentLength = file.Length;

        using var response = await SendTransferAsync(
            HttpMethod.Post, "api/parametres/espace/import", content, ct);

        var result = await response.Content.ReadFromJsonAsync<EspaceImportResultDto>(JsonOptions, ct);
        return result ?? throw new ApiException("Réponse vide du serveur", response.StatusCode);
    }

    /// <summary>
    /// A read-only pass-through that reports how much has been read out of it. Used for the
    /// upload, where the only way to know what has gone out is to watch HttpClient consume
    /// the file.
    /// </summary>
    private sealed class ProgressStream(Stream inner, IProgress<TransferProgress>? progress) : Stream
    {
        private readonly long _total = inner.CanSeek ? inner.Length : 0;
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));

        private int Count(int read)
        {
            if (read > 0)
            {
                _read += read;
                progress?.Report(new TransferProgress(_read, _total));
            }

            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Sends one espace transfer. Separate from <see cref="SendCoreAsync"/> because of the
    /// timeout: the ordinary client gives up after twenty seconds, which is right for a till
    /// waiting on a price and far too short for a database being copied over Wi-Fi.
    /// </summary>
    private async Task<HttpResponseMessage> SendTransferAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        if (_http.BaseAddress is null)
            throw new ApiException("Aucun serveur configuré", HttpStatusCode.ServiceUnavailable);

        using var request = new HttpRequestMessage(method, url) { Content = content };

        if (_accessToken is not null)
            request.Headers.Authorization = new("Bearer", _accessToken);
        if (_groupSession is not null)
            request.Headers.Add("x-group-session", _groupSession);
        request.Headers.Add("x-device-id", DeviceIdentity.Current);

        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead: the body is a file, and the caller streams it.
            response = await _transfers.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(
                "Le transfert a expiré. Vérifiez le réseau local, puis réessayez.",
                HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException e)
        {
            throw new ApiException(
                $"Impossible de joindre le serveur ({BaseAddress}). Vérifiez le réseau local.\n\n{e.Message}",
                HttpStatusCode.ServiceUnavailable);
        }

        if (response.IsSuccessStatusCode) return response;

        ApiError? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, ct); }
        catch { /* not the expected shape; fall through to a generic message */ }

        response.Dispose();

        throw new ApiException(
            error?.Error ?? $"Erreur serveur ({(int)response.StatusCode})",
            response.StatusCode, error?.Required);
    }

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

    private Task<ImageUploadResponse> UploadImageAsync(
        string url, byte[] content, string fileName, CancellationToken ct) =>
        UploadImageAsync<ImageUploadResponse>(url, content, fileName, ct);

    private async Task<T> UploadImageAsync<T>(
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

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
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
