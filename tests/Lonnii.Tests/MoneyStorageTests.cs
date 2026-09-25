using Lonnii.Data;
using Lonnii.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Tests;

/// <summary>
/// Pins how money is stored on each provider.
///
/// SQLite has no decimal type, so amounts are converted to integer minor units. PostgreSQL
/// does, and Lonnii Business already stores these columns as DECIMAL(15,2) - so carrying
/// the converter over would write 45 000 FCFA as 4 500 000 into the live database and
/// multiply every amount by a hundred. Nothing would throw; the numbers would simply be
/// wrong, which is why this is tested rather than trusted.
/// </summary>
public class MoneyStorageTests
{
    private static LonniiDbContext SqliteContext() =>
        new(new DbContextOptionsBuilder<LonniiDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);

    private static LonniiDbContext PostgresContext() =>
        new(new DbContextOptionsBuilder<LonniiDbContext>()
            .UseNpgsql("Host=localhost;Database=lonnii;Username=u;Password=p")
            .Options);

    /// <summary>The CLR type a property is actually stored as, after any value converter.</summary>
    private static Type StoredTypeOf<TEntity>(LonniiDbContext db, string propertyName)
    {
        var property = db.Model.FindEntityType(typeof(TEntity))!.FindProperty(propertyName)!;
        return property.GetValueConverter()?.ProviderClrType ?? property.ClrType;
    }

    [Fact]
    public void Sqlite_stores_amounts_as_integer_minor_units()
    {
        using var db = SqliteContext();

        Assert.Equal(typeof(long), StoredTypeOf<Vente>(db, nameof(Vente.MontantTotal)));
    }

    [Fact]
    public void Postgres_stores_amounts_as_decimal()
    {
        using var db = PostgresContext();

        Assert.Equal(typeof(decimal), StoredTypeOf<Vente>(db, nameof(Vente.MontantTotal)));
    }

    [Fact]
    public void Nullable_amounts_follow_the_same_rule()
    {
        using var sqlite = SqliteContext();
        using var postgres = PostgresContext();

        Assert.Equal(typeof(long?), StoredTypeOf<Product>(sqlite, nameof(Product.CostPrice)));
        Assert.Equal(typeof(decimal?), StoredTypeOf<Product>(postgres, nameof(Product.CostPrice)));
    }

    /// <summary>
    /// The converter is applied by sweeping every decimal property, so a newly added entity
    /// is covered automatically. This checks the sweep still reaches beyond the ventes
    /// tables it was written for.
    /// </summary>
    [Fact]
    public void The_rule_covers_every_money_column_not_just_ventes()
    {
        using var sqlite = SqliteContext();
        using var postgres = PostgresContext();

        Assert.Equal(typeof(long), StoredTypeOf<DashboardSubscription>(sqlite, nameof(DashboardSubscription.Montant)));
        Assert.Equal(typeof(decimal), StoredTypeOf<DashboardSubscription>(postgres, nameof(DashboardSubscription.Montant)));
    }
}
