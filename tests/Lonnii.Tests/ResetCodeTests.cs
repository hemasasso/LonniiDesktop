using System.Security.Cryptography;
using Lonnii.Shared.Security;

namespace Lonnii.Tests;

/// <summary>
/// Offline password reset codes: signed by us, checked by the shop's host, good for one account on
/// one server, once.
/// </summary>
public class ResetCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private static (byte[] PrivateKey, string PublicKey) NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKey(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private const string HostId = "0123456789abcdef0123456789abcdef";
    private const string UserId = "user-1";
    private const string PasswordHash = "$2a$11$abcdefghijklmnopqrstuv";

    private static string Tag(string hostId = HostId, string userId = UserId, string hash = PasswordHash) =>
        ResetCode.AccountTag(hostId, userId, hash);

    [Fact]
    public void A_code_issued_for_the_tag_is_accepted()
    {
        var (priv, pub) = NewKey();
        var code = ResetCode.Issue(priv, Tag(), Now.AddHours(24));

        Assert.Equal(ResetCodeResult.Valid, ResetCode.Check(code, Tag(), Now, pub));
    }

    [Fact]
    public void The_code_dies_once_the_password_has_changed()
    {
        var (priv, pub) = NewKey();
        var code = ResetCode.Issue(priv, Tag(), Now.AddHours(24));

        Assert.Equal(ResetCodeResult.WrongAccount, ResetCode.Check(code, Tag(hash: "$2a$11$another-hash"), Now, pub));
    }

    [Fact]
    public void The_code_does_not_work_for_another_account_or_server()
    {
        var (priv, pub) = NewKey();
        var code = ResetCode.Issue(priv, Tag(), Now.AddHours(24));

        Assert.Equal(ResetCodeResult.WrongAccount, ResetCode.Check(code, Tag(userId: "user-2"), Now, pub));
        Assert.Equal(ResetCodeResult.WrongAccount, ResetCode.Check(code, Tag(hostId: "ffff"), Now, pub));
    }

    [Fact]
    public void An_expired_code_is_refused()
    {
        var (priv, pub) = NewKey();
        var code = ResetCode.Issue(priv, Tag(), Now.AddHours(1));

        Assert.Equal(ResetCodeResult.Expired, ResetCode.Check(code, Tag(), Now.AddHours(2), pub));
    }

    [Fact]
    public void A_code_from_another_key_is_refused()
    {
        var (priv, _) = NewKey();
        var (_, otherPub) = NewKey();
        var code = ResetCode.Issue(priv, Tag(), Now.AddHours(24));

        Assert.Equal(ResetCodeResult.BadSignature, ResetCode.Check(code, Tag(), Now, otherPub));
    }

    [Fact]
    public void A_repair_code_cannot_stand_in_for_a_reset_code()
    {
        var (priv, pub) = NewKey();
        var repair = RepairCode.Issue(priv, "3194-39E8-5A96-7ECF", "192.168.1.20:5280", Now.AddHours(24));

        Assert.Equal(ResetCodeResult.Malformed, ResetCode.Check(repair, Tag(), Now, pub));
        Assert.Equal(ResetCodeResult.Malformed, ResetCode.Check("pas-un-code", Tag(), Now, pub));
    }

    /// <summary>
    /// A code made by the dashboard's Node signer (backend/services/repairCode.js, issueResetCode)
    /// for the tag A1B2-C3D4-E5F6-0718, 24 hours from 2026-10-12 10:00 UTC, with a throwaway key.
    /// If this fails, the two signers have drifted apart.
    /// </summary>
    [Fact]
    public void A_code_signed_by_the_dashboard_is_accepted()
    {
        const string nodePublicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQrZwQoC42J63hfWegmWlz413M6hTAQPjxV1z0qROra1janPyh5WB84WlbzhfEtiWA2hKqvILXjxzPkPsTe8ZtA==";
        const string nodeCode =
            "KKQ3FQ-6U4X3A-OGDKZY-ASB4LY-C2J7XD-VCH5UT-SGYBSP-TZNZPY-LOOU3X-LCMNT2-FQTPQX-LOSVBU-HUA6V3-57WABR-AVTX3R-2CLGUB-VIIOTJ-POPB2J-CIXNLM-ILMJON-GBEA";

        Assert.Equal(ResetCodeResult.Valid, ResetCode.Check(nodeCode, "A1B2-C3D4-E5F6-0718", Now, nodePublicKey));
        Assert.Equal(ResetCodeResult.Expired, ResetCode.Check(nodeCode, "A1B2-C3D4-E5F6-0718", Now.AddHours(25), nodePublicKey));
    }
}
