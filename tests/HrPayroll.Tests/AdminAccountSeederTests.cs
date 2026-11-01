using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HrPayroll.Tests;

public class AdminAccountSeederTests
{
    private const string Email = "admin@hr.local";
    private const string Password = "Admin123+";
    private const string FullName = "مدير النظام";

    private static IConfiguration Config(string email = Email, string password = Password, string fullName = FullName) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminAccount:Email"] = email,
                ["AdminAccount:Password"] = password,
                ["AdminAccount:FullName"] = fullName
            })
            .Build();

    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        public Harness()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            services.AddIdentityCore<ApplicationUser>(o =>
                {
                    o.Password.RequiredLength = 8;
                    o.Password.RequireNonAlphanumeric = false;
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<AppDbContext>()
                .AddDefaultTokenProviders();
            _provider = services.BuildServiceProvider();
            var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
            _scope = scope;
        }

        private readonly IServiceScope _scope;

        public AppDbContext Db => _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        public UserManager<ApplicationUser> Users => _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        public RoleManager<IdentityRole> Roles => _scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        public Task SeedAsync(IConfiguration config) =>
            AdminAccountSeeder.SeedAsync(config, Roles, Users, NullLogger.Instance);

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
            _connection.Dispose();
        }
    }

    [Fact]
    public async Task Creates_Admin_From_Configuration_With_Admin_Role()
    {
        using var h = new Harness();
        await h.SeedAsync(Config());

        var admin = await h.Users.FindByEmailAsync(Email);
        Assert.NotNull(admin);
        Assert.Equal(FullName, admin!.FullName);
        Assert.True(admin.IsActive);
        Assert.True(await h.Users.IsInRoleAsync(admin, AppRoles.Admin));
        Assert.True(await h.Users.CheckPasswordAsync(admin, Password));
    }

    [Fact]
    public async Task Creates_Admin_Role_When_Missing()
    {
        using var h = new Harness();
        Assert.False(await h.Roles.RoleExistsAsync(AppRoles.Admin));

        await h.SeedAsync(Config());

        Assert.True(await h.Roles.RoleExistsAsync(AppRoles.Admin));
    }

    [Fact]
    public async Task Existing_Admin_Keeps_Same_Id_And_Gets_Role_And_Active_Status()
    {
        using var h = new Harness();
        var existing = new ApplicationUser { UserName = Email, Email = Email, FullName = "Old", IsActive = false };
        Assert.True((await h.Users.CreateAsync(existing, Password)).Succeeded);
        var originalId = existing.Id;

        await h.SeedAsync(Config());

        var admin = await h.Users.FindByEmailAsync(Email);
        Assert.NotNull(admin);
        Assert.Equal(originalId, admin!.Id);
        Assert.True(admin.IsActive);
        Assert.True(await h.Users.IsInRoleAsync(admin, AppRoles.Admin));
        Assert.Equal(1, await h.Db.Users.CountAsync());
    }

    [Fact]
    public async Task Mismatched_Password_Is_Reset_To_Configured_Value()
    {
        using var h = new Harness();
        var existing = new ApplicationUser { UserName = Email, Email = Email, FullName = FullName };
        Assert.True((await h.Users.CreateAsync(existing, "OldPassword1")).Succeeded);

        await h.SeedAsync(Config());

        var admin = await h.Users.FindByEmailAsync(Email);
        Assert.True(await h.Users.CheckPasswordAsync(admin!, Password));
        Assert.False(await h.Users.CheckPasswordAsync(admin!, "OldPassword1"));
    }

    [Fact]
    public async Task Matching_Password_Is_Not_Reset_On_Reseed()
    {
        using var h = new Harness();
        await h.SeedAsync(Config());
        var before = (await h.Users.FindByEmailAsync(Email))!.SecurityStamp;

        await h.SeedAsync(Config());

        var after = (await h.Users.FindByEmailAsync(Email))!.SecurityStamp;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Seeder_Is_Idempotent_No_Duplicate_Users_Or_Roles()
    {
        using var h = new Harness();
        await h.SeedAsync(Config());
        await h.SeedAsync(Config());
        await h.SeedAsync(Config());

        Assert.Equal(1, await h.Db.Users.CountAsync());
        Assert.Equal(1, await h.Db.Roles.CountAsync(r => r.Name == AppRoles.Admin));
        var admin = await h.Users.FindByEmailAsync(Email);
        Assert.Single(await h.Users.GetRolesAsync(admin!));
    }

    [Fact]
    public async Task Existing_Other_Users_Are_Not_Modified_Or_Removed()
    {
        using var h = new Harness();
        var other = new ApplicationUser { UserName = "hr@hr.local", Email = "hr@hr.local", FullName = "HR", IsActive = true };
        Assert.True((await h.Users.CreateAsync(other, "HrUser#2026")).Succeeded);

        await h.SeedAsync(Config());

        Assert.Equal(2, await h.Db.Users.CountAsync());
        var hr = await h.Users.FindByEmailAsync("hr@hr.local");
        Assert.True(await h.Users.CheckPasswordAsync(hr!, "HrUser#2026"));
        Assert.False(await h.Users.IsInRoleAsync(hr!, AppRoles.Admin));
    }

    [Fact]
    public async Task Missing_Configuration_Fails_Clearly_And_Creates_Nothing()
    {
        using var h = new Harness();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.SeedAsync(Config(password: "")));

        Assert.Contains("AdminAccount", ex.Message);
        Assert.Empty(await h.Db.Users.ToListAsync());
    }

    [Fact]
    public async Task Password_Is_Never_Stored_In_Plain_Text()
    {
        using var h = new Harness();
        await h.SeedAsync(Config());

        var admin = await h.Users.FindByEmailAsync(Email);
        Assert.NotEqual(Password, admin!.PasswordHash);
        Assert.DoesNotContain(Password, admin.PasswordHash ?? string.Empty, StringComparison.Ordinal);
    }
}
