using ClosedXML.Excel;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HrPayroll.Tests;

public sealed class PayrollEarningsAndFinalizationTests
{
    private static PayrollService Service(AppDbContext db) => new(db, new SettingsService(db), new AuditService(db), NullLogger<PayrollService>.Instance);

    private static async Task SeedAsync(AppDbContext db)
    {
        db.Shifts.Add(TestDb.FirstShift());
        db.Employees.Add(TestDb.Employee(salary: 10_000m, shiftId: 1));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Historical_Draft_Without_Coverage_Cannot_Be_Silently_Finalized()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var run = new PayrollRun { Year = 2024, Month = 11, Status = PayrollStatus.Computed };
        db.PayrollRuns.Add(run);
        db.PayrollItems.Add(new PayrollItem { PayrollRun = run, EmployeeId = 1, EmployeeCode = "1001", BasicSalary = 10_000m, NetSalary = 10_000m });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).FinalizeAsync(run.Id, "admin", "u1"));
        Assert.Contains("أعد حساب الكشف", error.Message);
        Assert.Equal(PayrollStatus.Computed, run.Status);
    }

    [Fact]
    public async Task Future_Dated_Charges_Affect_Projection_But_Not_Earned_Net_And_Sheet_Preserves_That_Separation()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var period = PayrollPeriod.ForMonth(2026, 11);
        db.DailyAttendances.Add(PayrollTestAttendance.Present(new DateTime(2026, 11, 10)));
        db.Deductions.Add(new Deduction { EmployeeId = 1, Year = 2026, Month = 11, DeductionDate = new DateTime(2026, 11, 20), Amount = 100, Status = ApprovalStatus.Approved });
        db.EmployeeExpenses.Add(new EmployeeExpense { EmployeeId = 1, Year = 2026, Month = 11, ExpenseDate = new DateTime(2026, 11, 20), Amount = 200, Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.DeductFromPayroll });
        await db.SaveChangesAsync();
        var service = new PayrollService(db, new SettingsService(db), new AuditService(db), NullLogger<PayrollService>.Instance, new FixedEarningsClock());
        var result = await service.GenerateAsync(period.Year, period.Month);
        var item = await db.PayrollItems.SingleAsync();
        Assert.Equal(300, item.TotalDeductions);
        Assert.Equal(9700, item.NetSalary);
        Assert.Equal(0, item.EarnedDeductions);
        var earned = item.EarnedNetSalary;
        var saved = await new PayrollSheetService(db, new SettingsService(db), new AuditService(db))
            .SaveAsync(result.PayrollRunId, [new() { PayrollItemId = item.Id, Bonuses = 50 }], false, "admin", "u1");
        Assert.True(Assert.Single(saved.Rows).Saved);
        Assert.Equal(0, item.EarnedDeductions);
        Assert.Equal(earned + 50, item.EarnedNetSalary);
    }

    private sealed class FixedEarningsClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task Current_Cycle_Shows_Only_Verified_Earned_Days_Separately_From_Projected_Contract()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var today = await CompanyPayrollClock.TodayAsync(db);
        var period = PayrollPeriod.FromDate(today);
        db.DailyAttendances.Add(PayrollTestAttendance.Present(period.Start));
        // Existing future evidence must be retained, but is not earned attendance yet.
        if (today < period.End) db.DailyAttendances.Add(PayrollTestAttendance.Present(period.End));
        await db.SaveChangesAsync();

        var result = await Service(db).GenerateAsync(period.Year, period.Month);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == result.PayrollRunId);
        var restDays = Enumerable.Range(0, (today - period.Start).Days + 1)
            .Select(offset => period.Start.AddDays(offset)).Count(d => d != period.Start && d.DayOfWeek == DayOfWeek.Friday);
        var missing = (today - period.Start).Days - restDays;
        Assert.Equal(1, item.AttendanceDays);
        Assert.Equal(10_000m, item.BasicSalary);
        Assert.Equal(10_000m, item.NetSalary);
        Assert.Equal(EarnedSalaryCalculator.Calculate(10_000m, item.DailyRate, 1, restDays, 0), item.EarnedBasicSalary);
        Assert.Equal(today, item.EarningsAsOf);
        Assert.Equal(missing, item.AttendanceMissingDays);
        Assert.Equal(0m, item.AbsenceDays);
        Assert.True(item.IsProvisional);
        if (missing > 0) Assert.Contains(result.Warnings, warning => warning.Contains("غير مكتملة"));
    }

    [Fact]
    public void One_Day_In_Incomplete_Evidence_Is_One_Earned_Day_Without_Invented_Absence()
    {
        var period = PayrollPeriod.ForMonth(2026, 11);
        var employee = TestDb.Employee(salary: 10_000m);
        var coverage = PayrollCoverage.Calculate(employee, period, new DateTime(2026, 11, 18),
            [PayrollTestAttendance.Present(new DateTime(2026, 11, 10))], [], new HashSet<DayOfWeek>(), new HashSet<DateTime>());
        Assert.Equal(1, coverage.WorkedDays);
        Assert.Equal(23, coverage.MissingDays);
        Assert.Equal(333.33m, EarnedSalaryCalculator.Calculate(employee.BasicSalary, employee.BasicSalary / 30m,
            coverage.WorkedDays, coverage.PaidRestDays, coverage.PaidLeaveDays));
    }

    [Fact]
    public async Task Processing_Future_Only_Range_Creates_No_Absences_Or_Attendance()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var tomorrow = (await CompanyPayrollClock.TodayAsync(db)).AddDays(1);
        var processing = new AttendanceProcessingService(db, new SettingsService(db), NullLogger<AttendanceProcessingService>.Instance);
        var result = await processing.ProcessAsync(tomorrow, tomorrow.AddDays(4));
        Assert.Equal(0, result.DaysProcessed);
        Assert.Equal(0, result.AbsentDays);
        Assert.Empty(await db.DailyAttendances.ToListAsync());
        Assert.Contains(result.Warnings, warning => warning.Contains("المستقبلية"));
    }

    [Fact]
    public async Task Processing_Elapsed_And_Future_Range_Never_Marks_Future_Dates_Absent()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var today = await CompanyPayrollClock.TodayAsync(db);
        var processing = new AttendanceProcessingService(db, new SettingsService(db), NullLogger<AttendanceProcessingService>.Instance);
        await processing.ProcessAsync(today, today.AddDays(3));
        Assert.All(await db.DailyAttendances.ToListAsync(), day => Assert.True(day.WorkDate <= today));
        Assert.False(await db.DailyAttendances.AnyAsync(d => d.WorkDate > today));
    }

    [Fact]
    public async Task Complete_Cycle_Finalizes_Directly_Once_Without_Review_Or_Approval_Stage()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var period = PayrollPeriod.ForMonth(2024, 11); // 31 real calendar days.
        await PayrollTestAttendance.CompleteAsync(db, period);
        var service = Service(db);
        var result = await service.GenerateAsync(period.Year, period.Month);
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == result.PayrollRunId);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == run.Id);
        Assert.Equal(PayrollStatus.Computed, run.Status);
        Assert.Equal(31, item.AttendanceDays);
        Assert.Equal(30m, item.PayableBasicDays);
        Assert.Equal(10_000m, item.BasicSalary);
        Assert.Equal(10_000m, item.EarnedBasicSalary);
        Assert.Equal(0, item.AttendanceMissingDays);

        await service.FinalizeAsync(run.Id, "HR", "hr-1");
        await service.FinalizeAsync(run.Id, "HR", "hr-1");
        Assert.Equal(PayrollStatus.Locked, run.Status);
        Assert.Equal(PayrollStatus.Locked, item.Status);
        Assert.False(item.IsProvisional);
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "إقفال كشف الرواتب").ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(period.Year, period.Month));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PayrollSheetService(db, new SettingsService(db), new AuditService(db))
            .SaveAsync(run.Id, [new() { PayrollItemId = item.Id, Bonuses = 100m }], false, "HR", "hr-1"));
    }

    [Fact]
    public async Task Closed_Cycle_With_Unknown_Days_Reports_Employee_And_Remains_Editable_Draft()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var period = PayrollPeriod.ForMonth(2024, 11);
        db.DailyAttendances.Add(PayrollTestAttendance.Present(period.Start));
        await db.SaveChangesAsync();
        var service = Service(db);
        var result = await service.GenerateAsync(period.Year, period.Month);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == result.PayrollRunId);
        Assert.True(item.AttendanceMissingDays > 0);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(result.PayrollRunId, "HR", "hr-1"));
        Assert.Contains("1001", error.Message);
        Assert.Contains("استكمل حضور", error.Message);
        Assert.Equal(PayrollStatus.Computed, (await db.PayrollRuns.FindAsync(result.PayrollRunId))!.Status);
        Assert.Empty(await db.AuditLogs.Where(a => a.Action == "إقفال كشف الرواتب").ToListAsync());
    }

    [Fact]
    public async Task Corrected_Early_Departure_Matches_Payroll_Report_Payslip_And_Excel()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var date = new DateTime(2024, 10, 30);
        var daily = PayrollTestAttendance.Present(date);
        daily.CheckInTime = date.AddHours(8).AddMinutes(3);
        db.DailyAttendances.Add(daily);
        await db.SaveChangesAsync();
        await new AttendanceCorrectionService(db, new AuditService(db)).CorrectAsync(new()
        {
            DailyAttendanceId = daily.Id, CheckOut = date.AddHours(12).AddMinutes(16),
            Reason = "خروج مؤكد من المشرف", Approve = true
        }, "HR", "hr-1");

        var result = await Service(db).GenerateAsync(2024, 11);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == result.PayrollRunId);
        Assert.Equal(63, item.LateMinutes);
        Assert.Equal(164, item.EarlyLeaveMinutes);
        Assert.Equal(43.75m, item.LateDeduction);
        Assert.Equal(113.89m, item.EarlyLeaveDeduction);
        var settings = new SettingsService(db);
        var reports = new ReportService(db, settings);
        var dayReport = Assert.Single(await reports.GetDailyAttendanceAsync(new() { From = date, To = date }));
        Assert.Equal(253, dayReport.WorkedMinutes);
        Assert.Equal(227, dayReport.MissingScheduledMinutes);
        Assert.Equal(164, dayReport.EarlyLeaveMinutes);
        var slip = await reports.GetPayslipAsync(item.Id);
        Assert.NotNull(slip);
        Assert.Equal(item.NetSalary, slip.Item.NetSalary);
        Assert.Equal(item.EarlyLeaveDeduction, slip.Item.EarlyLeaveDeduction);
        Assert.Contains("2024-10-26", slip.PeriodText);
        Assert.Contains("2024-11-25", slip.PeriodText);
        var excel = new ExcelExportService(db, settings, reports);
        using var workbook = new XLWorkbook(new MemoryStream(await excel.ExportPayslipAsync(item.Id)));
        var sheet = workbook.Worksheet("Payslip");
        var earlyLabel = sheet.Column(1).CellsUsed().Single(c => c.GetString().StartsWith("خصم") && c.GetString().Contains("مبكر"));
        Assert.Equal(item.EarlyLeaveDeduction, sheet.Cell(earlyLabel.Address.RowNumber, 2).GetValue<decimal>());
        var netLabel = sheet.Column(1).CellsUsed().Single(c => c.GetString().StartsWith("صافي الدورة"));
        Assert.Equal(item.NetSalary, sheet.Cell(netLabel.Address.RowNumber, 2).GetValue<decimal>());
    }

    [Fact]
    public async Task Scheduled_Loan_Without_Repayment_Start_Fails_Instead_Of_Deducting_Entire_Balance()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var period = PayrollPeriod.ForMonth(2024, 11);
        db.DailyAttendances.Add(PayrollTestAttendance.Present(period.Start));
        db.Loans.Add(new()
        {
            EmployeeId = 1, Amount = 6_000m, OutstandingBalance = 6_000m,
            Installments = 6, InstallmentAmount = 1_000m, LoanDate = period.Start,
            DeductionYear = period.Year, DeductionMonth = period.Month, Status = ApprovalStatus.Approved
        });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).GenerateAsync(period.Year, period.Month));
        Assert.Contains("بداية سداد", error.Message);
        Assert.Empty(await db.PayrollRuns.AsNoTracking().ToListAsync());
        Assert.Null((await db.Loans.AsNoTracking().SingleAsync()).PayrollItemId);
    }

    [Fact]
    public async Task Aggregate_Overtime_For_Future_Dates_Is_Not_Added_To_Current_Earned_Pay()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var today = await CompanyPayrollClock.TodayAsync(db);
        var period = PayrollPeriod.FromDate(today);
        db.DailyAttendances.Add(PayrollTestAttendance.Present(period.Start));
        var approval = new OvertimeApproval
        {
            EmployeeId = 1, PeriodStart = period.Start, PeriodEnd = period.End,
            ApprovedHours = 8m, CalculatedHours = 8m, Status = ApprovalStatus.Approved
        };
        db.OvertimeApprovals.Add(approval);
        await db.SaveChangesAsync();
        // The current cycle is unfinished except on its final day; force a future cycle then.
        if (today == period.End)
        {
            period = PayrollPeriod.FromDate(today.AddDays(1));
            approval.PeriodStart = period.Start;
            approval.PeriodEnd = period.End;
            db.DailyAttendances.Add(PayrollTestAttendance.Present(period.Start));
            await db.SaveChangesAsync();
        }
        var result = await Service(db).GenerateAsync(period.Year, period.Month);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == result.PayrollRunId);
        Assert.Equal(0m, item.ApprovedOvertimeHours);
        Assert.Equal(0m, item.OvertimeAmount);
        Assert.Null(approval.PayrollItemId);
    }

    [Fact]
    public async Task Regeneration_Keeps_Only_Manual_Expense_Portion_And_Adds_New_Expense_Exactly_Once()
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await SeedAsync(db);
        var period = PayrollPeriod.ForMonth(2024, 11);
        db.DailyAttendances.Add(PayrollTestAttendance.Present(period.Start));
        db.EmployeeExpenses.Add(new()
        {
            EmployeeId = 1, Year = period.Year, Month = period.Month, ExpenseDate = period.Start,
            Amount = 100m, Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.DeductFromPayroll
        });
        await db.SaveChangesAsync();
        var service = Service(db);
        var original = await service.GenerateAsync(period.Year, period.Month);
        var oldItem = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == original.PayrollRunId);
        var save = await new PayrollSheetService(db, new SettingsService(db), new AuditService(db))
            .SaveAsync(original.PayrollRunId, [new() { PayrollItemId = oldItem.Id, OtherDeductions = 150m }], false, "HR", "hr-1");
        Assert.True(Assert.Single(save.Rows).Saved);
        db.EmployeeExpenses.Add(new()
        {
            EmployeeId = 1, Year = period.Year, Month = period.Month, ExpenseDate = period.Start.AddDays(1),
            Amount = 200m, Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.DeductFromPayroll
        });
        await db.SaveChangesAsync();
        var refreshed = await service.GenerateAsync(period.Year, period.Month);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == refreshed.PayrollRunId);
        Assert.Equal(300m, item.EmployeeExpenseRecoveries);
        Assert.Equal(350m, item.OtherDeductions); // 50 manual + 100 original expense + 200 new expense.
        Assert.Equal(350m, item.TotalDeductions);
        var again = await service.GenerateAsync(period.Year, period.Month);
        Assert.Equal(350m, (await db.PayrollItems.SingleAsync(i => i.PayrollRunId == again.PayrollRunId)).OtherDeductions);
    }
}

internal static class PayrollTestAttendance
{
    public static DailyAttendance Present(DateTime date) => new()
    {
        EmployeeId = 1, WorkDate = date, ShiftId = 1,
        ShiftStart = date.AddHours(7), ShiftEnd = date.AddHours(15),
        CheckInTime = date.AddHours(7), CheckOutTime = date.AddHours(15),
        Status = AttendanceStatus.Present, PunchReviewStatus = ClassificationStatus.Classified
    };

    public static async Task CompleteAsync(AppDbContext db, PayrollPeriod period)
    {
        var known = await db.DailyAttendances.Where(d => d.EmployeeId == 1).Select(d => d.WorkDate).ToListAsync();
        var dates = known.Select(d => d.Date).ToHashSet();
        foreach (var pending in db.DailyAttendances.Local) dates.Add(pending.WorkDate.Date);
        for (var day = period.Start; day <= period.End; day = day.AddDays(1))
            if (dates.Add(day)) db.DailyAttendances.Add(Present(day));
        await db.SaveChangesAsync();
    }
}
