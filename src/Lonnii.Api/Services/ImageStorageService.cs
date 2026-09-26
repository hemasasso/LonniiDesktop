using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Lonnii.Api.Services;

/// <summary>
/// Stores product and category photos on the host laptop's disk, under the same data
/// directory as the database, and serves them back through <c>/api/images/...</c>.
///
/// Not part of Lonnii Business - the web app keeps uploaded files under its own
/// <c>uploads/</c> tree and serves them as static files. The desktop host plays the same
/// role (photos live only on the host, never on a client), but goes through this service
/// so every read is checked against the caller's group before the bytes are returned.
///
/// Decodes with ImageSharp, not System.Drawing.Common: GDI+ - the native library behind
/// System.Drawing - rejects a real slice of ordinary real-world JPEGs (CMYK ones out of
/// Photoshop above all) with a bare "Parameter is not valid", which showed up here as a
/// perfectly good .jpg being told it was not a valid image. ImageSharp is a pure managed
/// decoder with none of GDI+'s format gaps, and works the same on every OS the API might
/// one day run on.
/// </summary>
public class ImageStorageService
{
    /// <summary>Subfolder names, also used as the first URL segment after <c>/api/images/</c>.</summary>
    public static class Folders
    {
        public const string Products = "products";
        public const string Categories = "categories";

        /// <summary>A workspace's receipt logo. One file per group, named for the group id.</summary>
        public const string ReceiptLogos = "receipt-logos";

        /// <summary>A workspace's payment QR code. Separate from the logo rather than sharing
        /// a folder so the stored filename can stay the bare group id, which is what lets
        /// <c>ImageEndpoints</c> check ownership by comparing it to the caller's group.</summary>
        public const string ReceiptQrCodes = "receipt-qrcodes";

        /// <summary>Every folder this service will read from or write to.</summary>
        public static readonly string[] All = [Products, Categories, ReceiptLogos, ReceiptQrCodes];
    }

    /// <summary>Longest edge a stored photo is allowed to have, in pixels.</summary>
    private const int MaxDimension = 1024;

    /// <summary>Largest upload accepted, before decoding. Keeps a phone photo from filling the disk.</summary>
    public const long MaxUploadBytes = 8 * 1024 * 1024;

    private readonly string _root;

    public ImageStorageService(string dataDirectory)
    {
        _root = Path.Combine(dataDirectory, "images");
        foreach (var folder in Folders.All) Directory.CreateDirectory(Path.Combine(_root, folder));
    }

    /// <summary>
    /// Resizes and re-encodes an uploaded image, replacing whatever the entity previously
    /// pointed at. Returns the API-relative URL to store on the entity.
    /// </summary>
    /// <param name="folder">One of <see cref="Folders"/>.</param>
    /// <param name="entityId">The product, category or group id the image belongs to.</param>
    /// <param name="content">The uploaded file's bytes.</param>
    /// <param name="previousUrl">The entity's current <c>ImageUrl</c>, if any, to delete.</param>
    /// <param name="lossless">
    /// Stores PNG instead of JPEG. Set for anything whose meaning is in its hard edges -
    /// a QR code above all: JPEG's ringing around the black/white boundaries survives the
    /// shrink to a receipt's ~120px square badly enough to stop a phone reading it.
    /// </param>
    /// <exception cref="InvalidImageException">The upload was not a decodable image.</exception>
    public string Save(string folder, string entityId, Stream content, string? previousUrl, bool lossless = false)
    {
        using var image = DecodeOrThrow(content);
        ResizeToFit(image, MaxDimension);

        // A fresh filename per upload, not a fixed one per entity, so a client that has
        // already loaded the old photo is never handed stale bytes under the same name.
        var fileName = $"{entityId}_{DateTime.UtcNow.Ticks}{(lossless ? ".png" : ".jpg")}";
        var path = Path.Combine(_root, folder, fileName);

        if (lossless)
        {
            image.SaveAsPng(path);
        }
        else
        {
            // JPEG has no alpha channel. Without compositing onto an opaque backdrop
            // first, encoding a transparent PNG straight to JPEG keeps whatever RGB
            // values sat under the discarded alpha - commonly black, since many editors
            // leave fully-transparent pixels at RGB (0,0,0) - so a product cut out on a
            // transparent background came out with a black backdrop instead of white.
            image.Mutate(x => x.BackgroundColor(Color.White));
            image.SaveAsJpeg(path, new JpegEncoder { Quality = 82 });
        }

        DeleteIfOwned(previousUrl);

        return $"/api/images/{folder}/{fileName}";
    }

    /// <summary>The media type a stored file should be served as, from its extension. Only
    /// the two formats <see cref="Save"/> writes are possible.</summary>
    public static string ContentTypeFor(string fileName) =>
        fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

    /// <summary>Removes the file behind an <c>ImageUrl</c>, if there is one and it is ours.</summary>
    public void DeleteIfOwned(string? imageUrl)
    {
        var path = ResolvePhysicalPath(imageUrl);
        if (path is null) return;

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A photo that fails to delete is not worth failing the request over; it is
            // an orphaned file on disk, not data loss.
        }
    }

    /// <summary>
    /// Opens a stored photo for reading, given the two URL segments after
    /// <c>/api/images/</c>. Returns null when the folder is unknown or the name does not
    /// resolve to a real file inside it - callers should answer with 404 either way, so
    /// the two cases are not distinguished.
    /// </summary>
    public FileStream? OpenRead(string folder, string fileName)
    {
        if (!Folders.All.Contains(folder)) return null;

        // Path.GetFileName strips any directory component a caller might smuggle in
        // (".." segments, a rooted path), so the result can only ever name a file
        // directly inside the requested folder.
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeName)) return null;

        var path = Path.Combine(_root, folder, safeName);

        // Belt and braces: confirm the resolved path is still inside our root before
        // opening it, in case some future change to the join above stops being safe.
        var fullRoot = Path.GetFullPath(Path.Combine(_root, folder)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return null;

        if (!File.Exists(fullPath)) return null;

        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>
    /// The entity id a stored filename was generated for, i.e. everything before the
    /// last <c>_</c>. Used to check that a requested photo belongs to the caller's group
    /// before the bytes are served.
    /// </summary>
    public static string? EntityIdFromFileName(string fileName)
    {
        var separator = fileName.LastIndexOf('_');
        return separator > 0 ? fileName[..separator] : null;
    }

    private string? ResolvePhysicalPath(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;

        const string prefix = "/api/images/";
        if (!imageUrl.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var parts = imageUrl[prefix.Length..].Split('/', 2);
        if (parts.Length != 2) return null;

        var safeName = Path.GetFileName(parts[1]);
        if (string.IsNullOrEmpty(safeName)) return null;

        return Path.Combine(_root, parts[0], safeName);
    }

    private static Image DecodeOrThrow(Stream content)
    {
        try
        {
            // Copied to a MemoryStream first: Image.Load keeps the source stream open and
            // reads from it lazily, which would break once the caller's request stream is
            // disposed.
            var buffer = new MemoryStream();
            content.CopyTo(buffer);
            buffer.Position = 0;
            return Image.Load(buffer);
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
            throw new InvalidImageException();
        }
    }

    private static void ResizeToFit(Image image, int maxDimension)
    {
        if (image.Width <= maxDimension && image.Height <= maxDimension) return;

        var scale = Math.Min((double)maxDimension / image.Width, (double)maxDimension / image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));

        image.Mutate(x => x.Resize(width, height, KnownResamplers.Bicubic));
    }
}

/// <summary>An uploaded file could not be decoded as an image.</summary>
public class InvalidImageException : Exception;
