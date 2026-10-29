using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Lonnii.Api.Features.Auth;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Remote;

/// <summary>
/// Replays on the shop's host a change an administrator made from afar on one of the shop's
/// screens (<see cref="RemoteCommandTypes.Request"/>).
///
/// <para>
/// The request goes through this host's own API over the loopback address, signed in as the sender
/// with a short-lived group session: every check the endpoint makes at the shop - privileges,
/// validation, the licence - is made again here, against the shop's own data. Nothing about the
/// change is trusted because the server accepted it; the server only kept it.
/// </para>
/// </summary>
public sealed class RemoteRequestReplayer(
    IHttpClientFactory factory,
    LonniiDbContext db,
    TokenService tokens,
    GroupSessionService sessions,
    ILogger<RemoteRequestReplayer> logger)
{
    public const string HttpClientName = "remote-replay";

    public async Task<RemoteCommandResult> RunAsync(string groupId, RemoteCommandDto command, CancellationToken ct)
    {
        if (command.Method is null || command.Path is null
            || !RemoteRoutingMiddleware.IsReplayable(command.Method, command.Path)
            || !command.Path.StartsWith('/'))
        {
            return new RemoteCommandResult(RemoteCommandStatuses.Failed, "Demande non autorisée à distance.");
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.IdUser == command.RequestedBy, ct);
        if (user is null)
            return new RemoteCommandResult(RemoteCommandStatuses.Failed, "Le compte de l'expéditeur n'existe plus dans la boutique.");

        var session = await sessions.OpenAsync(user.IdUser, groupId, ct);
        if (session is null)
            return new RemoteCommandResult(RemoteCommandStatuses.Failed, "L'expéditeur ne fait plus partie de la boutique.");

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(command.Method), command.Path.TrimStart('/'));
            if (command.Body is not null)
                request.Content = new StringContent(command.Body, Encoding.UTF8, "application/json");

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Issue(user).Token);
            request.Headers.Add(GroupSessionService.HeaderName, session.SessionToken);

            using var response = await factory.CreateClient(HttpClientName).SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return new RemoteCommandResult(RemoteCommandStatuses.Applied);

            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<ApiError>(ct))?.Error; }
            catch { /* not an ApiError */ }

            return new RemoteCommandResult(RemoteCommandStatuses.Failed,
                message ?? $"Refusée par la boutique ({(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The host's own API did not answer: nothing was applied, so the request waits for the next pass.
            logger.LogWarning("Demande à distance non rejouée, nouvel essai plus tard : {Message}", e.Message);
            throw new RemoteReplayDeferredException();
        }
        finally
        {
            await sessions.CloseAsync(session.SessionToken, CancellationToken.None);
        }
    }
}

/// <summary>The change could not be replayed yet and stays in the queue, untouched.</summary>
public sealed class RemoteReplayDeferredException : Exception;
