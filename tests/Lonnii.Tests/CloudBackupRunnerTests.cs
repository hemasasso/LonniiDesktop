using Lonnii.Api.Features.Backup;
using System.IO.Compression;
using System.Net.Http.Json;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lonnii.Tests;

/// <summary>
/// The host's side end to end: it builds a snapshot, sends the photos the server lacks, then
/// the snapshot. One in-process app plays both the shop's host and the licence server, with
/// the backup HTTP client wired straight to it, so the real protocol runs without a network.
/// </summary>
public class CloudBackupRunnerTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _dataDirectory = null!;

    private const string AdminEmail = "admin@pharmacie-nord.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string Device = "machine-1";
    private string _adminId = null!;
    private const string PhotoUrl = "/api/images/categories/cat1_1.jpg";

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
                services.AddHttpClient(CloudBackupClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _factory.Server.CreateHandler()));
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var admin = new User { Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword), IsVerified = true };
        db.Users.Add(admin);
        _adminId = admin.IdUser;
        db.Groupes.Add(new Groupe
        {
            Id = GroupId, Nom = "Pharmacie Nord", IdUserAdmin = admin.IdUser,
            Mode = DeploymentModes.Online, MaxDevices = 3, LicenceServerUrl = "http://localhost",
        });
        db.DashboardSubscriptions.Add(new DashboardSubscription
        {
            GroupId = GroupId, GroupName = "Pharmacie Nord", AdminName = "Sasso", Montant = 60_000m,
            Statut = StatutAbonnement.Active,
            ContractStartDate = DateTime.UtcNow.AddMonths(-2), ContractEndDate = DateTime.UtcNow.AddMonths(10),
        });
        db.Categories.Add(new Category { GroupId = GroupId, Name = "Antalgiques", ImageUrl = PhotoUrl });
        await db.SaveChangesAsync();

        // The photo as ImageStorageService would have left it.
        var photoPath = Path.Combine(_dataDirectory, "images", "categories", "cat1_1.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(photoPath)!);
        await File.WriteAllBytesAsync(photoPath, [9, 8, 7, 6]);

        // Activation gives the host its token, exactly as it does for a real installation.
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/activation",
            new ActivationRequest(GroupId, AdminEmail, AdminPassword, Device, "poste"));
        var token = (await response.Content.ReadFromJsonAsync<ActivationResponse>())!.BackupToken;
        LicenceGuard.StoreBackupToken(db, GroupId, Device, token);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private async Task<CloudBackupOutcome> RunAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CloudBackupRunner>().RunAsync(GroupId, CancellationToken.None);
    }

    private BackupStore Store => _factory.Services.GetRequiredService<BackupStore>();

    [Fact]
    public async Task A_run_uploads_the_snapshot_and_the_photo_it_refers_to()
    {
        var outcome = await RunAsync();

        Assert.True(outcome.Succeeded, outcome.Message);

        var info = Store.Info(GroupId);
        Assert.True(info.Exists);
        Assert.True(info.RecordCount > 0);
        Assert.Equal(1, info.ImageCount);

        using var photo = Store.OpenImage(GroupId, "categories", "cat1_1.jpg");
        Assert.NotNull(photo);
        var bytes = new byte[4];
        photo!.ReadExactly(bytes);
        Assert.Equal([9, 8, 7, 6], bytes);

        // And the stored snapshot really is the shop's data, rows keyed as they were.
        var restored = Path.Combine(_dataDirectory, "restored.db");
        await using (var gz = new GZipStream(Store.OpenLatestSnapshot(GroupId)!, CompressionMode.Decompress))
        await using (var file = File.Create(restored))
            await gz.CopyToAsync(file);

        var options = new DbContextOptionsBuilder<LonniiDbContext>().UseSqlite($"Data Source={restored}").Options;
        await using var archive = new LonniiDbContext(options);
        var category = await archive.Categories.SingleAsync();
        Assert.Equal("Antalgiques", category.Name);
        Assert.Equal(PhotoUrl, category.ImageUrl);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_second_run_succeeds_and_the_state_remembers_the_epoch()
    {
        Assert.True((await RunAsync()).Succeeded);
        Assert.True((await RunAsync()).Succeeded);

        using var scope = _factory.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<LonniiDbContext>()
            .CloudBackupStates.AsNoTracking().SingleAsync();

        Assert.Equal(Store.CurrentEpoch(GroupId), state.Epoch);
        Assert.NotNull(state.LastSuccessAt);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task A_host_that_lost_its_state_is_refused_and_says_why()
    {
        Assert.True((await RunAsync()).Succeeded);

        // The deleted-database scenario: same workspace, but no memory of the epoch.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.CloudBackupStates.Single().Epoch = null;
            await db.SaveChangesAsync();
        }

        var outcome = await RunAsync();

        Assert.False(outcome.Succeeded);
        Assert.Contains("Restaurez", outcome.Message);
        Assert.True(Store.Info(GroupId).Exists);
    }

    [Fact]
    public async Task A_local_mode_workspace_is_not_backed_up()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Groupes.Single().Mode = DeploymentModes.Local;
            await db.SaveChangesAsync();
        }

        var outcome = await RunAsync();

        Assert.False(outcome.Ran);
        Assert.False(Store.Info(GroupId).Exists);
    }

    /// <summary>
    /// The scenario the whole feature exists for. The host is lost, setup runs again and gives
    /// the admin a brand-new id, and the restore must put back the data, the photos, the staff
    /// (with their roles and privileges) and the admin's original id.
    /// </summary>
    [Fact]
    public async Task A_lost_host_is_restored_with_its_data_photos_and_accounts()
    {
        string cashierId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            var cashier = new User { Email = "caissier@pharmacie-nord.bf", Password = BCrypt.Net.BCrypt.HashPassword("x"), IsVerified = true };
            cashierId = cashier.IdUser;
            db.Users.Add(cashier);
            db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = cashier.IdUser });
            db.UserRoles.Add(new UserRole { UserId = cashier.IdUser, GroupId = GroupId, Role = GroupRoles.Member, AssignedBy = _adminId });
            var privilege = await db.Privileges.OrderBy(p => p.Id).LastAsync();
            db.UserPrivileges.Add(new UserPrivilege { UserId = cashier.IdUser, GroupId = GroupId, PrivilegeId = privilege.Id });
            await db.SaveChangesAsync();
        }

        Assert.True((await RunAsync()).Succeeded);

        // Disaster, then a fresh setup: no data, no photo, no staff, and an admin with a new id.
        const string newAdminId = "11111111-2222-3333-4444-555555555555";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            await db.Categories.ExecuteDeleteAsync();
            await db.UserPrivileges.ExecuteDeleteAsync();
            await db.UserRoles.Where(r => r.UserId == cashierId).ExecuteDeleteAsync();
            await db.GroupMembers.Where(m => m.IdUser == cashierId).ExecuteDeleteAsync();
            await db.Users.Where(u => u.IdUser == cashierId).ExecuteDeleteAsync();

            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;");
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET iduser = {0} WHERE iduser = {1};", newAdminId, _adminId);
            await db.Database.ExecuteSqlRawAsync("UPDATE groupes SET iduser_admin = {0} WHERE iduser_admin = {1};", newAdminId, _adminId);
            await tx.CommitAsync();

            db.CloudBackupStates.Single().Epoch = null;
            await db.SaveChangesAsync();
        }
        File.Delete(Path.Combine(_dataDirectory, "images", "categories", "cat1_1.jpg"));

        CloudRestoreOutcome outcome;
        using (var scope = _factory.Services.CreateScope())
            outcome = await scope.ServiceProvider.GetRequiredService<CloudRestoreService>()
                .RestoreAsync(GroupId, CancellationToken.None);

        Assert.True(outcome.Result is not null, outcome.Error);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

            var category = await db.Categories.SingleAsync();
            Assert.Equal(PhotoUrl, category.ImageUrl);
            Assert.True(File.Exists(Path.Combine(_dataDirectory, "images", "categories", "cat1_1.jpg")));

            Assert.Equal(_adminId, (await db.Groupes.SingleAsync()).IdUserAdmin);
            Assert.Contains(await db.Users.ToListAsync(), u => u.IdUser == _adminId && u.Email == AdminEmail);
            Assert.DoesNotContain(await db.Users.ToListAsync(), u => u.IdUser == newAdminId);

            Assert.Contains(await db.Users.ToListAsync(), u => u.IdUser == cashierId);
            Assert.Single(await db.UserRoles.Where(r => r.UserId == cashierId).ToListAsync());
            Assert.Single(await db.UserPrivileges.Where(p => p.UserId == cashierId).ToListAsync());

            Assert.Equal(Store.CurrentEpoch(GroupId), (await db.CloudBackupStates.SingleAsync()).Epoch);
        }

        // Backups carry on from the restored line.
        Assert.True((await RunAsync()).Succeeded);
    }

    /// <summary>A restore is never allowed to write over a shop that has started trading again.</summary>
    [Fact]
    public async Task Restore_refuses_a_workspace_that_already_has_data()
    {
        Assert.True((await RunAsync()).Succeeded);

        CloudRestoreOutcome outcome;
        using (var scope = _factory.Services.CreateScope())
            outcome = await scope.ServiceProvider.GetRequiredService<CloudRestoreService>()
                .RestoreAsync(GroupId, CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Equal(409, outcome.StatusCode);
    }
}
