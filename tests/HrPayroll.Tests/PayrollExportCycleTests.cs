using ClosedXML.Excel;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Services;

namespace HrPayroll.Tests;

public sealed class PayrollExportCycleTests
{
    [Theory]
    [InlineData(false, "2024-10-31", "2024-10-26", "2024-11-25")]
    [InlineData(false, "2024-11-26", "2024-11-26", "2024-12-25")]
    [InlineData(true, "2024-10-31", "2024-10-01", "2024-10-31")]
    public async Task Full_Workbook_Selects_Actual_Cycle_For_Report_End_And_Keeps_Negative_Net(bool legacy, string end, string expectedStart, string expectedEnd)
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        db.Employees.Add(TestDb.Employee(salary: 6000));
        var date = DateTime.Parse(end);
        var period = legacy ? PayrollPeriod.LegacyCalendarMonth(date.Year, date.Month) : PayrollPeriod.FromDate(date);
        var run = new PayrollRun
        {
            Year = period.Year, Month = period.Month, Version = 1, Status = PayrollStatus.Computed,
            PeriodStart = legacy ? null : period.Start, PeriodEnd = legacy ? null : period.End
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.PayrollItems.Add(new PayrollItem
        {
            EmployeeId = 1, PayrollRunId = run.Id, BasicSalary = 6000, ContractBasicSalary = 6000,
            DailyRate = 200, PayableBasicDays = 30, GrossEarnings = 6000, SalaryAdvancesPaid = 7000,
            LoansTotal = 7000, TotalDeductions = 7000, NetSalary = -1000, UncoveredDeductions = 1000
        });
        await db.SaveChangesAsync();
        var settings = new SettingsService(db);
        var export = new ExcelExportService(db, settings, new ReportService(db, settings));

        var bytes = await export.ExportFullWorkbookAsync(new AttendanceReportFilter { From = date, To = date });
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("Payroll");
        Assert.Contains(expectedStart, sheet.Cell(1, 1).GetString());
        Assert.Contains(expectedEnd, sheet.Cell(1, 1).GetString());
        Assert.Equal(-1000, sheet.Cell(6, 29).GetValue<decimal>());
        Assert.Equal(1000, sheet.Cell(6, 37).GetValue<decimal>());
    }
}
