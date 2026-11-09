using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lonnii.Client.Services;

/// <summary>
/// Shrinks a photo before it is uploaded: at most <see cref="MaxDimension"/> px on its longest
/// side, JPEG. A phone or camera picture is 3-8 MB; this sends ~100-250 KB instead, saving the
/// shop's data and the server's memory. The server still re-encodes whatever it receives, so this
/// is purely about what crosses the network. Not used for logos and QR codes (kept lossless).
/// </summary>
public static class PhotoShrinker
{
    /// <summary>Same limit the server keeps (ImageStorageService.MaxDimension).</summary>
    public const int MaxDimension = 1024;

    /// <summary>Already this small and within the size limit: sent as it is.</summary>
    private const int SmallEnoughBytes = 300 * 1024;

    /// <summary>The shrunk JPEG and its file name, or the original when it cannot be decoded
    /// (the server then answers with its own message) or is already small.</summary>
    public static (byte[] Content, string FileName) Shrink(byte[] content, string fileName)
    {
        try
        {
            BitmapFrame frame;
            using (var input = new MemoryStream(content))
                frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            var rotation = OrientationAngle(frame);
            var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (longest <= MaxDimension && content.Length <= SmallEnoughBytes && rotation == 0)
                return (content, fileName);

            var scale = Math.Min(1.0, MaxDimension / (double)longest);
            BitmapSource source = frame;
            if (scale < 1) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            if (rotation != 0) source = new TransformedBitmap(source, new RotateTransform(rotation));

            // JPEG has no transparency: drawn on white, as the server does, so a cut-out product
            // does not come out on black.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawImage(source, rect);
            }
            var flat = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            flat.Render(visual);

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(flat));
            using var output = new MemoryStream();
            encoder.Save(output);

            var shrunk = output.ToArray();
            return shrunk.Length < content.Length || rotation != 0
                ? (shrunk, Path.ChangeExtension(fileName, ".jpg"))
                : (content, fileName);
        }
        catch (Exception)
        {
            return (content, fileName);
        }
    }

    /// <summary>EXIF orientation as a clockwise angle (camera photos are often stored sideways
    /// with this flag). Re-encoding drops the flag, so the rotation is applied to the pixels.</summary>
    private static double OrientationAngle(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o)
                return o switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        }
        catch (Exception)
        {
            // No readable metadata (PNG, BMP...): as it is.
        }
        return 0;
    }
}
