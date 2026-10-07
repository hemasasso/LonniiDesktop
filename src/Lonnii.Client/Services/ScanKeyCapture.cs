using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Lonnii.Client.Services;

/// <summary>
/// Puts back the characters a barcode scanner meant to type when the laptop's keyboard layout
/// turned them into something else.
///
/// <para>
/// A scanner is a keyboard that sends key positions. On a French (AZERTY) laptop the number row
/// gives <c>&amp; é " ' ( - è _ ç à</c> unless Shift is held, so a scanned <c>123</c> lands in the
/// box as <c>&amp;é"</c>, and letters are swapped too (A and Q, Z and W, M and the comma).
/// </para>
///
/// <para>
/// This watches the raw key presses going into one <see cref="TextBox"/>, and for each also works
/// out what the same key would have typed on a US layout. A burst of keys - several characters,
/// each within a few milliseconds of the last, faster than anyone types - is a scan. For a scan
/// it prefers the text the layout produced when that looks like a barcode, and otherwise the
/// US-layout text. Slow typing, edits and pastes are never touched, so a person writing a
/// product name with accents in the same box is unaffected.
/// </para>
/// </summary>
public sealed class ScanKeyCapture
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    /// <summary>Longest pause between two keys that still counts as one scan.</summary>
    private const int MaxGapMs = 80;

    /// <summary>A scan shorter than this is indistinguishable from fast typing.</summary>
    private const int MinScanLength = 4;

    /// <summary>A pause this long means whatever came before was a separate entry.</summary>
    private const int StaleMs = 600;

    private readonly TextBox _box;
    private readonly StringBuilder _layout = new();
    private readonly StringBuilder _us = new();
    private readonly DispatcherTimer _settle;
    private int _lastTime;
    private bool _burst = true;
    private bool _attached;

    /// <summary>Raised a moment after a scan ends, for scanners that send no Enter.</summary>
    public event Action? ScanCompleted;

    /// <param name="box">The box scans are typed into.</param>
    /// <param name="autoCorrect">Fix the box's text by itself once a scan ends. Turn off when the
    /// caller wants to decide at Enter.</param>
    public ScanKeyCapture(TextBox box, bool autoCorrect = true)
    {
        _box = box;
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (autoCorrect) CorrectBox();
            if (IsScan(_box.Text)) ScanCompleted?.Invoke();
        };

        box.Loaded += (_, _) => Attach();
        box.Unloaded += (_, _) => Detach();
        box.TextChanged += (_, _) => { if (box.Text.Length == 0) Reset(); };
        if (box.IsLoaded) Attach();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        ComponentDispatcher.ThreadPreprocessMessage += OnMessage;
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        ComponentDispatcher.ThreadPreprocessMessage -= OnMessage;
    }

    /// <summary>Forgets the keys seen so far - call after acting on a scan.</summary>
    public void Reset()
    {
        _layout.Clear();
        _us.Clear();
        _burst = true;
    }

    /// <summary>
    /// The text a US-layout keyboard would have typed for the scan that produced
    /// <paramref name="typed"/>, or null when it was not a scan or the two are the same.
    /// </summary>
    public string? Alternate(string typed)
    {
        if (!_burst || _layout.Length < MinScanLength) return null;
        if (!string.Equals(_layout.ToString(), typed, StringComparison.Ordinal)) return null; // edited or pasted
        var us = _us.ToString();
        return us == typed ? null : us;
    }

    /// <summary>True when <paramref name="typed"/> came from a scan (read either way) rather than from typing.</summary>
    public bool IsScan(string typed) =>
        _burst && _layout.Length >= MinScanLength
        && (string.Equals(_layout.ToString(), typed, StringComparison.Ordinal)
            || string.Equals(_us.ToString(), typed, StringComparison.Ordinal));

    /// <summary>The best reading of <paramref name="typed"/>: as typed unless it is a scan whose
    /// US-layout reading is the one that looks like a barcode.</summary>
    public string Resolve(string typed)
    {
        if (Alternate(typed) is not { } us) return typed;
        if (LooksLikeBarcode(typed)) return typed;
        return LooksLikeBarcode(us) ? us : typed;
    }

    /// <summary>Barcodes are letters, digits and a few separators - never accents or most punctuation.</summary>
    public static bool LooksLikeBarcode(string text) =>
        text.Length > 0 && text.All(c => (c is >= '0' and <= '9') || (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z')
                                        || c is '-' or '.' or '_' or '/' or '+' or '$' or '%');

    private void CorrectBox()
    {
        var resolved = Resolve(_box.Text);
        if (resolved == _box.Text) return;

        _box.Text = resolved;
        _box.CaretIndex = resolved.Length;
    }

    // --- Keys -------------------------------------------------------------------------

    private void OnMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message is not (WmKeyDown or WmSysKeyDown) || !_box.IsKeyboardFocused) return;

        var vk = (uint)msg.wParam.ToInt64();
        if (vk is 0x10 or 0x11 or 0x12 or 0x14 or (>= 0xA0 and <= 0xA5)) return; // Shift, Ctrl, Alt, Caps Lock

        // Editing keys mean a person is at the keyboard, not a scanner.
        if (vk is 0x08 or 0x2E or 0x25 or 0x27) { Reset(); return; }

        var lParam = msg.lParam.ToInt64();
        var scan = (uint)((lParam >> 16) & 0xFF);
        var extended = ((lParam >> 24) & 1) != 0;
        if (extended) return;

        var layoutChar = Translate(vk, scan, GetKeyboardLayout(0));
        var usHkl = UsLayout.Value;
        var usChar = usHkl == IntPtr.Zero
            ? layoutChar
            : Translate(MapVirtualKeyEx(scan, MapVscToVkEx, usHkl), scan, usHkl);

        // Enter, Tab and the like produce no printable character: they end a scan, not part of it.
        if (layoutChar is null || usChar is null) return;

        var gap = unchecked(msg.time - _lastTime);
        if (_layout.Length > 0 && gap > StaleMs) Reset();
        else if (_layout.Length > 0 && gap > MaxGapMs) _burst = false;
        _lastTime = msg.time;

        _layout.Append(layoutChar.Value);
        _us.Append(usChar.Value);

        _settle.Stop();
        _settle.Start();
    }

    private static char? Translate(uint vk, uint scan, IntPtr hkl)
    {
        var state = new byte[256];
        if (!GetKeyboardState(state)) return null;

        var buffer = new StringBuilder(8);
        // Flag 4: do not disturb the keyboard's dead-key state, so asking has no side effects.
        var count = ToUnicodeEx(vk, scan, state, buffer, buffer.Capacity, 4, hkl);
        return count == 1 && !char.IsControl(buffer[0]) ? buffer[0] : null;
    }

    private static readonly Lazy<IntPtr> UsLayout = new(() => LoadKeyboardLayout("00000409", 0));

    private const uint MapVscToVkEx = 3;

    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keyState);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadKeyboardLayout(string layoutId, uint flags);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint virtualKey, uint scanCode, byte[] keyState,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, int bufferSize, uint flags, IntPtr hkl);
}
