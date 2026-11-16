using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Lonnii.Api.Features.Infrastructure;

/// <summary>
/// The shop computer itself, as tills and phones pin it. <see cref="TokenService.HostId"/> hashes
/// jwt.key, so reinstalling Lonnii with the data folder deleted made a new one and every till -
/// the shop computer's own app included - refused its own host as "another server". This is
/// built from Windows' MachineGuid instead (as DeviceIdentity is on the client), which survives
/// any reinstall of Lonnii and changes only with another computer or a reinstall of Windows.
///
/// <para>Salted and hashed: it is handed to anyone on the network who asks, and must say
/// nothing about the machine beyond "same as before".</para>
/// <para>Null off Windows (the licence server), where there is no shop computer to pin.</para>
/// </summary>
public static class HostMachineIdentity
{
    public static string? Current { get; } = Compute();

    /// <summary>
    /// This computer's <c>x-device-id</c> as its own desktop app sends it (the client's DeviceIdentity:
    /// "lonnii-device-v1|" + MachineGuid, SHA-256, lower-case hex). Lets the host renew the licence by
    /// itself as the shop computer it is (<see cref="Licensing.LicenceRenewalService"/>). Null off
    /// Windows or when the registry cannot be read.
    /// </summary>
    public static string? DeviceId { get; } = ReadMachineGuid() is { } guid
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("lonnii-device-v1|" + guid))).ToLowerInvariant()
        : null;

    /// <summary>
    /// This computer's second fingerprint (see <see cref="Lonnii.Shared.Security.HardwareSignature"/>),
    /// sent when the host renews its own licence so our server can tell the real machine from a copy of
    /// its MachineGuid. Built from the same inputs the desktop client's DeviceIdentity uses. Null off
    /// Windows (the licence server) or when nothing could be read.
    /// </summary>
    public static string? Hardware { get; } = OperatingSystem.IsWindows()
        ? Lonnii.Shared.Security.HardwareSignature.Of(
            ReadMachineGuid(), WindowsValue("ProductId"), WindowsValue("InstallDate"), SystemVolumeSerial())
        : null;

    private static string? WindowsValue(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string? rootPathName, System.Text.StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        System.Text.StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

    private static string? SystemVolumeSerial()
    {
        if (!OperatingSystem.IsWindows()) return null;
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

    private static string? ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") is string guid && !string.IsNullOrWhiteSpace(guid) ? guid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? Compute()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is not string guid || string.IsNullOrWhiteSpace(guid)) return null;

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("lonnii-host-machine-v1|" + guid)))[..32]
                .ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
