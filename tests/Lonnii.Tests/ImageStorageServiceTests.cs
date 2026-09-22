using System.Drawing;
using System.Drawing.Imaging;
using Lonnii.Api.Services;

namespace Lonnii.Tests;

/// <summary>
/// Covers <see cref="ImageStorageService"/> directly: filename safety and the resize/
/// re-encode path, without going through HTTP.
/// </summary>
public class ImageStorageServiceTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly ImageStorageService _service;

    public ImageStorageServiceTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-image-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        _service = new ImageStorageService(_dataDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing over */ }
    }

    /// <summary>A small, genuinely decodable image, so Save exercises the real decode/resize path.</summary>
    private static MemoryStream MakeImage(int width = 20, int height = 20, ImageFormat? format = null)
    {
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.CornflowerBlue);

        var stream = new MemoryStream();
        bitmap.Save(stream, format ?? ImageFormat.Png);
        stream.Position = 0;
        return stream;
    }

    // --- Save / DeleteIfOwned ---

    [Fact]
    public void Save_ReturnsAnApiRelativeUrl_UnderTheRequestedFolder()
    {
        using var image = MakeImage();

        var url = _service.Save(ImageStorageService.Folders.Products, "prod-1", image, previousUrl: null);

        Assert.StartsWith("/api/images/products/", url);
        Assert.EndsWith(".jpg", url);
    }

    [Fact]
    public void Save_WritesADecodableJpegFile_RegardlessOfTheSourceFormat()
    {
        // Uploaded as PNG; stored as JPEG either way, so every product photo weighs the same.
        using var image = MakeImage(format: ImageFormat.Png);

        var url = _service.Save(ImageStorageService.Folders.Products, "prod-1", image, previousUrl: null);
        var fileName = url.Split('/').Last();

        using var stream = _service.OpenRead(ImageStorageService.Folders.Products, fileName);
        Assert.NotNull(stream);

        using var decoded = Image.FromStream(stream);
        Assert.Equal(ImageFormat.Jpeg, decoded.RawFormat);
    }

    [Fact]
    public void Save_ShrinksAnOversizedImage_ToTheMaximumDimension()
    {
        using var image = MakeImage(2000, 500);

        var url = _service.Save(ImageStorageService.Folders.Products, "prod-1", image, previousUrl: null);
        var fileName = url.Split('/').Last();

        using var stream = _service.OpenRead(ImageStorageService.Folders.Products, fileName);
        using var decoded = Image.FromStream(stream!);

        // 2000x500 at a 1024 cap on the longest edge scales to 1024x256.
        Assert.Equal(1024, decoded.Width);
        Assert.Equal(256, decoded.Height);
    }

    [Fact]
    public void Save_LeavesASmallImagesDimensionsUnchanged()
    {
        using var image = MakeImage(20, 15);

        var url = _service.Save(ImageStorageService.Folders.Products, "prod-1", image, previousUrl: null);
        var fileName = url.Split('/').Last();

        using var stream = _service.OpenRead(ImageStorageService.Folders.Products, fileName);
        using var decoded = Image.FromStream(stream!);

        Assert.Equal(20, decoded.Width);
        Assert.Equal(15, decoded.Height);
    }

    [Fact]
    public void Save_DeletesThePreviousFile_WhenReplacingAPhoto()
    {
        using var first = MakeImage();
        var firstUrl = _service.Save(ImageStorageService.Folders.Products, "prod-1", first, previousUrl: null);
        var firstFileName = firstUrl.Split('/').Last();

        using var second = MakeImage();
        _service.Save(ImageStorageService.Folders.Products, "prod-1", second, previousUrl: firstUrl);

        Assert.Null(_service.OpenRead(ImageStorageService.Folders.Products, firstFileName));
    }

    [Fact]
    public void Save_GivesEachUploadAFreshFileName()
    {
        // A stale BitmapImage cached under the old name must never be handed the new
        // photo's bytes; giving every upload its own name is what guarantees that.
        using var first = MakeImage();
        var firstUrl = _service.Save(ImageStorageService.Folders.Products, "prod-1", first, previousUrl: null);

        using var second = MakeImage();
        var secondUrl = _service.Save(ImageStorageService.Folders.Products, "prod-1", second, previousUrl: firstUrl);

        Assert.NotEqual(firstUrl, secondUrl);
    }

    [Fact]
    public void Save_Throws_WhenTheUploadIsNotADecodableImage()
    {
        using var notAnImage = new MemoryStream("this is definitely not an image"u8.ToArray());

        Assert.Throws<InvalidImageException>(() =>
            _service.Save(ImageStorageService.Folders.Products, "prod-1", notAnImage, previousUrl: null));
    }

    [Fact]
    public void DeleteIfOwned_DoesNothing_ForANullOrEmptyUrl()
    {
        // Should simply not throw.
        _service.DeleteIfOwned(null);
        _service.DeleteIfOwned("");
    }

    [Fact]
    public void DeleteIfOwned_IgnoresAUrlItDidNotIssue()
    {
        // Should not throw, and must never touch anything outside its own root.
        _service.DeleteIfOwned("/api/images/products/../../../windows/system32/config");
    }

    // --- OpenRead: path traversal protection ---

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("..\\..\\windows\\win.ini")]
    [InlineData("/etc/passwd")]
    public void OpenRead_RejectsPathTraversalAttempts(string maliciousName)
    {
        var result = _service.OpenRead(ImageStorageService.Folders.Products, maliciousName);

        Assert.Null(result);
    }

    [Fact]
    public void OpenRead_RejectsAnUnknownFolder()
    {
        var result = _service.OpenRead("../../etc", "passwd");

        Assert.Null(result);
    }

    [Fact]
    public void OpenRead_ReturnsNull_ForAFileThatDoesNotExist()
    {
        var result = _service.OpenRead(ImageStorageService.Folders.Products, "nonexistent_123.jpg");

        Assert.Null(result);
    }

    // --- EntityIdFromFileName ---

    [Fact]
    public void EntityIdFromFileName_ReadsEverythingBeforeTheLastUnderscore()
    {
        var id = ImageStorageService.EntityIdFromFileName("abc-123-def_638123456789.jpg");

        Assert.Equal("abc-123-def", id);
    }

    [Fact]
    public void EntityIdFromFileName_ReturnsNull_WhenThereIsNoUnderscore()
    {
        Assert.Null(ImageStorageService.EntityIdFromFileName("noUnderscoreHere.jpg"));
    }
}
