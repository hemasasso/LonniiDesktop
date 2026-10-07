using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Lonnii.Client.Services;

/// <summary>
/// The short tone that says "that scan worked". Played from the file shipped beside the program
/// (<c>Assets\Lonnii_Bus_ScanTone.mp3</c>) rather than embedded, because <see cref="MediaPlayer"/>
/// opens files by path, not from resources.
///
/// <para>
/// A missing file or no sound device is not an error worth interrupting a sale for: the tone is
/// feedback, and the product is already in the cart either way.
/// </para>
/// </summary>
public static class ScanTone
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Lonnii_Bus_ScanTone.mp3");

    private static MediaPlayer? _player;

    /// <summary>Plays the tone from the start, cutting off one still sounding so rapid scans each get one.</summary>
    public static void Play()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            _player ??= Create();
            _player.Stop();
            _player.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or UriFormatException)
        {
            // No audio device, or the media stack refused: stay silent.
        }
    }

    private static MediaPlayer Create()
    {
        var player = new MediaPlayer { Volume = 1.0 };
        player.Open(new Uri(FilePath, UriKind.Absolute));
        return player;
    }
}
