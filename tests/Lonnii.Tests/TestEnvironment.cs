using System.Runtime.CompilerServices;

namespace Lonnii.Tests;

/// <summary>
/// Runs once before any test. Turns off LAN discovery for every in-process host the tests start:
/// each would otherwise try to bind the real discovery port, and a test run could take it from the
/// host actually running on the developer's machine. Also restores the development behaviour of
/// hand-made accounts and espaces, which the shipped host switches off.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("Lonnii__Discovery__Enabled", "false");

        // The tests build workspaces directly, without a licence server: the shipped host does
        // not allow that, so they opt back in. Tests of the shipped behaviour set these per test.
        Environment.SetEnvironmentVariable("Lonnii__AllowManualSetup", "true");
        Environment.SetEnvironmentVariable("Lonnii__OnlineRegistration__Required", "false");
    }
}
