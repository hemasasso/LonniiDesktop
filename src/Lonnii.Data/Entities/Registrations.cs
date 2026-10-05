namespace Lonnii.Data.Entities;

/// <summary>
/// A shop's registration that has not been confirmed by email yet.
///
/// <para>
/// Only ever written on the licence server. Nothing real exists until the emailed code comes
/// back: no user, no workspace. That is what stops someone typing another person's address
/// into the form from creating an account in their name, and it keeps unconfirmed
/// registrations out of the list of shops we have agreed to.
/// </para>
/// <para>
/// The password is kept as a BCrypt hash from the start, never in the clear, and the code is
/// kept only as a hash too.
/// </para>
/// </summary>
public class RegistrationRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string ShopName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }

    /// <summary>SHA-256 of the request id and the six-digit code.</summary>
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CodeExpiresAt { get; set; }

    /// <summary>Wrong codes tried so far. Six digits is only safe because this is capped.</summary>
    public int Attempts { get; set; }

    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
