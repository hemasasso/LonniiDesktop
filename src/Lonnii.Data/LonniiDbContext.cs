using Lonnii.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lonnii.Data;

/// <summary>
/// The Lonnii database, on either provider: SQLite on a shop's host laptop in local mode,
/// PostgreSQL on the OCI server in online mode. One model serves both, which is what lets
/// the same build run in both deployments.
///
/// Money: SQLite has no decimal type. EF Core's default is to store decimal as TEXT,
/// which makes SQL-side ORDER BY and SUM either wrong or lossy. Every decimal here is
/// therefore converted to an integer number of minor units (value * 100) and stored as
/// INTEGER, which is exact and aggregates correctly in SQL. The consequence to respect:
/// do not write LINQ that multiplies two converted columns together in the database -
/// the result would be scaled twice. Multiply in C# and store the computed total, which
/// is what the schema does anyway (ventes_items.prix_total, ventes.montant_total).
/// </summary>
public class LonniiDbContext(DbContextOptions<LonniiDbContext> options) : DbContext(options)
{
    /// <summary>Scale used by the money converter: two decimal places, as DECIMAL(15,2) in the source schema.</summary>
    private const decimal MoneyScale = 100m;

    private static readonly ValueConverter<decimal, long> MoneyConverter =
        new(v => (long)Math.Round(v * MoneyScale, MidpointRounding.AwayFromZero),
            v => v / MoneyScale);

    private static readonly ValueConverter<decimal?, long?> NullableMoneyConverter =
        new(v => v == null ? null : (long)Math.Round(v.Value * MoneyScale, MidpointRounding.AwayFromZero),
            v => v == null ? null : v.Value / MoneyScale);

    // Billing (rows are written by the web dashboard; the desktop app only reads them)
    public DbSet<DashboardSubscription> DashboardSubscriptions => Set<DashboardSubscription>();

    // Licensing
    public DbSet<Device> Devices => Set<Device>();

    // Identity
    public DbSet<User> Users => Set<User>();
    public DbSet<Groupe> Groupes => Set<Groupe>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<GroupeSession> GroupeSessions => Set<GroupeSession>();
    public DbSet<PasswordHistory> PasswordHistories => Set<PasswordHistory>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    // Core privileges
    public DbSet<Privilege> Privileges => Set<Privilege>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserPrivilege> UserPrivileges => Set<UserPrivilege>();
    public DbSet<RolePrivilege> RolePrivileges => Set<RolePrivilege>();
    public DbSet<PrivilegeAudit> PrivilegeAudits => Set<PrivilegeAudit>();

    // Option privileges
    public DbSet<OptionPrivilege> OptionPrivileges => Set<OptionPrivilege>();
    public DbSet<OptionUserPrivilege> OptionUserPrivileges => Set<OptionUserPrivilege>();
    public DbSet<OptionPrivilegeAudit> OptionPrivilegeAudits => Set<OptionPrivilegeAudit>();

    // Gestion privileges
    public DbSet<GestionPrivilege> GestionPrivileges => Set<GestionPrivilege>();
    public DbSet<GestionUserRole> GestionUserRoles => Set<GestionUserRole>();
    public DbSet<GestionUserPrivilege> GestionUserPrivileges => Set<GestionUserPrivilege>();
    public DbSet<GestionPrivilegeAudit> GestionPrivilegeAudits => Set<GestionPrivilegeAudit>();

    // Stock
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockHistory> StockHistories => Set<StockHistory>();
    public DbSet<StockSetting> StockSettings => Set<StockSetting>();
    public DbSet<StockUserActivity> StockUserActivities => Set<StockUserActivity>();

