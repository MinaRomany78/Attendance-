using System.Text;
using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HrPayroll.Tests;

public class ZkEmployeeMappingTests
{
    private static Employee Employee(int id, string employeeCode, string? biometricCode, string? name = null)
    {
        var employee = TestDb.Employee(id, employeeCode);
        employee.DeviceEmployeeCode = biometricCode;
        employee.FullName = name ?? "موظف تجريبي " + id;
        employee.Department = "قسم اختباري";
        return employee;
    }
    private static byte[] Text(params string[] rows) => Encoding.UTF8.GetBytes(string.Join("\n", rows));

    [Fact]
    public async Task Resolving_A_Missing_Code_Changes_Only_Mapping_And_Records_An_Audit()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        var employee = Employee(1, "payroll-1", null);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var service = new AttendanceEmployeeMappingService(db, new AuditService(db));
        await service.ResolveAsync(" 003 ", 1, "operator-id", "operator");
        Assert.Equal("003", employee.DeviceEmployeeCode);
        Assert.Equal("payroll-1", employee.EmployeeCode);
        Assert.Equal("موظف تجريبي 1", employee.FullName);
        Assert.Equal("قسم اختباري", employee.Department);
        Assert.Equal(9000m, employee.BasicSalary);
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("003", audit.NewValue);
        Assert.Equal("operator", audit.UserName);
        await service.ResolveAsync("003", 1, "operator-id", "operator");
        Assert.Equal(1, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Resolving_An_Already_Used_Normalized_Code_Is_Rejected()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        var target = Employee(2, "payroll-2", null);
        db.Employees.AddRange(Employee(1, "payroll-1", "AbC"), target);
        await db.SaveChangesAsync();
        var service = new AttendanceEmployeeMappingService(db, new AuditService(db));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(" abc ", 2, "operator-id", "operator"));
        Assert.Null(target.DeviceEmployeeCode);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Resolution_Cannot_Overwrite_An_Existing_Different_Biometric_Code()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        var employee = Employee(1, "payroll-1", "existing-code");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AttendanceEmployeeMappingService(db, new AuditService(db))
            .ResolveAsync("replacement", 1, "operator-id", "operator"));
        Assert.Equal("existing-code", employee.DeviceEmployeeCode);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Unknown_Punch_Becomes_Importable_Only_After_Explicit_Mapping_And_Reanalysis()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee(1, "payroll-1", null));
        await db.SaveChangesAsync();
        var bytes = Text("003\t2026-11-10 08:03:09\t101\t1\tاسم من الملف\tI\t0\t1");
        var imports = new AttendanceImportService(db, NullLogger<AttendanceImportService>.Instance);
        var before = await imports.AnalyzeAsync(bytes, "mapping.txt");
        Assert.Equal(1, before.UnknownEmployeeCount);
        await new AttendanceEmployeeMappingService(db, new AuditService(db)).ResolveAsync("003", 1, "operator-id", "operator");
        Assert.Equal(1, before.UnknownEmployeeCount);
        var after = await imports.AnalyzeAsync(bytes, "mapping.txt");
        Assert.Equal(1, after.SuccessCount);
        Assert.Equal(1, after.Rows.Single().EmployeeId);
        Assert.Equal(1, await db.Employees.CountAsync());
        Assert.Equal(9000m, (await db.Employees.SingleAsync()).BasicSalary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456789012345678901234567890123456789012345678901")]
    public async Task Invalid_Mapping_Codes_Are_Rejected(string code)
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee(1, "payroll-1", null));
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AttendanceEmployeeMappingService(db, new AuditService(db))
            .ResolveAsync(code, 1, "operator-id", "operator"));
        Assert.Null((await db.Employees.SingleAsync()).DeviceEmployeeCode);
    }

    [Fact]
    public async Task Roster_Prefers_Biometric_Column_And_Uses_Names_Only_As_Diagnostics()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        var employee = Employee(1, "payroll-1", "003", "موظف تجريبي");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var result = await new ZkRosterReconciliationService(db).AnalyzeAsync(Text("EmployeeCode\tBiometric Code\tName\tSalary",
            "unrelated-payroll-code\t003\t  موظف   تجريبي  \t999999"));
        Assert.True(result.Analyzed);
        Assert.Equal(1, result.SystemEmployees);
        Assert.Equal(1, result.DeviceRows);
        Assert.Equal("matched", result.Rows.Single().Status);
        Assert.Equal("003", result.Rows.Single().Code);
        Assert.Equal(9000m, employee.BasicSalary);
        Assert.Equal("موظف تجريبي", employee.FullName);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Roster_Reports_Duplicate_Unknown_Invalid_And_Missing_Codes_Without_Modifying_Data()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.AddRange(Employee(1, "payroll-1", "003"), Employee(2, "payroll-2", "004"));
        await db.SaveChangesAsync();
        var result = await new ZkRosterReconciliationService(db).AnalyzeAsync(Text("Biometric Code\tName",
            "003\tموظف تجريبي 1", " 003 \tموظف تجريبي 1", "unknown\tموظف غير مسجل", "\tكود مفقود"));
        Assert.Equal(4, result.DeviceRows);
        Assert.Equal(2, result.UniqueDeviceCodes);
        Assert.Equal(2, result.Rows.Count(r => r.Status == "review"));
        Assert.Contains(result.Rows, r => r.Status == "unknown" && r.Code == "unknown");
        Assert.Contains(result.Rows, r => r.Status == "invalid");
        Assert.Contains(result.Rows, r => r.Status == "missing" && r.Code == "004");
        Assert.Equal(2, await db.Employees.CountAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Roster_Name_Differences_And_Payroll_Code_Fallback_Require_Review()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.AddRange(Employee(1, "payroll-1", "003"), Employee(2, "payroll-2", null));
        await db.SaveChangesAsync();
        var result = await new ZkRosterReconciliationService(db).AnalyzeAsync(Text("Biometric Code\tName",
            "003\tاسم مختلف", "payroll-2\tموظف تجريبي 2"));
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("review", row.Status));
        Assert.Equal("موظف تجريبي 1", (await db.Employees.SingleAsync(e => e.Id == 1)).FullName);
        Assert.Null((await db.Employees.SingleAsync(e => e.Id == 2)).DeviceEmployeeCode);
    }

    [Fact]
    public async Task A_Synthetic_251_Employee_Roster_Reconciles_Without_Changing_Employee_Data()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.AddRange(Enumerable.Range(1, 251).Select(id => Employee(id, "payroll-" + id, id.ToString("D5"))));
        await db.SaveChangesAsync();
        var rows = new[] { "Biometric Code\tName" }.Concat(Enumerable.Range(1, 251).Select(id => $"{id:D5}\tموظف تجريبي {id}"));
        var result = await new ZkRosterReconciliationService(db).AnalyzeAsync(Text(rows.ToArray()));
        Assert.Equal(251, result.SystemEmployees);
        Assert.Equal(251, result.DeviceRows);
        Assert.Equal(251, result.UniqueDeviceCodes);
        Assert.Equal(251, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("matched", row.Status));
        Assert.Equal(251, await db.Employees.CountAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Attendance_Records_With_Name_And_Code_Headers_Are_Rejected_As_A_Roster()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee(1, "payroll-1", "003"));
        await db.SaveChangesAsync();
        var bytes = Text("ID\tName\tAtt.Time\tAttType",
            "003\tموظف تجريبي 1\t2026-11-10 08:03:09\tI");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ZkRosterReconciliationService(db).AnalyzeAsync(bytes));
        Assert.Contains("حضور", error.Message);
        Assert.Equal(1, await db.Employees.CountAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }
}
