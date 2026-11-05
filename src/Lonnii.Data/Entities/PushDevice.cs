namespace Lonnii.Data.Entities;

/// <summary>
/// A phone that asked to be woken for this user's alerts: Lonnii Business's <c>push_tokens</c>
/// table, same columns. The token is the Expo push token the phone app gets for itself; the
/// shop's host sends to it through Expo's push service (see PushAlertService).
/// Not tied to an espace: a user's phone gets the alerts of every espace they administer.
/// </summary>
public class PushDevice
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string PushToken { get; set; } = string.Empty;

    /// <summary>android or ios.</summary>
    public string? Platform { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
