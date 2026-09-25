using Lonnii.Api.Services;

namespace Lonnii.Tests;

/// <summary>
/// Guards the rule that keeps EF away from the live PostgreSQL database. The damage from
/// getting this wrong is not a failed test run - it is migrations applied to the schema
/// paying customers are working on - so it is pinned rather than left to a comment.
/// </summary>
public class DatabaseOptionsTests
{
    [Fact]
    public void Defaults_to_sqlite_so_an_existing_host_laptop_is_unaffected()
    {
        var options = new DatabaseOptions();

        Assert.False(options.IsPostgres);
        Assert.True(options.AllowsAutomaticMigration);
        options.Validate();
    }

    [Fact]
    public void Postgres_never_migrates_automatically()
    {
        var options = new DatabaseOptions
        {
            Provider = DatabaseOptions.Postgres,
            ConnectionString = "Host=oci;Database=lonnii;Username=u;Password=p",
        };

        Assert.True(options.IsPostgres);
        Assert.False(options.AllowsAutomaticMigration);
    }

    [Theory]
    [InlineData("POSTGRES")]
    [InlineData("Postgres")]
    [InlineData("postgres")]
    public void Provider_name_is_case_insensitive(string provider)
    {
        var options = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = "Host=oci;Database=lonnii",
        };

        // A stray capital must not silently fall through to SQLite and then auto-migrate.
        Assert.True(options.IsPostgres);
        Assert.False(options.AllowsAutomaticMigration);
    }

    [Fact]
    public void Postgres_without_a_connection_string_is_refused_at_startup()
    {
        var options = new DatabaseOptions { Provider = DatabaseOptions.Postgres };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ConnectionString", error.Message);
    }

    [Fact]
    public void An_unknown_provider_is_refused_rather_than_assumed()
    {
        // Falling back to SQLite here would start a working-looking API on an empty local
        // file while the operator believes they are pointed at the server.
        var options = new DatabaseOptions { Provider = "mysql" };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("mysql", error.Message);
    }
}
