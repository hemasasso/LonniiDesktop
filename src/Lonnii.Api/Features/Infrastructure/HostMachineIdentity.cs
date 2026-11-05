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
