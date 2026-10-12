using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lonnii.Client.Features.CustomerDisplay;

/// <summary>
/// Plays an animated GIF in a WPF <see cref="Image"/>, which on its own shows only the first
/// frame. Every frame is composed once up front - a GIF frame is often just the part of the
/// picture that changed, drawn at an offset over the previous one - then a timer steps through
/// them at the delays the file asks for.
/// </summary>
public sealed class GifAnimator : IDisposable
{
    /// <summary>Beyond this the file is shown still: a long GIF composed in full would cost
    /// hundreds of megabytes on a till.</summary>
    private const int MaxFrames = 400;

    private readonly Image _target;
    private readonly List<(BitmapSource Frame, TimeSpan Delay)> _frames = [];
    private readonly DispatcherTimer _timer = new();
    private int _index;

    private GifAnimator(Image target)
    {
        _target = target;
        _timer.Tick += (_, _) => Next();
    }

    /// <summary>Shows <paramref name="path"/> in <paramref name="target"/>, animated when it is a
    /// GIF with several frames. Returns null for a still picture (already shown).</summary>
    public static GifAnimator? Play(Image target, string path)
    {
        var animator = new GifAnimator(target);
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count < 2 || decoder.Frames.Count > MaxFrames)
            {
                target.Source = decoder.Frames[0];
                return null;
            }
            animator.Compose(decoder);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            target.Source = null;
            return null;
        }

        animator._index = -1;
        animator.Next();
        return animator;
    }

    private void Compose(GifBitmapDecoder decoder)
    {
        var width = Read(decoder.Metadata as BitmapMetadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
        var height = Read(decoder.Metadata as BitmapMetadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;

        BitmapSource? previous = null;
        Rect? clearBeforeNext = null;

        foreach (var frame in decoder.Frames)
        {
            var meta = frame.Metadata as BitmapMetadata;
            var rect = new Rect(Read(meta, "/imgdesc/Left") ?? 0, Read(meta, "/imgdesc/Top") ?? 0, frame.PixelWidth, frame.PixelHeight);
            var delay = Read(meta, "/grctlext/Delay") is { } d and > 1 ? d * 10 : 100; // hundredths of a second
            var disposal = Read(meta, "/grctlext/Disposal") ?? 0;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (previous is not null)
                {
                    // Disposal 2: the previous frame's area goes back to transparent first.
                    if (clearBeforeNext is { } clear)
                    {
                        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                            new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(clear)));
                        dc.DrawImage(previous, new Rect(0, 0, width, height));
                        dc.Pop();
                    }
                    else
                    {
                        dc.DrawImage(previous, new Rect(0, 0, width, height));
                    }
                }
                dc.DrawImage(frame, rect);
            }

            var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            composed.Render(visual);
            composed.Freeze();
            _frames.Add((composed, TimeSpan.FromMilliseconds(delay)));

            // Disposal 3 (restore previous) keeps the frame before this one as the base.
            if (disposal != 3) previous = composed;
            clearBeforeNext = disposal == 2 ? rect : null;
        }
    }

    private static int? Read(BitmapMetadata? meta, string query)
    {
        try
        {
            return meta?.GetQuery(query) switch
            {
                ushort u => u,
                byte b => b,
                int i => i,
                _ => null,
            };
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Next()
    {
        _index = (_index + 1) % _frames.Count;
        _target.Source = _frames[_index].Frame;
        _timer.Interval = _frames[_index].Delay;
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _frames.Clear();
    }
}
