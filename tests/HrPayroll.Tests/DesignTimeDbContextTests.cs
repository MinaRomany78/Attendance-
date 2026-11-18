using HrPayroll.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Tests;

public sealed class DesignTimeDbContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HrPayroll-Factory-" + Guid.NewGuid().ToString("N"));

    public DesignTimeDbContextTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), """
            { "ConnectionStrings": { "DefaultConnection": "Server=base.invalid;Database=BaseDb;Integrated Security=True" } }
            """);
        File.WriteAllText(Path.Combine(_root, "appsettings.FactoryTest.json"), """
            { "ConnectionStrings": { "DefaultConnection": "Server=environment.invalid;Database=EnvironmentDb;Integrated Security=True" } }
            """);
    }

    [Fact]
    public void Factory_Uses_Environment_Settings_Without_Connecting_Or_Seeding()
    {
        using var db = new AppDbContextDesignFactory().CreateDbContext(
            ["--contentRoot", _root, "--environment", "FactoryTest"]);

        Assert.Equal("EnvironmentDb", db.Database.GetDbConnection().Database);
        Assert.Equal("environment.invalid", db.Database.GetDbConnection().DataSource);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Factory_Allows_Command_Line_Connection_Override()
    {
        using var db = new AppDbContextDesignFactory().CreateDbContext(
            ["--contentRoot", _root, "--environment", "FactoryTest",
             "--ConnectionStrings:DefaultConnection", "Server=override.invalid;Database=OverrideDb;Integrated Security=True"]);

        Assert.Equal("OverrideDb", db.Database.GetDbConnection().Database);
    }

    [Fact]
    public void Factory_Rejects_Empty_Connection_Instead_Of_Targeting_A_Different_Database()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new AppDbContextDesignFactory().CreateDbContext(
            ["--contentRoot", _root, "--environment", "FactoryTest", "--ConnectionStrings:DefaultConnection="]));

        Assert.Contains("ConnectionStrings:DefaultConnection", error.Message);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
