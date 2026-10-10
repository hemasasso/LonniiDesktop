using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Backup;

/// <summary>Raised when the licence server refuses or cannot be reached, with words fit for a shopkeeper.</summary>
public sealed class CloudBackupException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>True when the server says this machine is not on the shop's line of backups.</summary>
    public bool IsStaleEpoch => Status == HttpStatusCode.Conflict;
}

/// <summary>
/// The host's side of the conversation with the licence server's <see cref="BackupEndpoints"/>.
/// One instance per request/run, bound to one workspace by <see cref="For"/>.
/// </summary>
public sealed class CloudBackupClient(IHttpClientFactory factory)
{
    public const string HttpClientName = "cloud-backup";

    private string _baseUrl = string.Empty;
    private string _groupId = string.Empty;
    private string _deviceId = string.Empty;
    private string _token = string.Empty;
    private string? _epoch;

    public CloudBackupClient For(string baseUrl, string groupId, string deviceId, string token, string? epoch)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _groupId = groupId;
        _deviceId = deviceId;
        _token = token;
        _epoch = epoch;
        return this;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}/api/backup/{path}") { Content = content };
        request.Headers.Add(BackupEndpoints.GroupHeader, _groupId);
        request.Headers.Add("x-device-id", _deviceId);
        request.Headers.Add(BackupEndpoints.TokenHeader, _token);
        if (_epoch is not null) request.Headers.Add(BackupEndpoints.EpochHeader, _epoch);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await factory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new CloudBackupException("Impossible de joindre le serveur Lonnii pour la sauvegarde.");
        }

        if (response.IsSuccessStatusCode) return response;

        string message;
        try { message = (await response.Content.ReadFromJsonAsync<ApiError>(ct))?.Error ?? ""; }
        catch { message = ""; }

        if (message.Length == 0) message = $"Sauvegarde refusée par le serveur ({(int)response.StatusCode}).";
        var status = response.StatusCode;
        response.Dispose();
        throw new CloudBackupException(message, status);
    }

    /// <summary>The changes administrators have asked for from afar, waiting for this host.</summary>
    public async Task<IReadOnlyList<RemoteCommandDto>> PendingCommandsAsync(CancellationToken ct)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, "commands"), ct);
        return (await response.Content.ReadFromJsonAsync<RemoteCommandsResponse>(ct))!.Commands;
    }

    /// <summary>Tells the server what this host did with one of them.</summary>
    public async Task ReportCommandAsync(string id, RemoteCommandResult result, CancellationToken ct)
    {
        using var response = await SendAsync(
            Request(HttpMethod.Post, $"commands/{Uri.EscapeDataString(id)}/result", JsonContent.Create(result)), ct);
    }

    /// <summary>The photo sent with a product request, or null when the server no longer has it.</summary>
    public async Task<byte[]?> DownloadCommandPhotoAsync(string id, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(Request(HttpMethod.Get, $"commands/{Uri.EscapeDataString(id)}/photo"), ct);
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (CloudBackupException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<BackupInfoDto> InfoAsync(CancellationToken ct)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, "info"), ct);
        return (await response.Content.ReadFromJsonAsync<BackupInfoDto>(ct))!;
    }

    /// <summary>Opens or confirms the line of backups and remembers the epoch for later calls.</summary>
    public async Task<BackupBeginResponse> BeginAsync(CancellationToken ct)
    {
        using var response = await SendAsync(
            Request(HttpMethod.Post, "begin", JsonContent.Create(new BackupBeginRequest(_epoch))), ct);

        var begun = (await response.Content.ReadFromJsonAsync<BackupBeginResponse>(ct))!;
        _epoch = begun.Epoch;
        return begun;
    }

    public async Task<BackupBeginResponse> ResetAsync(CancellationToken ct)
    {
        using var response = await SendAsync(Request(HttpMethod.Post, "reset"), ct);
        var begun = (await response.Content.ReadFromJsonAsync<BackupBeginResponse>(ct))!;
        _epoch = begun.Epoch;
        return begun;
    }

    public async Task<IReadOnlyList<BackupImageRef>> MissingImagesAsync(
        IReadOnlyList<BackupImageRef> images, CancellationToken ct)
    {
        if (images.Count == 0) return [];

        using var response = await SendAsync(
            Request(HttpMethod.Post, "images/missing", JsonContent.Create(new BackupImagesRequest(images))), ct);

        return (await response.Content.ReadFromJsonAsync<BackupImagesMissingDto>(ct))!.Missing;
    }

    public async Task PutImageAsync(BackupImageRef image, string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendAsync(
            Request(HttpMethod.Put, $"images/{Uri.EscapeDataString(image.Folder)}/{Uri.EscapeDataString(image.Name)}", content), ct);
    }

    public async Task PutSnapshotAsync(string gzPath, int records, int images, CancellationToken ct)
    {
        await using var file = File.OpenRead(gzPath);
        using var content = new StreamContent(file, 1 << 20);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");

        using var response = await SendAsync(
            Request(HttpMethod.Put, $"snapshot?records={records}&images={images}", content), ct);
    }

    /// <summary>Downloads the newest snapshot to <paramref name="path"/> (gzip, as stored).</summary>
    public async Task DownloadSnapshotAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, "snapshot"), ct);
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, ct);
    }

    public async Task DownloadImageAsync(BackupImageRef image, string path, CancellationToken ct)
    {
        using var response = await SendAsync(
            Request(HttpMethod.Get, $"images/{Uri.EscapeDataString(image.Folder)}/{Uri.EscapeDataString(image.Name)}"), ct);
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, ct);
    }
}
