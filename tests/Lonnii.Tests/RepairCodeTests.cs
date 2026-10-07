using System.Security.Cryptography;
using Lonnii.Shared.Security;

namespace Lonnii.Tests;

/// <summary>
/// The repair code is what stands between a shop and pointing its tills at a server of its
/// choosing, so the cases that matter are the refusals: another till, another address, a
/// tampered or self-made code.
/// </summary>
public class RepairCodeTests
{
    private const string Device = "device-fingerprint-A";
    private const string Address = "192.168.1.20:5280";
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static (byte[] Private, string Public) NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKey(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static string Issue(byte[] key, string device = Device, string address = Address, int hours = 24) =>
        RepairCode.Issue(key, RepairCode.DeviceTag(device), address, Now.AddHours(hours));

    [Fact]
    public void Valid_code_is_accepted_for_its_till_and_address()
    {
        var (priv, pub) = NewKey();

        Assert.Equal(RepairCodeResult.Valid,
            RepairCode.Check(Issue(priv), Device, Address, Now, checkExpiry: true, pub));
    }

    [Fact]
    public void Address_spelling_does_not_matter()
    {
        var (priv, pub) = NewKey();

        Assert.Equal(RepairCodeResult.Valid,
            RepairCode.Check(Issue(priv), Device, "HTTP://192.168.1.20:5280/", Now, true, pub));
    }

    [Fact]
    public void Code_for_another_till_is_refused()
    {
        var (priv, pub) = NewKey();

        Assert.Equal(RepairCodeResult.WrongDevice,
            RepairCode.Check(Issue(priv), "device-fingerprint-B", Address, Now, true, pub));
    }

    [Fact]
    public void Code_for_another_address_is_refused()
    {
        var (priv, pub) = NewKey();

        Assert.Equal(RepairCodeResult.WrongAddress,
            RepairCode.Check(Issue(priv), Device, "10.8.0.5:5280", Now, true, pub));
    }

    [Fact]
    public void Expired_code_is_refused_when_typed_but_not_when_rechecked()
    {
        var (priv, pub) = NewKey();
        var code = Issue(priv, hours: 1);
        var later = Now.AddDays(30);

        Assert.Equal(RepairCodeResult.Expired, RepairCode.Check(code, Device, Address, later, true, pub));
        Assert.Equal(RepairCodeResult.Valid, RepairCode.Check(code, Device, Address, later, false, pub));
    }

    [Fact]
    public void Code_signed_with_another_key_is_refused()
    {
        var (_, pub) = NewKey();
        var (otherPriv, _) = NewKey();

        Assert.Equal(RepairCodeResult.BadSignature,
            RepairCode.Check(Issue(otherPriv), Device, Address, Now, true, pub));
    }

    [Fact]
    public void Tampered_code_is_refused()
    {
        var (priv, pub) = NewKey();
        var code = Issue(priv);

        // Flip one character in the middle (a dash-free position).
        var chars = code.ToCharArray();
        var i = 40;
        chars[i] = chars[i] == 'A' ? 'B' : 'A';

        Assert.NotEqual(RepairCodeResult.Valid,
            RepairCode.Check(new string(chars), Device, Address, Now, true, pub));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a code")]
    [InlineData("AAAA-BBBB")]
    public void Garbage_is_malformed(string? code)
    {
        var (_, pub) = NewKey();

        Assert.Equal(RepairCodeResult.Malformed, RepairCode.Check(code, Device, Address, Now, true, pub));
    }

    [Fact]
    public void Code_survives_being_retyped_in_lower_case_with_spaces()
    {
        var (priv, pub) = NewKey();
        var retyped = Issue(priv).ToLowerInvariant().Replace('-', ' ');

        Assert.Equal(RepairCodeResult.Valid, RepairCode.Check(retyped, Device, Address, Now, true, pub));
    }

    [Fact]
    public void Device_tag_is_stable_and_hides_the_fingerprint()
    {
        var tag = RepairCode.DeviceTag(Device);

        Assert.Equal(tag, RepairCode.DeviceTag(Device));
        Assert.NotEqual(tag, RepairCode.DeviceTag("device-fingerprint-B"));
        Assert.DoesNotContain(Device, tag);
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){3}$", tag);
    }

    [Fact]
    public void Shipped_public_key_is_a_valid_key()
    {
        // Guards against the placeholder or a mangled paste: the default key must import.
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(RepairCode.PublicKey), out _);
    }

    /// <summary>
    /// A code made by the dashboard's Node signer (backend/services/repairCode.js in the React app),
    /// so the two implementations cannot drift apart: the byte layout, base32 and signature format
    /// must agree or every code issued from the dashboard would be refused by the till.
    /// </summary>
    [Fact]
    public void Code_issued_by_the_dashboard_is_accepted()
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGXaeV6rggMN5k3yUnfkJIbcz4GqynwPJRrxcsgYNkRkjYhKMAAhWaph4vcNYYtr5K3OnVDQ885umLv83BSz5+g==";
        const string code =
            "AGQMGY-RM6MQN-ZGFG4R-5ZNLT3-N5R6DS-RGAAZN-EMO4V6-QACVBP-5XVE77-U6TV2K-5IEQO2-7QIOZQ-BUS2UN-FWL2V2-4IMK7I-FVAQ5J-J3M2PP-4T5FJJ-Q52GN5-MPAB7F-PD6ZJV-LLP4I6-OZME";

        // Expires in 2090, so the check below needs no clock tricks.
        Assert.Equal(RepairCodeResult.Valid,
            RepairCode.Check(code, "interop-device", "192.168.1.20:5280", Now, true, publicKey));
        Assert.Equal(RepairCodeResult.WrongDevice,
            RepairCode.Check(code, "another-device", "192.168.1.20:5280", Now, true, publicKey));
        Assert.Equal(RepairCodeResult.WrongAddress,
            RepairCode.Check(code, "interop-device", "10.0.0.9:5280", Now, true, publicKey));
    }
}
