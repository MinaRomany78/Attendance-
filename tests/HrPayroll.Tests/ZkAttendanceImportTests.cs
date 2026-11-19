using System.Text;
using System.Text.Json;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HrPayroll.Tests;

public class ZkAttendanceImportTests
{
    private static byte[] File(params string[] rows) => Encoding.UTF8.GetBytes(string.Join("\n", rows));
    private static string Row(string code = "3", string direction = "I", string time = "2026-11-10 08:03:09")
        => $"{code}\t{time}\t101\t1\tموظف تجريبي\t{direction}\t0\t1";
    private static AttendanceImportService Service(Infrastructure.Persistence.AppDbContext db)
        => new(db, NullLogger<AttendanceImportService>.Instance);
    private static Employee Employee(int id = 1, string code = "payroll-3", string? biometricCode = "3")
    {
        var employee = TestDb.Employee(id, code);
        employee.DeviceEmployeeCode = biometricCode;
        employee.Department = "قسم اختباري";
        return employee;
    }

    [Fact]
    public async Task Sample_Contains_Two_Matched_Records_One_In_And_One_Out_Without_Losing_First_Row()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var service = Service(db);
        var analysis = await service.AnalyzeAsync(File(Row(), Row(direction: "O", time: "2026-11-10 08:08:24")), "sample.txt");
        Assert.Equal(2, analysis.Total);
        Assert.Equal(2, analysis.SuccessCount);
        Assert.Equal(1, analysis.CheckInCount);
        Assert.Equal(1, analysis.CheckOutCount);
        Assert.Equal(1, analysis.Rows[0].RowNumber);
        Assert.All(analysis.Rows, row =>
        {
            Assert.Equal("3", row.DeviceEmployeeCode);
            Assert.Equal(1, row.EmployeeId);
            Assert.Equal(DateTimeKind.Unspecified, row.PunchTime!.Value.Kind);
            Assert.Null(row.DeviceId);
        });
        var committed = await service.CommitAsync(analysis, "sample.txt", "test-user", "tester");
        Assert.Equal(2, committed.Inserted);
        var punches = await db.RawPunches.OrderBy(p => p.PunchTime).ToListAsync();
        Assert.Equal(new DateTime(2026, 11, 10, 8, 3, 9), punches[0].PunchTime);
        Assert.Equal("I", punches[0].AttType);
        Assert.Equal("O", punches[1].AttType);
        Assert.Equal(8, JsonSerializer.Deserialize<string[]>(punches[0].OriginalFieldsJson!)!.Length);
        // These are import assertions; this short sample is never processed as a shift.
        Assert.Empty(await db.DailyAttendances.ToListAsync());
    }

    [Fact]
    public async Task Windows1256_Arabic_Name_Is_Preserved_Without_Changing_Employee_Information()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var database = new TestDb();
        await using var db = database.CreateContext();
        var employee = Employee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(Encoding.GetEncoding(1256).GetBytes(Row()), "device.txt");
        Assert.Equal("windows-1256", analysis.EncodingName);
        Assert.Equal("موظف تجريبي", analysis.Rows.Single().Name);
        await Service(db).CommitAsync(analysis, "device.txt", "test-user", "tester");
        Assert.Equal(9000m, employee.BasicSalary);
        Assert.Equal("قسم اختباري", employee.Department);
        Assert.Equal("موظف اختبار payroll-3", employee.FullName);
        Assert.Equal(1, await db.Employees.CountAsync());
    }

    [Fact]
    public async Task Biometric_Code_Takes_Priority_Over_Another_Employees_Payroll_Code()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.AddRange(Employee(1, "3", "other-biometric"), Employee(2, "payroll-2", "3"));
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File(Row()), "device.txt");
        Assert.Equal(1, analysis.SuccessCount);
        Assert.Equal(2, analysis.Rows.Single().EmployeeId);
    }

    [Fact]
    public async Task Duplicate_Normalized_Biometric_Codes_Are_Withheld_For_Review()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.AddRange(Employee(1, "payroll-1", "3"), Employee(2, "payroll-2", " 3 "));
        await db.SaveChangesAsync();
        var service = Service(db);
        var analysis = await service.AnalyzeAsync(File(Row()), "ambiguous.txt");
        Assert.Equal(1, analysis.ReviewCount);
        Assert.Equal(0, analysis.SuccessCount);
        Assert.Null(analysis.Rows.Single().EmployeeId);
        var committed = await service.CommitAsync(analysis, "ambiguous.txt", "test-user", "tester");
        Assert.Equal(0, committed.Inserted);
        Assert.Equal(1, committed.Failed);
        Assert.Equal(1, (await db.ImportBatches.SingleAsync()).ReviewRows);
    }

    [Fact]
    public async Task Leading_Zeroes_Are_Distinct_And_Only_Outer_Whitespace_Is_Normalized()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.AddRange(Employee(1, "payroll-1", "003"), Employee(2, "payroll-2", "3"));
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File(Row(" 003 "), Row("3", "O")), "codes.txt");
        Assert.Equal(2, analysis.SuccessCount);
        Assert.Equal("003", analysis.Rows[0].DeviceEmployeeCode);
        Assert.Equal(1, analysis.Rows[0].EmployeeId);
        Assert.Equal(2, analysis.Rows[1].EmployeeId);
    }

    [Fact]
    public async Task Unique_EmployeeCode_Fallback_Still_Matches_Without_Using_Names()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee(code: "3", biometricCode: null));
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File(Row()), "fallback.txt");
        Assert.Equal(1, analysis.SuccessCount);
        Assert.Equal(1, analysis.Rows.Single().EmployeeId);
        Assert.Contains("مطابقة فريدة", analysis.Rows.Single().Message);
    }

    [Fact]
    public async Task Unknown_Employees_And_Malformed_Rows_Are_Reported_While_Valid_Rows_Are_Imported()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File("3\tbroken\trow", Row(), Row("unknown"),
            Row(time: "invalid-time"), Row(direction: "unsupported")), "mixed.txt");
        Assert.Equal(5, analysis.Total);
        Assert.Equal(1, analysis.SuccessCount);
        Assert.Equal(1, analysis.UnknownEmployeeCount);
        Assert.Equal(2, analysis.InvalidCount);
        Assert.Equal(1, analysis.ReviewCount);
        Assert.Contains(analysis.Rows, row => row.DeviceEmployeeCode == "unknown" && row.Message!.Contains("unknown"));
        var result = await Service(db).CommitAsync(analysis, "mixed.txt", "test-user", "tester");
        Assert.Equal(1, result.Inserted);
        Assert.Equal(4, result.Failed);
        Assert.Equal(1, await db.Employees.CountAsync());
        var batch = await db.ImportBatches.SingleAsync();
        Assert.Equal(5, batch.TotalRows);
        Assert.Equal(batch.TotalRows, batch.SuccessRows + batch.InvalidRows + batch.UnknownEmployeeRows + batch.ReviewRows + batch.DuplicateRows);
    }

    [Fact]
    public async Task Duplicate_Records_In_File_And_Reimports_Are_Idempotent()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var bytes = File(Row(), Row(), Row(direction: "O"));
        var service = Service(db);
        var first = await service.AnalyzeAsync(bytes, "first.txt");
        Assert.Equal(2, first.SuccessCount);
        Assert.Equal(1, first.DuplicateCount);
        var committed = await service.CommitAsync(first, "first.txt", "test-user", "tester");
        Assert.Equal(2, committed.Inserted);
        Assert.Equal(1, committed.SkippedDuplicate);
        var second = await service.AnalyzeAsync(bytes, "renamed.txt");
        Assert.Equal(0, second.SuccessCount);
        Assert.Equal(3, second.DuplicateCount);
        var repeated = await service.CommitAsync(second, "renamed.txt", "test-user", "tester");
        Assert.Equal(0, repeated.Inserted);
        Assert.Equal(3, repeated.SkippedDuplicate);
        Assert.Equal(2, await db.RawPunches.CountAsync());
    }

    [Fact]
    public async Task Overlapping_Date_Ranges_Keep_New_Records_And_Exclude_Existing_Records()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var service = Service(db);
        var first = await service.AnalyzeAsync(File(Row(), Row(direction: "O", time: "2026-11-10 16:00:00")), "day1.txt");
        await service.CommitAsync(first, "day1.txt", "test-user", "tester");
        var overlapping = await service.AnalyzeAsync(File(Row(direction: "O", time: "2026-11-10 16:00:00"),
            Row(time: "2026-11-11 08:00:00")), "overlap.txt");
        Assert.Equal(1, overlapping.DuplicateCount);
        Assert.Equal(1, overlapping.SuccessCount);
        var result = await service.CommitAsync(overlapping, "overlap.txt", "test-user", "tester");
        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.SkippedDuplicate);
        Assert.Equal(3, await db.RawPunches.CountAsync());
    }

    [Fact]
    public async Task Manual_File_And_Ethernet_Records_Deduplicate_Across_Device_And_Direction_Aliases()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var service = Service(db);
        await service.CommitAsync(await service.AnalyzeAsync(File(Row()), "manual.txt"), "manual.txt", "test-user", "tester");
        var ethernet = await service.AnalyzeRecordsAsync(new[]
        {
            new ParsedPunchRow { RowNumber = 1, DeviceId = "verified-serial", DeviceEmployeeCode = "3", AttType = "check-in", PunchTime = new DateTime(2026, 11, 10, 8, 3, 9) },
            new ParsedPunchRow { RowNumber = 2, DeviceId = "verified-serial", DeviceEmployeeCode = "3", AttType = "OUT", PunchTime = new DateTime(2026, 11, 10, 8, 3, 9) }
        }, "test-device");
        Assert.Equal(1, ethernet.DuplicateCount);
        Assert.Equal(1, ethernet.SuccessCount);
        var result = await service.CommitAsync(ethernet, "test-device", "test-user", "tester");
        Assert.Equal(1, result.Inserted);
        Assert.Equal(2, await db.RawPunches.CountAsync());
    }

    [Fact]
    public async Task File_Import_Recognizes_Preexisting_Ethernet_Record_Without_Rewriting_Historical_Hash()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        db.RawPunches.Add(new RawPunch
        {
            DeviceEmployeeCode = "3", EmployeeId = 1, DeviceId = "serial-verified", AttType = "IN",
            PunchTime = new DateTime(2026, 11, 10, 8, 3, 9), DedupHash = "EXISTING-HISTORICAL-DRIVER-HASH", IsValid = true
        });
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File(Row()), "manual.txt");
        Assert.Equal(1, analysis.DuplicateCount);
        Assert.True(analysis.Rows.Single().AlreadyImported);
        Assert.Equal("EXISTING-HISTORICAL-DRIVER-HASH", (await db.RawPunches.SingleAsync()).DedupHash);
    }

    [Fact]
    public async Task Payroll_Code_And_Biometric_Code_For_Same_Employee_Deduplicate()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee(code: "payroll-3", biometricCode: "3"));
        await db.SaveChangesAsync();
        var service = Service(db);
        await service.CommitAsync(await service.AnalyzeAsync(File(Row("payroll-3")), "legacy.txt"), "legacy.txt", "test-user", "tester");
        var analysis = await service.AnalyzeAsync(File(Row("3")), "biometric.txt");
        Assert.Equal(1, analysis.DuplicateCount);
        Assert.Equal(1, await db.RawPunches.CountAsync());
    }

    [Fact]
    public async Task Distinct_Subsecond_Punches_And_Opposite_Directions_Are_Preserved()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var service = Service(db);
        var analysis = await service.AnalyzeAsync(File(Row(time: "2026-11-10 08:03:09.123"),
            Row(time: "2026-11-10 08:03:09.456"), Row(direction: "O", time: "2026-11-10 08:03:09.456")), "precision.txt");
        Assert.Equal(3, analysis.SuccessCount);
        Assert.Equal(3, analysis.Rows.Select(r => r.DedupHash).Distinct().Count());
        await service.CommitAsync(analysis, "precision.txt", "test-user", "tester");
        Assert.Equal(3, await db.RawPunches.CountAsync());
        var repeated = await service.AnalyzeAsync(File(Row(time: "2026-11-10 08:03:09.123")), "precision-repeat.txt");
        Assert.Equal(1, repeated.DuplicateCount);
    }

    [Fact]
    public async Task Split_Entry_Mode_Does_Not_Override_Explicit_Checkout()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File(Row(), Row(direction: "O", time: "2026-11-10 16:00:00")),
            "entry.txt", expectedDirection: PunchDirection.In);
        Assert.Equal(1, analysis.SuccessCount);
        Assert.Equal(1, analysis.ReviewCount);
        Assert.Equal("O", analysis.Rows[1].AttType);
        Assert.Contains("يخالف", analysis.Rows[1].Message);
    }

    [Fact]
    public async Task Commit_Rechecks_Duplicates_That_Appear_After_Preview()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var service = Service(db);
        var preview1 = await service.AnalyzeAsync(File(Row()), "first.txt");
        var preview2 = await service.AnalyzeAsync(File(Row()), "second.txt");
        await service.CommitAsync(preview1, "first.txt", "test-user", "tester");
        var second = await service.CommitAsync(preview2, "second.txt", "test-user", "tester");
        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.SkippedDuplicate);
        Assert.Equal(ImportRowStatus.Duplicate, preview2.Rows.Single().Status);
        var batch = await db.ImportBatches.SingleAsync(b => b.Id == second.ImportBatchId);
        Assert.Equal(1, batch.DuplicateRows);
        Assert.Equal(0, batch.SuccessRows);
    }

    [Fact]
    public async Task Employee_Mapping_Changed_After_Preview_Is_Withheld()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        var employee = Employee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var service = Service(db);
        var preview = await service.AnalyzeAsync(File(Row()), "mapping.txt");
        employee.DeviceEmployeeCode = "replacement";
        await db.SaveChangesAsync();
        var result = await service.CommitAsync(preview, "mapping.txt", "test-user", "tester");
        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Failed);
        Assert.Empty(await db.RawPunches.ToListAsync());
    }

    [Fact]
    public async Task Headerless_Legacy_Six_Column_File_Imports_First_Row()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File("1\tdevice1\t3\tموظف تجريبي\tFP\t2026-11-10 08:03:09"), "legacy.txt");
        Assert.Equal(1, analysis.Total);
        Assert.Equal(1, analysis.SuccessCount);
        Assert.Equal(1, analysis.Rows.Single().RowNumber);
    }

    [Fact]
    public async Task Late_Header_Does_Not_Discard_Valid_Punches_Before_It()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File(Row(),
            "Biometric Code\tDate/Time\tMetadata A\tMetadata B\tName\tDirection\tMetadata C\tMetadata D",
            Row(direction: "O", time: "2026-11-10 16:00:00")), "concatenated.txt");
        Assert.Equal(3, analysis.Total);
        Assert.Equal(2, analysis.SuccessCount);
        Assert.Equal(1, analysis.InvalidCount);
        Assert.Equal(1, analysis.Rows.First(r => r.Status == ImportRowStatus.Success).RowNumber);
    }

    [Fact]
    public async Task Neutral_Fingerprint_Type_In_Zk_Direction_Column_Is_Withheld()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var analysis = await Service(db).AnalyzeAsync(File(Row(), Row(direction: "FP", time: "2026-11-10 16:00:00")), "direction-review.txt");
        Assert.Equal(1, analysis.SuccessCount);
        Assert.Equal(1, analysis.ReviewCount);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\u0001")]
    [InlineData("\u007F")]
    public async Task Damaged_Control_Character_Row_Is_Rejected_Without_Losing_Surrounding_Valid_Rows(string control)
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(Employee());
        await db.SaveChangesAsync();
        var damaged = Row(time: "2026-11-10 12:00:00").Replace("موظف تجريبي", "موظف" + control + "تجريبي");
        var bytes = File(Row(), damaged, Row(direction: "O", time: "2026-11-10 16:00:00"));
        Assert.Equal(AttendanceFileFormat.Csv, AttendanceFileParser.DetectFormat(bytes));
        var service = Service(db);
        var analysis = await service.AnalyzeAsync(bytes, "damaged-row.txt");
        Assert.Equal(3, analysis.Total);
        Assert.Equal(2, analysis.SuccessCount);
        Assert.Equal(1, analysis.InvalidCount);
        Assert.Equal(ImportRowStatus.InvalidRow, analysis.Rows[1].Status);
        Assert.Equal(1, analysis.Rows[0].RowNumber);
        Assert.Equal(3, analysis.Rows[2].RowNumber);
        var committed = await service.CommitAsync(analysis, "damaged-row.txt", "test-user", "tester");
        Assert.Equal(2, committed.Inserted);
        Assert.Equal(1, committed.Failed);
        Assert.Equal(2, await db.RawPunches.CountAsync());
    }
}
