using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HrPayroll.Web.Services;

/// <summary>Uses the application's configuration without running the host, migrations or seeders.</summary>
public sealed class AppDbContextDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var hostConfiguration = new ConfigurationBuilder()
            .AddEnvironmentVariables("ASPNETCORE_")
            .AddEnvironmentVariables("DOTNET_")
            .AddCommandLine(args)
            .Build();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = hostConfiguration[WebHostDefaults.ContentRootKey] ?? FindContentRoot(),
            Args = args
        });

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Configure ConnectionStrings:DefaultConnection in appsettings.json or " +
                "ConnectionStrings__DefaultConnection before running EF commands.");

        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .Options);
    }

    private static string FindContentRoot()
    {
        // EF can be invoked from the solution root, a project directory or Visual Studio.
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HrPayroll.Web.csproj")))
                return directory.FullName;

            var webProject = Path.Combine(directory.FullName, "src", "HrPayroll.Web");
            if (File.Exists(Path.Combine(webProject, "HrPayroll.Web.csproj")))
                return webProject;
        }

        // Published applications carry their configuration next to the assembly.
        return AppContext.BaseDirectory;
    }
}