    // Ventes
    public DbSet<Vente> Ventes => Set<Vente>();
    public DbSet<VenteItem> VenteItems => Set<VenteItem>();
    public DbSet<PaiementVente> PaiementsVentes => Set<PaiementVente>();
    public DbSet<Caisse> Caisses => Set<Caisse>();
    public DbSet<CaisseTransaction> CaisseTransactions => Set<CaisseTransaction>();
    public DbSet<VentesParametres> VentesParametres => Set<VentesParametres>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<VentesUserActivity> VentesUserActivities => Set<VentesUserActivity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ConfigureBilling(b);
        ConfigureIdentity(b);
        ConfigurePrivileges(b);
        ConfigureStock(b);
        ConfigureVentes(b);

        // SQLite only. PostgreSQL has a real decimal type, and Lonnii Business already
        // stores these columns as DECIMAL(15,2) - applying the minor-units converter there
        // would write 45 000 FCFA into a numeric column as 4 500 000, silently multiplying
        // every amount in the database by a hundred.
        if (Database.IsSqlite())
            ApplyMoneyConverter(b);

        // Run last: index filters written above already use snake_case column names.
        ApplySnakeCaseNames(b);
    }

    /// <summary>
    /// Table and column names Lonnii Business uses where they differ from the CLR names.
    /// Everything not listed here is derived by <see cref="ToSnakeCase"/>.
    /// </summary>
    private static readonly Dictionary<Type, string> TableNames = new()
    {
        [typeof(DashboardSubscription)] = "dashboard_subscriptions",
        [typeof(Device)] = "devices",
        [typeof(User)] = "users",
        [typeof(Groupe)] = "groupes",
        [typeof(GroupMember)] = "groupe_membres",
        [typeof(GroupeSession)] = "groupe_sessions",
        [typeof(PasswordHistory)] = "password_history",
        [typeof(UserSession)] = "user_sessions",
        [typeof(Privilege)] = "privileges",
        [typeof(UserRole)] = "user_roles",
        [typeof(UserPrivilege)] = "user_privileges",
        [typeof(RolePrivilege)] = "role_privileges",
        [typeof(PrivilegeAudit)] = "privilege_audit",
        [typeof(OptionPrivilege)] = "option_privileges",
        [typeof(OptionUserPrivilege)] = "option_user_privileges",
        [typeof(OptionPrivilegeAudit)] = "option_privilege_audit",
        [typeof(GestionPrivilege)] = "gestion_privileges",
        [typeof(GestionUserRole)] = "gestion_user_roles",
        [typeof(GestionUserPrivilege)] = "gestion_user_privileges",
        [typeof(GestionPrivilegeAudit)] = "gestion_privilege_audit",
        [typeof(Category)] = "categories",
        [typeof(Supplier)] = "suppliers",
        [typeof(Product)] = "products",
        [typeof(StockHistory)] = "stock_history",
        [typeof(StockSetting)] = "stock_settings",
        [typeof(StockUserActivity)] = "stock_user_activity",
        [typeof(Vente)] = "ventes",
        [typeof(VenteItem)] = "ventes_items",
        [typeof(PaiementVente)] = "paiements_ventes",
        [typeof(Caisse)] = "caisses",
        [typeof(CaisseTransaction)] = "caisse_transactions",
        [typeof(VentesParametres)] = "ventes_parametres",
        [typeof(Client)] = "clients",
        [typeof(VentesUserActivity)] = "ventes_user_activity",
    };

    /// <summary>Columns whose Lonnii Business name is not the snake_case of the property name.</summary>
    private static readonly Dictionary<(Type, string), string> ColumnNames = new()
    {
        [(typeof(User), nameof(User.IdUser))] = "iduser",
        [(typeof(Groupe), nameof(Groupe.IdUserAdmin))] = "iduser_admin",
        // Live column is prestations_access; the property keeps the clearer name.
        [(typeof(Groupe), nameof(Groupe.PrestationsEnabled))] = "prestations_access",

        // --- ventes -----------------------------------------------------------------
        // The live table is the older gestion_stock_schema.sql shape with English column
        // names, not the French setup_ventes_tables.sql the entity was modelled from -
        // that file was never applied to production. Lonnii Business hides the difference
        // by aliasing in SQL ("v.sale_number as numero_vente"), which is why reading its
        // routes did not reveal it. Verified against the live schema 2026-09-24.
        //
        // The names are mapped rather than the properties renamed, so the French domain
        // vocabulary the rest of the desktop code uses survives, and so the SQLite file
        // and the server hold identically named columns - which is what makes an import
        // or a sync a straight copy.
        [(typeof(Vente), nameof(Vente.NumeroVente))] = "sale_number",
        [(typeof(Vente), nameof(Vente.DateVente))] = "date",
        [(typeof(Vente), nameof(Vente.ClientNom))] = "customer_name",
        [(typeof(Vente), nameof(Vente.ClientTelephone))] = "customer_phone",
        [(typeof(Vente), nameof(Vente.ClientEmail))] = "customer_email",
        [(typeof(Vente), nameof(Vente.MontantTotal))] = "total_amount",
        [(typeof(Vente), nameof(Vente.StatutPaiement))] = "payment_status",
        [(typeof(Vente), nameof(Vente.ModePaiement))] = "payment_method",
        [(typeof(Vente), nameof(Vente.CreatedBy))] = "user_id",
        [(typeof(GroupMember), nameof(GroupMember.IdGroupe))] = "idgroupe",
        [(typeof(GroupMember), nameof(GroupMember.IdUser))] = "iduser",
        [(typeof(PasswordHistory), nameof(PasswordHistory.IdUser))] = "iduser",
    };

    private static void ApplySnakeCaseNames(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            var clr = entity.ClrType;
            entity.SetTableName(TableNames.TryGetValue(clr, out var table)
                ? table
                : ToSnakeCase(clr.Name));

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ColumnNames.TryGetValue((clr, property.Name), out var column)
                    ? column
                    : ToSnakeCase(property.Name));
            }
        }
    }

    /// <summary>Turns <c>MontantTotal</c> into <c>montant_total</c>.</summary>
    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(name[i - 1]) ||
                              (i + 1 < name.Length && !char.IsUpper(name[i + 1]))))
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static void ConfigureBilling(ModelBuilder b)
    {
        b.Entity<DashboardSubscription>(e =>
        {
            e.HasKey(x => x.Id);
            // Finding a group's current contract is the only read path the desktop app has.
            e.HasIndex(x => new { x.GroupId, x.ContractEndDate });
            e.HasIndex(x => x.Statut);
            // Deliberately no foreign key to groupes: in Lonnii Business these rows are
            // written by the admin dashboard against a separate database, and a subscription
            // is kept as billing history after a group is deleted.
        });

        b.Entity<Device>(e =>
        {
            e.HasKey(x => x.Id);
            // One row per machine per shop. A machine coming back reuses its row instead of
            // adding a second that would eat another slot.
            e.HasIndex(x => new { x.GroupId, x.DeviceId }).IsUnique();
            e.HasIndex(x => x.GroupId);
            e.Property(x => x.DeviceId).IsRequired();
            e.HasOne(x => x.Groupe).WithMany()
                .HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureIdentity(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.IdUser);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Email).IsRequired();
        });

        b.Entity<Groupe>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.IdUserAdmin);
            e.HasOne(x => x.Admin)
                .WithMany()
                .HasForeignKey(x => x.IdUserAdmin)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GroupMember>(e =>
        {
            e.HasKey(x => new { x.IdGroupe, x.IdUser });
            e.HasIndex(x => x.IdUser);
            e.HasOne(x => x.Groupe).WithMany(g => g.Members)
                .HasForeignKey(x => x.IdGroupe).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Memberships)
                .HasForeignKey(x => x.IdUser).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GroupeSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SessionToken).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<PasswordHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.IdUser);
        });

        b.Entity<UserSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
        });
    }

    private static void ConfigurePrivileges(ModelBuilder b)
    {
        b.Entity<Privilege>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Category);
        });

        b.Entity<UserRole>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.GroupId }).IsUnique();
        });

        b.Entity<UserPrivilege>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.GroupId, x.PrivilegeId }).IsUnique();
            e.HasOne(x => x.Privilege).WithMany()
                .HasForeignKey(x => x.PrivilegeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RolePrivilege>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Role, x.PrivilegeId }).IsUnique();
            e.HasOne(x => x.Privilege).WithMany()
                .HasForeignKey(x => x.PrivilegeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PrivilegeAudit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.CreatedAt });
        });

        b.Entity<OptionPrivilege>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Module);
        });

        b.Entity<OptionUserPrivilege>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.GroupId, x.PrivilegeId }).IsUnique();
            e.HasOne(x => x.Privilege).WithMany()
                .HasForeignKey(x => x.PrivilegeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OptionPrivilegeAudit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.CreatedAt });
        });

        b.Entity<GestionPrivilege>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Module);
        });

        b.Entity<GestionUserRole>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.GroupId, x.Module }).IsUnique();
        });

        b.Entity<GestionUserPrivilege>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.GroupId, x.PrivilegeId }).IsUnique();
            e.HasOne(x => x.Privilege).WithMany()
                .HasForeignKey(x => x.PrivilegeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GestionPrivilegeAudit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.CreatedAt });
            e.HasIndex(x => x.Module);
        });
    }

    private static void ConfigureStock(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.HasKey(x => x.Id);
            // Unique per group rather than globally: each company names its own categories.
            e.HasIndex(x => new { x.GroupId, x.Name }).IsUnique();
            e.HasOne(x => x.Parent).WithMany()
                .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Supplier>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.GroupId);
        });

        b.Entity<Product>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.GroupId);
            e.HasIndex(x => new { x.GroupId, x.Sku }).IsUnique().HasFilter("sku IS NOT NULL");
            e.HasIndex(x => new { x.GroupId, x.Barcode }).IsUnique().HasFilter("barcode IS NOT NULL");
            e.HasOne(x => x.Category).WithMany()
                .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Supplier).WithMany()
                .HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<StockHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.CreatedAt });
            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StockSetting>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.SettingName }).IsUnique();
        });

        b.Entity<StockUserActivity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.CreatedAt });
        });
    }

    private static void ConfigureVentes(ModelBuilder b)
    {
        b.Entity<Vente>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.NumeroVente }).IsUnique();
            e.HasIndex(x => new { x.GroupId, x.DateVente });
            e.HasIndex(x => x.StatutPaiement);
            // Makes a retried sale from a till collapse onto the original row.
            e.HasIndex(x => new { x.GroupId, x.IdempotencyKey })
                .IsUnique().HasFilter("idempotency_key IS NOT NULL");
        });

        b.Entity<VenteItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.VenteId);
            e.HasIndex(x => x.ProductId);
            e.HasOne(x => x.Vente).WithMany(v => v.Items)
                .HasForeignKey(x => x.VenteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PaiementVente>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.VenteId);
            e.HasOne(x => x.Vente).WithMany(v => v.Paiements)
                .HasForeignKey(x => x.VenteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Caisse>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.GroupId);
            e.HasIndex(x => x.Status);
            // One open register per user per group, as in the source schema.
            e.HasIndex(x => new { x.GroupId, x.UserId })
                .IsUnique().HasFilter("status = 'open'");
        });

        b.Entity<CaisseTransaction>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CaisseId);
            e.HasIndex(x => new { x.GroupId, x.CreatedAt });
            e.HasOne(x => x.Caisse).WithMany()
                .HasForeignKey(x => x.CaisseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<VentesParametres>(e => e.HasKey(x => x.GroupeId));

        b.Entity<Client>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.GroupId);
        });

        b.Entity<VentesUserActivity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.CreatedAt });
        });
    }

    /// <summary>
    /// Applies the minor-units money converter to every decimal property in the model,
    /// so a new entity cannot accidentally fall back to EF's lossy TEXT storage.
    /// </summary>
    private static void ApplyMoneyConverter(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(decimal))
                    property.SetValueConverter(MoneyConverter);
                else if (property.ClrType == typeof(decimal?))
                    property.SetValueConverter(NullableMoneyConverter);
            }
        }
    }
}
