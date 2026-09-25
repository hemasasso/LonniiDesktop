namespace Lonnii.Shared.Security;

/// <summary>
/// How a workspace is deployed. Stored in <c>groupes.mode</c>.
///
/// <para>
/// Lives in Shared rather than beside the entity because the setup tool that issues a
/// customer's credentials file needs the same vocabulary, and that tool deliberately
/// references nothing but Shared - it is never shipped to a customer.
/// </para>
/// </summary>
public static class DeploymentModes
{
    /// <summary>API and SQLite run on the company's own machine. No subscription is checked, no mobile, works offline.</summary>
    public const string Local = "local";

    /// <summary>API and PostgreSQL run on our server; clients keep a SQLite cache and sync. Subscription required.</summary>
    public const string Online = "online";

    public static readonly IReadOnlyList<string> All = [Local, Online];

    /// <summary>Local-mode workspaces are never checked against a subscription.</summary>
    public static bool RequiresSubscription(string? mode) => mode == Online;
}
