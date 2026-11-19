using System.Security.Claims;
using System.Text;
using ClosedXML.Excel;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using HrPayroll.Web.Controllers;
using HrPayroll.Web.Services;
using HrPayroll.Web.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HrPayroll.Tests;

public class AttendanceImportWorkflowTests
{
    private static readonly byte[] Sample = Encoding.UTF8.GetBytes(
        "3\t2026-11-10 08:03:09\t101\t1\tموظف تجريبي\tI\t0\t1\n" +
        "3\t2026-11-10 08:08:24\t102\t1\tموظف تجريبي\tO\t0\t1");

    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static ImportController Controller(AppDbContext db, IDataProtectionProvider protection, string owner = "operator")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, owner), new Claim(ClaimTypes.Name, owner),
                new Claim(ClaimTypes.Role, "Admin")
            }, "test"))
        };
        return new ImportController(new AttendanceImportService(db, NullLogger<AttendanceImportService>.Instance),
            new AttendanceProcessingService(db, new SettingsService(db), NullLogger<AttendanceProcessingService>.Instance),
            db, NullLogger<ImportController>.Instance, protection)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new MemoryTempData())
        };
    }

    private static FormFile Upload(byte[] bytes, string name = "device.txt")
        => new(new MemoryStream(bytes), 0, bytes.Length, "combinedFile", name);

    private static async Task<ImportFilesViewModel> Preview(ImportController controller, byte[]? bytes = null)
    {
        var result = await controller.Upload(null, null, default, Upload(bytes ?? Sample));
        return Assert.IsType<ImportFilesViewModel>(Assert.IsType<ViewResult>(result).Model);
    }

    [Fact]
    public async Task Combined_Upload_Preview_Export_And_Commit_Use_One_Pipeline()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(TestDb.Employee(code: "3"));
        await db.SaveChangesAsync();
        var controller = Controller(db, new EphemeralDataProtectionProvider());
        var model = await Preview(controller);
        var zone = Assert.IsType<ImportFileZoneViewModel>(model.Combined);
        Assert.Equal(2, model.TotalAccepted);
        Assert.Equal(1, model.TotalCheckIns);
        Assert.Equal(1, model.TotalCheckOuts);
        Assert.Empty(await db.RawPunches.ToListAsync());
        var export = Assert.IsType<FileContentResult>(await controller.Validation(zone.Token!, default));
        using (var workbook = new XLWorkbook(new MemoryStream(export.FileContents)))
        {
            Assert.Equal(3, workbook.Worksheet(1).LastRowUsed()!.RowNumber());
            Assert.Equal("3", workbook.Worksheet(1).Cell(2, 2).GetString());
        }
        await controller.Confirm(null, null, null, null, processAttendance: false,
            combinedToken: zone.Token, combinedName: "forged-filename.txt");
        Assert.Equal(2, await db.RawPunches.CountAsync());
        Assert.Equal("device.txt", (await db.ImportBatches.SingleAsync()).FileName);
        Assert.All(await db.RawPunches.ToListAsync(), p => Assert.Equal(1, p.EmployeeId));
    }

    [Fact]
    public async Task Confirmation_Rejects_An_Employee_Reassignment_After_Preview()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        var first = TestDb.Employee(code: "3");
        var second = TestDb.Employee(2, "payroll-2");
        second.DeviceEmployeeCode = null;
        db.Employees.AddRange(first, second);
        await db.SaveChangesAsync();
        var controller = Controller(db, new EphemeralDataProtectionProvider());
        var preview = await Preview(controller);
        first.DeviceEmployeeCode = "retired-3";
        first.EmployeeCode = "payroll-1";
        second.DeviceEmployeeCode = "3";
        await db.SaveChangesAsync();
        await controller.Confirm(null, null, null, null, processAttendance: false, combinedToken: preview.Combined!.Token);
        Assert.Empty(await db.RawPunches.ToListAsync());
        Assert.Empty(await db.ImportBatches.ToListAsync());
        Assert.Contains("تغير ربط", controller.TempData["Error"]?.ToString());
    }

    [Fact]
    public async Task Separate_File_Duplicates_Have_Consistent_Preview_Export_And_Batch_Counts()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(TestDb.Employee(code: "3"));
        await db.SaveChangesAsync();
        var controller = Controller(db, new EphemeralDataProtectionProvider());
        var bytes = Encoding.UTF8.GetBytes("1\tdevice\t3\tموظف تجريبي\tFP\t2026-11-10 08:03:09");
        var response = await controller.Upload(Upload(bytes, "entry.txt"), Upload(bytes, "exit.txt"), default);
        var model = Assert.IsType<ImportFilesViewModel>(Assert.IsType<ViewResult>(response).Model);
        Assert.Equal(2, model.TotalRecords);
        Assert.Equal(1, model.TotalAccepted);
        Assert.Equal(1, model.TotalDuplicates);
        var entry = model.Entry!; var exit = model.Exit!;
        var tokens = new List<string> { entry.Token!, exit.Token! };
        var export = Assert.IsType<FileContentResult>(await controller.Validation(exit.Token!, default, tokens));
        using (var workbook = new XLWorkbook(new MemoryStream(export.FileContents)))
            Assert.Equal("مكرر", workbook.Worksheet(1).Cell(2, 8).GetString());
        await controller.Confirm(entry.Token, entry.FileName, exit.Token, exit.FileName, processAttendance: false);
        Assert.Equal(1, await db.RawPunches.CountAsync());
        Assert.Equal(2, await db.ImportBatches.CountAsync());
        Assert.Equal(model.TotalRecords, await db.ImportBatches.SumAsync(b => b.TotalRows));
        Assert.Equal(model.TotalAccepted, await db.ImportBatches.SumAsync(b => b.SuccessRows));
        Assert.Equal(model.TotalDuplicates, await db.ImportBatches.SumAsync(b => b.DuplicateRows));
    }

    [Fact]
    public async Task Authorized_Mapping_Resolution_Refreshes_The_Preview_Snapshot()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        var employee = TestDb.Employee(code: "payroll-1");
        employee.DeviceEmployeeCode = null;
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var controller = Controller(db, new EphemeralDataProtectionProvider());
        var before = await Preview(controller);
        Assert.Equal(2, before.TotalUnknownEmployees);
        var token = before.Combined!.Token!;
        var response = await controller.ResolveMapping(token, "3", employee.Id, new List<string> { token }, default);
        var after = Assert.IsType<ImportFilesViewModel>(Assert.IsType<ViewResult>(response).Model);
        Assert.Equal(2, after.TotalAccepted);
        Assert.NotEqual(token, after.Combined!.Token);
        await controller.Confirm(null, null, null, null, processAttendance: false, combinedToken: after.Combined.Token);
        Assert.Equal(2, await db.RawPunches.CountAsync());
        Assert.Equal(9000m, employee.BasicSalary);
    }

    [Fact]
    public async Task Staged_Files_Reject_Other_Owners_Path_Traversal_Tampering_And_Cancellation()
    {
        var store = new AttendanceImportStagingStore(new EphemeralDataProtectionProvider());
        var token = await store.StageAsync(Sample, "owner", "combined", "device.txt", default);
        try
        {
            var read = await store.ReadAsync(token, "owner", default);
            Assert.Equal(Sample, read.Bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(token, "other", default));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync("../../arbitrary-file", "owner", default));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(token[..^8] + "tampered", "owner", default));
            Assert.Throws<InvalidDataException>(() => store.Delete(token, "other"));
            Assert.Equal(Sample, (await store.ReadAsync(token, "owner", default)).Bytes);
        }
        finally { store.Delete(token, "owner"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(token, "owner", default));
    }

    [Fact]
    public async Task Expired_Preview_Is_Rejected_Without_Reading_Files()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = provider.CreateProtector("HrPayroll.Attendance.Staging.Token.v1");
        var expired = new StagedAttendanceFile(Guid.NewGuid().ToString("N"), "owner", "combined", "device.txt", DateTime.UtcNow.AddMinutes(-1));
        var token = protector.Protect(System.Text.Json.JsonSerializer.Serialize(expired));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AttendanceImportStagingStore(provider).ReadAsync(token, "owner", default));
    }

    [Fact]
    public async Task Cancelled_Preview_Cannot_Be_Confirmed()
    {
        using var database = new TestDb();
        await using var db = database.CreateContext();
        db.Employees.Add(TestDb.Employee(code: "3"));
        await db.SaveChangesAsync();
        var controller = Controller(db, new EphemeralDataProtectionProvider());
        var model = await Preview(controller);
        controller.Cancel(new List<string> { model.Combined!.Token! });
        await controller.Confirm(null, null, null, null, processAttendance: false, combinedToken: model.Combined.Token);
        Assert.Empty(await db.RawPunches.ToListAsync());
    }
}
