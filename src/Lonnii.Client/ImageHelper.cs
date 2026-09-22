using System.IO;
using System.Windows.Media.Imaging;

namespace Lonnii.Client;

/// <summary>
/// Turns raw bytes - either downloaded from the API or read from a file the user picked -
/// into a <see cref="BitmapImage"/> an <c>Image</c> control can display.
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// Decodes bytes into a frozen bitmap, safe to hand to any thread or cache. Loading
    /// fully into memory up front (<see cref="BitmapCacheOption.OnLoad"/>) means the
    /// source stream can be disposed immediately after this call returns.
    /// </summary>
    public static BitmapImage FromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        return image;
    }
}
