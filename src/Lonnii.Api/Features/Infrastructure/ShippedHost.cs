using Lonnii.Data.Entities;

namespace Lonnii.Api.Features.Infrastructure;

/// <summary>
/// Whether this build is the one installed on shops' computers (the Windows installer), as opposed
/// to our licence server on OCI or a development build.
///
/// <para>
/// The same API runs in all three, and until now it worked out which one it was from things the
/// shop controls: an empty <c>groupes.licence_server_url</c> in lonnii.db made a shop's host behave
/// like a self-built one and stop enforcing its licence; <c>appsettings.json</c> could switch manual
/// setup on and point at another licence server; and a PostgreSQL provider made the host believe it
/// was our server. On a shipped host none of that is read any more: this is decided when the
/// installer is built (<c>LONNII_SHIPPED_HOST</c>, set by deploy/windows/build-installer.ps1), and
/// a shop cannot change a compiled constant without patching the program itself.
/// </para>
/// <para>
/// Development builds, the OCI build and the tests keep the configuration switches.
/// </para>
/// </summary>
public static class ShippedHost
{
    /// <summary>True when this build was made by the Windows installer script.</summary>
    public const bool CompiledAsShipped =
#if LONNII_SHIPPED_HOST
        true;
#else
        false;
#endif

    /// <summary>What the API follows: the compiled value, flipped only by the tests (which run the
    /// shipped rules in their own serial collection).</summary>
    public static bool IsShipped { get; internal set; } = CompiledAsShipped;

    /// <summary>Our licence server, the only one a shipped host ever talks to.</summary>
    public const string LicenceServerUrl = "https://www.lonnii.com";

    /// <summary>The licence server to register and activate with: built in on a shipped host,
    /// from <c>Lonnii:LicenceServerUrl</c> otherwise (empty when unset).</summary>
    public static string ConfiguredLicenceServerUrl(IConfiguration config) =>
        IsShipped ? LicenceServerUrl : config["Lonnii:LicenceServerUrl"]?.Trim() ?? string.Empty;

    /// <summary>The licence server <paramref name="groupe"/> answers to: on a shipped host always ours,
    /// whatever its row says - blanking the column no longer turns its licence off.</summary>
    public static string? LicenceServerUrlFor(Groupe groupe) => LicenceServerUrlFor(groupe.LicenceServerUrl);

    /// <summary>The licence server to use given one read from a file or the database.</summary>
    public static string? LicenceServerUrlFor(string? given) => IsShipped ? LicenceServerUrl : given;

    /// <summary>Whether the first account and espaces may be created by hand, with no licence server.
    /// Never on a shipped host, whatever its appsettings.json says.</summary>
    public static bool ManualSetupAllowed(IConfiguration config) =>
        !IsShipped && config.GetValue("Lonnii:AllowManualSetup", false);

    /// <summary>Whether a new espace must be registered with Lonnii first. Always on a shipped host.</summary>
    public static bool OnlineRegistrationRequired(IConfiguration config) =>
        IsShipped || config.GetValue("Lonnii:OnlineRegistration:Required", true);

    /// <summary>A phone-only shop lives on our server and has no computer to check. A shop's own
    /// computer never hosts one, so on a shipped host the column (editable like any other) is ignored
    /// and its machines are always checked.</summary>
    public static bool IsPhoneOnly(Groupe groupe) => groupe.PhoneOnly && !IsShipped;
}
