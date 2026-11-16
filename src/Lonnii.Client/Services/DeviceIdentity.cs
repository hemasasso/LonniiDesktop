using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Lonnii.Shared.Security;
using Microsoft.Win32;

namespace Lonnii.Client.Services;

/// <summary>
/// This machine's fingerprint, sent as <c>x-device-id</c> so the server can tell whether
/// the workspace is running on hardware it bound.
///
/// <para>
/// The two properties that matter: it must survive reinstalling Lonnii - otherwise a shop
/// would burn a slot every time it repaired an installation - and it must differ on another
/// shop's computer, which is what makes a copied installation visible.
/// </para>
/// <para>
/// Windows' MachineGuid gives both. It is written when Windows is installed and left alone
/// afterwards, so it outlives our installer while being unique to the machine. It is hashed
/// with a product-specific salt before it leaves here, so nothing identifying the customer's
/// computer is sent, and the value cannot be reused to correlate with anything else on it.
/// </para>
/// <para>
/// This is a fingerprint, not a secret. Someone who controls the machine can read and replay
/// it; cloning a machine bit for bit would clone this too. It raises copying from "copy the
/// folder" to "clone the computer", and obfuscation is what widens that gap - see the
/// licensing notes.
/// </para>
/// </summary>
public static class DeviceIdentity
{
    private const string Salt = "lonnii-device-v1";

    private static readonly string FallbackPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "device-id");

    private static string? _cached;

    /// <summary>
    /// The fingerprint for this machine. Computed once per run: it cannot change while the
    /// application is open, and re-reading the registry on every request would be waste.
    /// </summary>
    public static string Current => _cached ??= Compute();

    /// <summary>A name for the device list, so a shop can tell its tills apart.</summary>
    public static string FriendlyName => Environment.MachineName;

    private static string? _hardware;
    private static bool _hardwareComputed;

    /// <summary>
    /// A second fingerprint (see <see cref="HardwareSignature"/>), sent so our server can tell this
    /// real machine from a copy of its MachineGuid on other hardware. Computed once and cached; null
    /// when nothing could be read. Never blocks the machine - it is reported, not enforced here.
    /// </summary>
    public static string? Hardware
    {
        get
        {
            if (_hardwareComputed) return _hardware;
            _hardware = HardwareSignature.Of(MachineGuid(), WindowsValue("ProductId"), WindowsValue("InstallDate"), SystemVolumeSerial());
            _hardwareComputed = true;
            return _hardware;
        }
    }

    /// <summary>One value from Windows' version key - the product id, the install date - each unique
    /// to a Windows installation, so a MachineGuid copied to another install does not carry them.</summary>
    private static string? WindowsValue(string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue(name)?.ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string? rootPathName, System.Text.StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        System.Text.StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

    /// <summary>The serial of the system drive, written when Windows is installed. A plain MachineGuid
    /// copy onto another computer carries a different one.</summary>
    private static string? SystemVolumeSerial()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(root)) return null;
            return GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0)
                ? serial.ToString("x8") : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The name shown to phones printing on this computer: the one set in Configuration,
    /// or the Windows name.</summary>
    public static string PosteName =>
        string.IsNullOrWhiteSpace(App.Settings.PosteName) ? FriendlyName : App.Settings.PosteName.Trim();

    private static string Compute()
    {
        var source = MachineGuid() ?? StoredFallback();
        return Hash(source);
    }

    /// <summary>
    /// Windows' own machine identifier. Survives our installer; changes only when Windows
    /// itself is reinstalled, which is a new machine as far as a licence is concerned.
    /// </summary>
    private static string? MachineGuid()
    {
        try
        {
            using var key = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

            var value = key?.GetValue("MachineGuid") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A locked-down machine can refuse this read. Better a weaker fingerprint than
            // a till that cannot open at all.
            return null;
        }
    }

    /// <summary>
    /// Used only when the registry cannot be read: a value generated once and kept beside
    /// the client's settings. Weaker, because wiping the profile changes it and costs the
    /// shop a slot - which is why it is the fallback rather than the primary.
    /// </summary>
    private static string StoredFallback()
    {
        try
        {
            if (File.Exists(FallbackPath))
            {
                var existing = File.ReadAllText(FallbackPath).Trim();
                if (!string.IsNullOrWhiteSpace(existing)) return existing;
            }

            var created = Guid.NewGuid().ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
            File.WriteAllText(FallbackPath, created);
            return created;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Last resort: stable for this session, so the app still runs even though the
            // machine will look new next time.
            return Environment.MachineName + "|" + Environment.UserName;
        }
    }

    /// <summary>
    /// This computer as a shop computer reports itself (the API's HostMachineIdentity, same salt
    /// and shape). Equal to a server's HostMachineId only when that server runs on this very PC -
    /// which lets the shop computer's own app accept its host after a reinstall changed the old pin.
    /// </summary>
    public static string? AsHostMachineId { get; } = MachineGuid() is { } guid
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("lonnii-host-machine-v1|" + guid)))[..32].ToLowerInvariant()
        : null;

    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Salt + "|" + source)))
            .ToLowerInvariant();
}
