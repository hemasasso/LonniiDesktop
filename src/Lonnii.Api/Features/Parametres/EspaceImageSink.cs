namespace Lonnii.Api.Features.Parametres;

/// <summary>
/// Where the photos go while an espace is copied. Rows carry an API-relative URL
/// (<c>/api/images/products/&lt;file&gt;</c>) whose bytes live on the host's disk, so a copy
/// has to move the file as well as the column - in opposite directions depending on which
/// way the transfer runs, which is the whole reason this is an interface.
/// </summary>
internal interface IEspaceImageSink
{
    /// <summary>
    /// Moves the image behind <paramref name="sourceUrl"/> and returns the URL the copied
    /// row should carry. Null in, null out; null also comes back when the image cannot be
    /// found, which leaves the copied row with no photo rather than with a broken link.
    /// </summary>
    /// <param name="targetEntityId">The id the copied row will have - the stored filename is
    /// built from it, and <c>ImageEndpoints</c> checks ownership by reading it back.</param>
    /// <param name="lossless">PNG rather than JPEG; set for the payment QR code.</param>
    Task<string?> TransferAsync(string? sourceUrl, string targetEntityId, bool lossless, CancellationToken ct);

    /// <summary>How many images have been moved so far.</summary>
    int Count { get; }
}

/// <summary>Reads each photo off the host's disk and stores its bytes in the archive.</summary>
internal sealed class ExportImageSink(ImageStorageService images, EspaceArchive archive) : IEspaceImageSink
{
    public int Count { get; private set; }

    /// <summary>
    /// The URL is returned unchanged: inside the archive it is the key the bytes are stored
    /// under, so the exported rows keep pointing at their own photos.
    /// </summary>
    public async Task<string?> TransferAsync(
        string? sourceUrl, string targetEntityId, bool lossless, CancellationToken ct)
    {
        if (EspaceImages.Parse(sourceUrl) is not { } image) return null;

        using var stream = images.OpenRead(image.Folder, image.FileName);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        await archive.PutImageAsync(sourceUrl!, image.Folder, buffer.ToArray(), ct);
        Count++;

        return sourceUrl;
    }
}

/// <summary>
/// Cloud-backup export: the rows keep their image URLs and the bytes stay out of the archive.
/// Photos travel separately, by name, so a backup every few minutes uploads each photo once
/// instead of re-sending every one inside every snapshot.
/// </summary>
internal sealed class ReferenceImageSink(ImageStorageService images) : IEspaceImageSink
{
    private readonly Dictionary<string, (string Folder, string Name)> _refs = [];

    public int Count => _refs.Count;

    public IEnumerable<(string Folder, string Name)> Refs => _refs.Values;

    public Task<string?> TransferAsync(string? sourceUrl, string targetEntityId, bool lossless, CancellationToken ct)
    {
        // A row pointing at a file that is gone gets no photo rather than a broken link.
        if (EspaceImages.Parse(sourceUrl) is not { } image || images.ExistingPath(image.Folder, image.FileName) is null)
            return Task.FromResult<string?>(null);

        _refs[sourceUrl!] = (image.Folder, image.FileName);
        return Task.FromResult<string?>(sourceUrl);
    }

    public void Add(string? url)
    {
        if (EspaceImages.Parse(url) is { } image && images.ExistingPath(image.Folder, image.FileName) is not null)
            _refs[url!] = (image.Folder, image.FileName);
    }
}

/// <summary>
/// Cloud restore: the photos were already downloaded under their original names, so a row
/// keeps its URL if the file is on disk and loses it (rather than showing a broken link) if not.
/// </summary>
internal sealed class KeepImageSink(ImageStorageService images) : IEspaceImageSink
{
    public int Count { get; private set; }

    public Task<string?> TransferAsync(string? sourceUrl, string targetEntityId, bool lossless, CancellationToken ct)
    {
        if (EspaceImages.Parse(sourceUrl) is not { } image || images.ExistingPath(image.Folder, image.FileName) is null)
            return Task.FromResult<string?>(null);

        Count++;
        return Task.FromResult<string?>(sourceUrl);
    }
}

/// <summary>
/// Writes each photo from the archive back onto the host's disk, under a filename built from
/// the receiving row's id. Files already written are remembered so a failed import can take
/// them back off the disk.
/// </summary>
internal sealed class ImportImageSink(ImageStorageService images, EspaceArchive archive) : IEspaceImageSink
{
    private readonly List<string> _written = [];

    public int Count => _written.Count;

    public async Task<string?> TransferAsync(
        string? sourceUrl, string targetEntityId, bool lossless, CancellationToken ct)
    {
        if (EspaceImages.Parse(sourceUrl) is not { } image) return null;

        var content = await archive.GetImageAsync(sourceUrl!, ct);
        if (content is null || content.Length == 0) return null;

        using var buffer = new MemoryStream(content);

        try
        {
            // Through the ordinary store, so an imported photo is re-encoded and size-capped
            // exactly like one someone uploads by hand - an archive is a file a customer
            // hands us, and nothing in it should reach the disk undecoded.
            var url = images.Save(image.Folder, targetEntityId, buffer, previousUrl: null, lossless);
            _written.Add(url);
            return url;
        }
        catch (InvalidImageException)
        {
            return null;
        }
    }

    /// <summary>Deletes everything this sink wrote. Called when the import is rolled back:
    /// the database transaction undoes the rows, and nothing else would undo the files.</summary>
    public void Rollback()
    {
        foreach (var url in _written) images.DeleteIfOwned(url);
        _written.Clear();
    }
}

/// <summary>Splits an API-relative image URL into the two segments the store takes.</summary>
internal static class EspaceImages
{
    private const string Prefix = "/api/images/";

    /// <summary>Null for a blank URL, or for anything that is not one of ours.</summary>
    public static (string Folder, string FileName)? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var parts = url[Prefix.Length..].Split('/', 2);
        if (parts.Length != 2 || parts[1].Length == 0) return null;
        if (!ImageStorageService.Folders.All.Contains(parts[0])) return null;

        return (parts[0], parts[1]);
    }
}
