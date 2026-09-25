using Lonnii.Shared.Security;

namespace Lonnii.Data.Entities;

/// <summary>Values stored in <see cref="Device.Platform"/>.</summary>
public static class DevicePlatforms
{
    public const string Windows = "windows";
    public const string Android = "android";
    public const string Ios = "ios";

    public static readonly IReadOnlyList<string> All = [Windows, Android, Ios];
}

/// <summary>
/// A machine bound to a workspace. A shop may bind <see cref="Groupe.MaxDevices"/> of them;
/// beyond that, a new machine is refused.
///
/// <para>
/// Bound to the <em>shop</em>, not to a person - there is deliberately no user id. Tying
/// machines to individuals would lock a cashier out mid-shift the moment they moved to a
/// different till, while doing nothing to stop the copying this is meant to prevent: a
/// shared credentials file gives every extra person their own fresh allowance. The shop
/// gets N machines and its staff use any of them.
/// </para>
/// <para>
/// Distinct from <see cref="UserSession"/>: a session is one sign-in and expires, a binding
/// lasts and survives sign-out and reinstallation.
/// </para>
/// <para>
/// New to the desktop product. Lonnii Business has no equivalent, since a web app never
/// needed to know which machine it was running on.
/// </para>
/// </summary>
public class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// A fingerprint the client derives from the machine, stable across reinstallation.
    /// Opaque to the server, which only ever compares it, so how it is derived can change
    /// without a migration.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>What the shop sees in its device list, normally the computer's name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>One of <see cref="DevicePlatforms"/>.</summary>
    public string Platform { get; set; } = DevicePlatforms.Windows;

    public string? AppVersion { get; set; }

    /// <summary>Where this machine last connected from, kept for the access log.</summary>
    public string? LastIpAddress { get; set; }

    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set when a machine is unbound - normally a till that broke and was replaced. The row
    /// is kept rather than deleted so the same machine returning is still recognisable, and
    /// a revoked binding does not count towards the limit.
    ///
    /// <para>
    /// A shop's own admin may do this: it frees a slot but can never take the shop above
    /// the number it was granted, so it removes a support call without weakening anything.
    /// Raising <see cref="Groupe.MaxDevices"/> stays with us.
    /// </para>
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public bool IsActive => RevokedAt is null;

    public Groupe? Groupe { get; set; }
}
