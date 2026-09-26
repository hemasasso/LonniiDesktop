using Lonnii.Shared.Navigation;
using Lonnii.Shared.Security;

namespace Lonnii.Tests;

/// <summary>
/// Covers who sees which menu entries, and the French labels shown for stored roles.
/// </summary>
public class MenuVisibilityTests
{
    /// <summary>A privilege map where everything is granted, for testing non-privilege gates.</summary>
    private static Dictionary<string, bool> AllGranted() =>
        PrivilegeCatalog.Gestion.Select(p => p.Name)
            .Concat(PrivilegeCatalog.Option.Select(p => p.Name))
            .Concat([Priv.Gestion.ViewAudit, Priv.Gestion.ViewParametres])
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(n => n, _ => true, StringComparer.Ordinal);

    [Fact]
    public void Member_DoesNotSeeOptions()
    {
        var visible = AppMenu.Visible(
            AppMenu.Espace, AllGranted(),
            gestionAccess: true, prestationsEnabled: true, isAdmin: false);

        Assert.DoesNotContain(visible, e => e.Key == "options");
    }

    [Fact]
    public void Admin_SeesOptions()
    {
        var visible = AppMenu.Visible(
            AppMenu.Espace, AllGranted(),
            gestionAccess: true, prestationsEnabled: true, isAdmin: true);

        Assert.Contains(visible, e => e.Key == "options");
    }

    [Fact]
    public void Member_StillSeesTheOtherEspaceEntries()
    {
        var visible = AppMenu.Visible(
            AppMenu.Espace, AllGranted(),
            gestionAccess: true, prestationsEnabled: true, isAdmin: false);

        // Hiding Options must not take the rest of the workspace with it.
        Assert.Contains(visible, e => e.Key == "program");
        Assert.Contains(visible, e => e.Key == "prestations");
    }

    [Fact]
    public void EveryGestionEntry_BelongsToExactlyOneSection()
    {
        // The shell navigates Gestion only through its section pills, so a key in no
        // section - or in two - is either unreachable or listed twice.
        foreach (var entry in AppMenu.Gestion)
        {
            var sections = AppMenu.GestionSections.Count(s => s.Keys.Contains(entry.Key));
            Assert.True(sections == 1, $"'{entry.Key}' belongs to {sections} sections, expected 1.");
        }
    }

    [Fact]
    public void AdminFlag_DoesNotOverrideAMissingPrivilege()
    {
        // Being an admin opens the Options door; it does not grant Gestion privileges.
        var noPrivileges = new Dictionary<string, bool>(StringComparer.Ordinal);

        var visible = AppMenu.Visible(
            AppMenu.Gestion, noPrivileges,
            gestionAccess: true, prestationsEnabled: true, isAdmin: true);

        Assert.DoesNotContain(visible, e => e.Key == "gestion-de-stock");
    }

    // --- Role labels ---

    [Theory]
    [InlineData(GroupRoles.Member, "Membre")]
    [InlineData(GroupRoles.Moderator, "Modérateur")]
    [InlineData(GroupRoles.SubAdmin, "Administrateur délégué")]
    [InlineData(GroupRoles.Admin, "Administrateur")]
    public void StoredRoles_AreShownInFrench(string stored, string expected)
    {
        Assert.Equal(expected, GroupRoles.DisplayName(stored));
    }

    [Fact]
    public void TheStoredValues_StayInEnglish()
    {
        // The labels are display-only. Changing these strings would break the schema
        // compatibility that the future Lonnii Business importer depends on.
        Assert.Equal("member", GroupRoles.Member);
        Assert.Equal("sub_admin", GroupRoles.SubAdmin);
        Assert.Equal("moderator", GroupRoles.Moderator);
        Assert.Equal("admin", GroupRoles.Admin);
    }

    [Fact]
    public void TheGroupCreator_IsLabelledAdministrateurGeneral()
    {
        Assert.Equal("Administrateur Général",
            GroupRoles.DisplayName(GroupRoles.Member, isAdminGeneral: true));
    }

    [Fact]
    public void AnEmptyRole_ProducesAnEmptyLabel_RatherThanTheWordMembre()
    {
        Assert.Equal(string.Empty, GroupRoles.DisplayName(null));
        Assert.Equal(string.Empty, GroupRoles.DisplayName(""));
    }

    [Fact]
    public void AnUnknownRole_IsShownAsItself_RatherThanSilentlyBecomingMembre()
    {
        Assert.Equal("quelque_chose", GroupRoles.DisplayName("quelque_chose"));
    }
}
