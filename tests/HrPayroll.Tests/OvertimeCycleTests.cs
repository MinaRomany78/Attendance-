using System.Security.Claims;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using HrPayroll.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HrPayroll.Tests;

public sealed class OvertimeCycleTests
{
    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static OvertimeController Controller(AppDbContext db)
    {
        var settings = new SettingsService(db);
        var reports = new ReportService(db, settings);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim(ClaimTypes.Name, "payroll")
            }, "test"))
        };
        var controller = new OvertimeController(db, reports, settings, new AuditService(db), new ExcelExportService(db, settings, reports))
        {
            ControllerContext = new ControllerContext { HttpContext = context, RouteData = new Microsoft.AspNetCore.Routing.RouteData() },
            TempData = new TempDataDictionary(context, new MemoryTempData())
        };
        controller.Url = new UrlHelper(controller.ControllerContext);
        return controller;
    }

    private static DailyAttendance Day(DateTime date) => new()
    {
        EmployeeId = 1, WorkDate = date, ShiftId = 1,
        ShiftStart = date.AddHours(7), ShiftEnd = date.AddHours(15),
        CheckInTime = date.AddHours(7), CheckOutTime = date.AddHours(16),
        RawOvertimeMinutes = 60, Status = AttendanceStatus.Present, PunchReviewStatus = ClassificationStatus.Classified
    };

    private static async Task Seed(AppDbContext db)
    {
        db.Shifts.Add(TestDb.FirstShift());
        db.Employees.Add(TestDb.Employee(salary: 6000));
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false, 0, 60)]
    [InlineData(true, 60, 0)]
    public async Task Bulk_Approval_Uses_Actual_Protected_Bounds_And_Preserves_Legacy_Calendar_Runs(bool legacy, int octoberExpected, int novemberExpected)
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await Seed(db);
        var october = Day(new(2024, 10, 31));
        var november = Day(new(2024, 11, 26));
        db.DailyAttendances.AddRange(october, november);
        var period = PayrollPeriod.ForMonth(2024, 11);
        db.PayrollRuns.Add(new PayrollRun
        {
            Year = 2024, Month = 11, Version = 1, Status = PayrollStatus.Locked,
            PeriodStart = legacy ? null : period.Start, PeriodEnd = legacy ? null : period.End
        });
        await db.SaveChangesAsync();

        await Controller(db).ApproveAll(october.WorkDate, november.WorkDate, null, null, null, null, CancellationToken.None);

        Assert.Equal(octoberExpected, october.ApprovedOvertimeMinutes);
        Assert.Equal(novemberExpected, november.ApprovedOvertimeMinutes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_And_Bulk_Approval_Invalidate_Draft_Until_Explicit_Regeneration(bool bulk)
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await Seed(db);
        var day = Day(new(2024, 10, 31));
        db.DailyAttendances.Add(day);
        await db.SaveChangesAsync();
        var payroll = new PayrollService(db, new SettingsService(db), new AuditService(db), NullLogger<PayrollService>.Instance);
        var result = await payroll.GenerateAsync(2024, 11);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == result.PayrollRunId);
        var originalNet = item.NetSalary;
        var controller = Controller(db);

        if (bulk) await controller.ApproveAll(day.WorkDate, day.WorkDate, null, null, null, null, CancellationToken.None);
        else await controller.ApproveDay(1, "2024-10-31", 60, null, CancellationToken.None);

        Assert.Equal(60, day.ApprovedOvertimeMinutes);
        Assert.NotNull(item.FinancialReviewReason);
        Assert.Equal(originalNet, item.NetSalary);
        await Assert.ThrowsAsync<InvalidOperationException>(() => payroll.ApproveAsync(result.PayrollRunId, "payroll", "u1"));
        var regenerated = await payroll.GenerateAsync(2024, 11);
        var refreshed = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == regenerated.PayrollRunId);
        Assert.Null(refreshed.FinancialReviewReason);
        Assert.Equal(25m, refreshed.OvertimeAmount);
        Assert.Equal(originalNet + 25m, refreshed.NetSalary);
    }
}
