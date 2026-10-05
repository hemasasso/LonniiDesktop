using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Licensing;

/// <summary>Raised when activation is refused, carrying a message fit to show a shopkeeper.</summary>
public sealed class ActivationRefusedException(string message, HttpStatusCode? status = null)
    : Exception(message)
{
    /// <summary>What the licence server answered, or null if it could not be reached.</summary>
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Talks to the licence server on our own infrastructure.
///
/// <para>
/// Behind an interface so the first-launch flow can be tested without a server, and so a
/// host laptop that is enrolling itself takes the same path as one being tested.
/// </para>
/// </summary>
public interface ILicenceServer
{
    /// <summary>Activates an installation. Throws <see cref="ActivationRefusedException"/> on refusal.</summary>
    Task<ActivationResponse> ActivateAsync(string baseUrl, ActivationRequest request, CancellationToken ct);

    /// <summary>
    /// Renews this installation's licence: carries our changes down and resets the offline
    /// clock. Throws <see cref="ActivationRefusedException"/> on refusal or when unreachable.
    /// </summary>
    Task<LicenceRefreshResponse> RefreshAsync(string baseUrl, LicenceRefreshRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>Asks the server to register a new shop and email the owner a code.</summary>
    Task<RegistrationStartResponse> RegistrationStartAsync(
        string baseUrl, RegistrationStartRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>Registers one more espace for a shop owner we have already approved.</summary>
    Task<RegistrationVerifyResponse> RegisterEspaceAsync(
        string baseUrl, RegistrationEspaceRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>Confirms the emailed code; the shop then exists on the server, pending our approval.</summary>
    Task<RegistrationVerifyResponse> RegistrationVerifyAsync(
        string baseUrl, RegistrationVerifyRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>
/// The real client. Every installation calls this once before it will run, including in
/// local mode - a shop that runs entirely offline afterwards still has to be recognised
/// once, and that single call is what makes a self-issued credentials file worthless.
/// </summary>
public sealed class HttpLicenceServer(HttpClient http, ILogger<HttpLicenceServer> logger) : ILicenceServer
{
    public Task<ActivationResponse> ActivateAsync(
        string baseUrl, ActivationRequest request, CancellationToken ct) =>
        PostAsync<ActivationRequest, ActivationResponse>(
            baseUrl, "api/activation",
            "Impossible de joindre le serveur Lonnii. Connectez cet ordinateur à Internet " +
            "le temps de l'activation, puis réessayez.",
            request, ct);

    public Task<LicenceRefreshResponse> RefreshAsync(
        string baseUrl, LicenceRefreshRequest request, CancellationToken ct) =>
        PostAsync<LicenceRefreshRequest, LicenceRefreshResponse>(
            baseUrl, "api/licence/refresh",
            "Impossible de joindre le serveur Lonnii. Connectez cet ordinateur à Internet " +
            "pour renouveler la licence.",
            request, ct);

    public Task<RegistrationStartResponse> RegistrationStartAsync(
        string baseUrl, RegistrationStartRequest request, CancellationToken ct) =>
        PostAsync<RegistrationStartRequest, RegistrationStartResponse>(
            baseUrl, "api/registration/start",
            "Impossible de joindre le serveur Lonnii. Vérifiez la connexion Internet, puis réessayez.",
            request, ct);

    public Task<RegistrationVerifyResponse> RegisterEspaceAsync(
        string baseUrl, RegistrationEspaceRequest request, CancellationToken ct) =>
        PostAsync<RegistrationEspaceRequest, RegistrationVerifyResponse>(
            baseUrl, "api/registration/espace",
            "Impossible de joindre le serveur Lonnii. Connectez cet ordinateur à Internet pour créer un espace.",
            request, ct);

    public Task<RegistrationVerifyResponse> RegistrationVerifyAsync(
        string baseUrl, RegistrationVerifyRequest request, CancellationToken ct) =>
        PostAsync<RegistrationVerifyRequest, RegistrationVerifyResponse>(
            baseUrl, "api/registration/verify",
            "Impossible de joindre le serveur Lonnii. Vérifiez la connexion Internet, puis réessayez.",
            request, ct);

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string baseUrl, string path, string unreachableMessage, TRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ActivationRefusedException("Aucune adresse de serveur dans le fichier d'identifiants.");

        var url = $"{baseUrl.TrimEnd('/')}/{path}";

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(url, request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Being specific matters here: "no internet" and "licence refused" need very
            // different reactions from the shop, and a generic failure would send them
            // chasing the wrong one.
            logger.LogWarning(e, "Licence server unreachable at {Url}", url);
            throw new ActivationRefusedException(unreachableMessage);
        }

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(ct)
                   ?? throw new ActivationRefusedException("Réponse du serveur Lonnii illisible.");
        }

        // Pass the server's own wording through: it already explains an expired
        // subscription or an exhausted machine limit better than anything generic.
        string? message = null;
        try
        {
            message = (await response.Content.ReadFromJsonAsync<ApiError>(ct))?.Error;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            // Not JSON - fall through to the default message below.
        }

        throw new ActivationRefusedException(
            message ?? "Demande refusée par le serveur Lonnii.",
            response.StatusCode);
    }
}
