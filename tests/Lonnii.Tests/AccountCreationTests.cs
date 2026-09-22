using Lonnii.Api.Endpoints;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Tests;

/// <summary>
/// Covers how accounts come into being. On a local network this is the part a shop owner
/// touches on day one, and the part that decides whether a stranger on the office Wi-Fi
/// can mint themselves an account.
/// </summary>
public class AccountCreationTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private LonniiDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<LonniiDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new LonniiDbContext(options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task FirstAccount_IsCreatedWithAHashedPassword()
    {
        var (user, error) = await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("Patron@Lonnii.Test", "MotDePasse123", "patron"), _db, default);

        Assert.Null(error);
        Assert.NotNull(user);
        await _db.SaveChangesAsync();

        var stored = await _db.Users.SingleAsync();

        // The email is normalised so sign-in is not case sensitive.
        Assert.Equal("patron@lonnii.test", stored.Email);

        // The password is never stored as typed.
        Assert.NotNull(stored.Password);
        Assert.NotEqual("MotDePasse123", stored.Password);
        Assert.True(BCrypt.Net.BCrypt.Verify("MotDePasse123", stored.Password));

        // Usable straight away: there is no email to verify against on a local network.
        Assert.True(stored.IsVerified);
    }

    [Fact]
    public async Task NewAccount_IsRecordedInPasswordHistory()
    {
        var (user, _) = await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("patron@lonnii.test", "MotDePasse123"), _db, default);
        await _db.SaveChangesAsync();

        var history = await _db.PasswordHistories.SingleAsync();
        Assert.Equal(user!.IdUser, history.IdUser);
        Assert.Equal(user.Password, history.PasswordHash);
    }

    [Theory]
    [InlineData("pasdemail", "MotDePasse123")]
    [InlineData("", "MotDePasse123")]
    [InlineData("   ", "MotDePasse123")]
    public async Task InvalidEmail_IsRejected(string email, string password)
    {
        var (user, error) = await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest(email, password), _db, default);

        Assert.Null(user);
        Assert.IsType<BadRequest<ApiError>>(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("court")]
    [InlineData("1234567")]
    public async Task ShortPassword_IsRejected(string password)
    {
        var (user, error) = await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("patron@lonnii.test", password), _db, default);

        Assert.Null(user);
        Assert.IsType<BadRequest<ApiError>>(error);
    }

    [Fact]
    public async Task PasswordOfExactlyTheMinimumLength_IsAccepted()
    {
        var password = new string('a', AuthEndpoints.MinimumPasswordLength);

        var (user, error) = await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("patron@lonnii.test", password), _db, default);

        Assert.Null(error);
        Assert.NotNull(user);
    }

    [Fact]
    public async Task DuplicateEmail_IsRejected_RegardlessOfCase()
    {
        await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("patron@lonnii.test", "MotDePasse123"), _db, default);
        await _db.SaveChangesAsync();

        var (user, error) = await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("PATRON@LONNII.TEST", "AutreMotDePasse123"), _db, default);

        Assert.Null(user);
        Assert.IsType<Conflict<ApiError>>(error);
    }

    [Fact]
    public async Task DuplicateUsername_IsRejected()
    {
        await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"), _db, default);
        await _db.SaveChangesAsync();

        var (user, error) = await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("autre@lonnii.test", "MotDePasse123", "patron"), _db, default);

        Assert.Null(user);
        Assert.IsType<Conflict<ApiError>>(error);
    }

    [Fact]
    public async Task BlankUsername_IsStoredAsNull_SoSeveralAccountsCanOmitIt()
    {
        await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("un@lonnii.test", "MotDePasse123", "   "), _db, default);
        await _db.SaveChangesAsync();

        var (user, error) = await AuthEndpoints.CreateAccountAsync(
            new RegisterRequest("deux@lonnii.test", "MotDePasse123", null), _db, default);

        Assert.Null(error);
        Assert.NotNull(user);
        await _db.SaveChangesAsync();

        Assert.Equal(2, await _db.Users.CountAsync());
        Assert.Equal(2, await _db.Users.CountAsync(u => u.Username == null));
    }
}
