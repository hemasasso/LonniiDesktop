using Lonnii.Shared.Navigation;
using Lonnii.Shared.Security;

namespace Lonnii.Tests;

/// <summary>
/// Guards the ported catalogue itself: a duplicate or a typo here would silently change
/// who can do what, because every gate in the app is keyed on these names.
/// </summary>
public class PrivilegeCatalogTests
{
    [Fact]
    public void GestionCatalog_HasNoDuplicateNames()
    {
        var duplicates = PrivilegeCatalog.Gestion
            .GroupBy(p => p.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void OptionCatalog_HasNoDuplicateNames()
    {
        var duplicates = PrivilegeCatalog.Option
            .GroupBy(p => p.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void CoreCatalog_HasNoDuplicateNames()
    {
        var duplicates = PrivilegeCatalog.Core
            .GroupBy(p => p.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void DeduplicationKeepsTheFirstDefinition_AsOnConflictDoNothingWould()
    {
        // can_print_receipt is seeded twice: first under 'sales' by setup_gestion_privileges.sql,
        // then under 'ventes'. PostgreSQL keeps the first, so the port must too.
        var printReceipt = PrivilegeCatalog.Gestion.Single(p => p.Name == Priv.Gestion.PrintReceipt);
        Assert.Equal(GestionModules.Sales, printReceipt.Module);

        var addPayment = PrivilegeCatalog.Gestion.Single(p => p.Name == Priv.Gestion.AddPayment);
        Assert.Equal(GestionModules.Sales, addPayment.Module);

        var cancelVente = PrivilegeCatalog.Gestion.Single(p => p.Name == Priv.Gestion.CancelVente);
        Assert.Equal(GestionModules.Sales, cancelVente.Module);
    }

    [Fact]
    public void AdminOnlyFlags_MatchTheSourceSchema()
    {
        Assert.True(Admin(Priv.Gestion.DeleteProducts));
        Assert.True(Admin(Priv.Gestion.DeleteSales));
        Assert.True(Admin(Priv.Gestion.DeleteVente));
        Assert.True(Admin(Priv.Gestion.ResolveCaisseEcart));
        Assert.True(Admin(Priv.Gestion.ApproveCharges));
        Assert.True(Admin(Priv.Gestion.ManageGestionSettings));
        Assert.True(Admin(Priv.Gestion.BackupData));
        Assert.True(Admin(Priv.Gestion.ViewSystemLogs));

        Assert.False(Admin(Priv.Gestion.ViewStock));
        Assert.False(Admin(Priv.Gestion.ViewVentes));
        Assert.False(Admin(Priv.Gestion.AddProducts));

        // can_delete_charges is NOT admin-only in create_charges_system.sql, unlike its
        // siblings elsewhere. Keeping that asymmetry is deliberate.
        Assert.False(Admin(Priv.Gestion.DeleteCharges));

        static bool Admin(string name) =>
            PrivilegeCatalog.Gestion.Single(p => p.Name == name).IsAdminOnly;
    }

    [Fact]
    public void EveryMenuEntryRequiresAPrivilegeThatExists()
    {
        var known = PrivilegeCatalog.Gestion.Select(p => p.Name)
            .Concat(PrivilegeCatalog.Option.Select(p => p.Name))
            .Concat([Priv.Gestion.ViewAudit, Priv.Gestion.ViewParametres])
            .ToHashSet(StringComparer.Ordinal);

        var entries = AppMenu.Espace.Concat(AppMenu.Gestion);

        foreach (var entry in entries)
        {
            if (entry.RequiredPrivilege is null) continue;
            Assert.True(known.Contains(entry.RequiredPrivilege),
                $"Menu entry '{entry.Key}' requires unknown privilege '{entry.RequiredPrivilege}'");
        }
    }

    [Fact]
    public void EveryGestionSectionKeyMatchesARealMenuEntry()
    {
        var keys = AppMenu.Gestion.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var section in AppMenu.GestionSections)
        {
            foreach (var key in section.Keys)
                Assert.True(keys.Contains(key), $"Section '{section.Id}' references unknown entry '{key}'");
        }
    }

    [Fact]
    public void EveryGestionMenuEntryBelongsToExactlyOneSection()
    {
        // A missing entry would vanish from the admin view, which groups by section.
        foreach (var entry in AppMenu.Gestion)
        {
            var sections = AppMenu.GestionSections.Count(s => s.Keys.Contains(entry.Key));
            Assert.True(sections == 1, $"Entry '{entry.Key}' appears in {sections} sections, expected 1");
        }
    }

    [Fact]
    public void AdminRoleMapping_MatchesTheSourceSchema()
    {
        Assert.True(GroupRoles.IsAdminRole(GroupRoles.Admin));
        Assert.True(GroupRoles.IsAdminRole(GroupRoles.SubAdmin));
        Assert.False(GroupRoles.IsAdminRole(GroupRoles.Moderator));
        Assert.False(GroupRoles.IsAdminRole(GroupRoles.Member));
        Assert.False(GroupRoles.IsAdminRole(null));
    }

    [Fact]
    public void AdminRole_CarriesEveryCorePrivilege()
    {
        var adminPrivileges = PrivilegeCatalog.RolePrivileges[GroupRoles.Admin];
        Assert.Equal(PrivilegeCatalog.Core.Count, adminPrivileges.Count);
    }

    [Fact]
    public void RoleMappings_OnlyReferenceKnownCorePrivileges()
    {
        var known = PrivilegeCatalog.Core.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var (role, names) in PrivilegeCatalog.RolePrivileges)
        {
            foreach (var name in names)
                Assert.True(known.Contains(name), $"Role '{role}' references unknown privilege '{name}'");
        }
    }
}
