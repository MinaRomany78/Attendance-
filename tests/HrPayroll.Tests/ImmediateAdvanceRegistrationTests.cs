using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using HrPayroll.Web.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HrPayroll.Tests;

public sealed class ImmediateAdvanceRegistrationTests
{
    private static SalaryAdvanceService Advances(AppDbContext db, DateTimeOffset? now = null) =>
        new(db, new SettingsService(db), new AuditService(db), NullLogger<SalaryAdvanceService>.Instance,
            new AdvanceTestClock(now ?? new DateTimeOffset(2024, 10, 20, 12, 0, 0, TimeSpan.Zero)));
    private static PayrollService Payroll(AppDbContext db) =>
        new(db, new SettingsService(db), new AuditService(db), NullLogger<PayrollService>.Instance);
    private static async Task Seed(AppDbContext db)
    {
        db.Shifts.Add(TestDb.FirstShift());
        var employee = TestDb.Employee(salary: 6000); employee.HireDate = new(2024, 10, 14);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var date = new DateTime(2024, 10, 14);
        db.DailyAttendances.Add(new DailyAttendance
        {
            EmployeeId = 1, WorkDate = date, ShiftId = 1, ShiftStart = date.AddHours(7), ShiftEnd = date.AddHours(15),
            CheckInTime = date.AddHours(7), CheckOutTime = date.AddHours(15),
            Status = AttendanceStatus.Present, PunchReviewStatus = ClassificationStatus.Classified
        });
        await db.SaveChangesAsync();
    }
    private static async Task<SalaryAdvance> Save(SalaryAdvanceService s, decimal amount, long revision = 0) =>
        Assert.Single(await s.RegisterAsync(2024, 10, [new(1, amount, revision)], "hr-user", "u1"));

    [Fact]
    public async Task Save_Is_Immediately_Active_And_Does_Not_Record_Cash_Or_Approval()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var row = await Save(Advances(db), 5000);
        Assert.Equal(SalaryAdvanceStatus.Registered, row.Status); Assert.Equal(5000, row.RegisteredAmount);
        Assert.Equal(0, row.PaidAmount); Assert.Null(row.PaidAt); Assert.Null(row.PaidBy);
        Assert.Null(row.PaymentRequestId); Assert.Null(row.ApprovedAt); Assert.Null(row.ApprovedBy);
        Assert.Equal(new DateTime(2024, 9, 26), row.PeriodStart); Assert.Equal(new DateTime(2024, 10, 25), row.PeriodEnd);
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "تسجيل خصم سلفة").ToListAsync());
        Assert.Null(typeof(SalaryAdvancesController).GetMethod("ConfirmPayment"));
        Assert.Null(typeof(SalaryAdvancesController).GetMethod("MarkPaid"));
    }

    [Fact]
    public async Task Editing_Replaces_Amount_And_Unchanged_Save_Or_Retry_Does_Not_Duplicate()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var s = Advances(db); var row = await Save(s, 2000); var firstRevision = row.Revision;
        await Save(s, 4000, firstRevision); var revision = row.Revision;
        await Save(s, 4000, revision); await Save(s, 4000, firstRevision);
        Assert.Equal(4000, row.RegisteredAmount); Assert.Equal(revision, row.Revision);
        Assert.Single(await db.SalaryAdvances.ToListAsync());
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.Action == "تسجيل خصم سلفة"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(s, 5000, firstRevision));
    }

    [Fact]
    public async Task Above_Earned_And_After_Day_15_Are_Allowed_With_An_Audit_Warning()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var s = Advances(db); var balance = await s.GetBalanceAsync(1, new(2024, 10, 20));
        var row = await Save(s, balance.Available + 5000);
        Assert.Equal(balance.Available + 5000, row.RegisteredAmount);
        Assert.Contains("قيمة السلفة أكبر من المستحق الحالي", (await db.AuditLogs.SingleAsync()).NewValue!);
    }

    [Fact]
    public async Task Zero_Removes_Active_Deduction_With_History_And_Allows_Later_Registration()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var s = Advances(db); var row = await Save(s, 2000);
        await Save(s, 0, row.Revision);
        Assert.Equal(SalaryAdvanceStatus.Cancelled, row.Status); Assert.Equal(0, row.RegisteredAmount);
        Assert.Single(await db.SalaryAdvances.ToListAsync());
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "إلغاء خصم سلفة").ToListAsync());
        await Save(s, 1000, row.Revision); Assert.Equal(SalaryAdvanceStatus.Registered, row.Status);
        Assert.Equal(1000, row.RegisteredAmount); Assert.Single(await db.SalaryAdvances.ToListAsync());
    }

    [Theory]
    [InlineData(-1)] [InlineData(1.001)]
    public async Task Invalid_Amounts_Are_Rejected(decimal amount)
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Advances(db), amount));
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Theory]
    [InlineData(PayrollStatus.Approved)] [InlineData(PayrollStatus.Locked)]
    public async Task Finalized_Period_Prevents_Register_Edit_And_Zero_Removal(PayrollStatus status)
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var s = Advances(db); var row = await Save(s, 2000);
        var period = PayrollPeriod.ForMonth(2024, 10);
        db.PayrollRuns.Add(new() { Year = 2024, Month = 10, Version = 1, Status = status, PeriodStart = period.Start, PeriodEnd = period.End });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(s, 4000, row.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(s, 0, row.Revision));
        Assert.Equal(2000, row.RegisteredAmount);
    }

    [Fact]
    public async Task Current_Cycle_Uses_26th_Boundary_And_History_Is_Read_Only()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var s = Advances(db, new DateTimeOffset(2024, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var row = Assert.Single(await s.RegisterAsync(2024, 11, [new(1, 4000, 0)], "hr", "u1"));
        Assert.Equal(new DateTime(2024, 10, 26), row.PeriodStart); Assert.Equal(new DateTime(2024, 11, 25), row.PeriodEnd);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(s, 1000));
    }

    [Fact]
    public async Task Saved_Deduction_Is_Automatic_And_Recalculation_Does_Not_Duplicate()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var row = await Save(Advances(db), 2000); var payroll = Payroll(db);
        var first = await payroll.GenerateAsync(2024, 10, "hr", "u1");
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == first.PayrollRunId);
        Assert.Equal(2000, item.SalaryAdvancesPaid); Assert.Equal(2000, item.LoansTotal);
        Assert.Equal(SalaryAdvanceStatus.Registered, row.Status); Assert.Equal(0, row.PaidAmount);
        var second = await payroll.GenerateAsync(2024, 10, "hr", "u1");
        var again = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == second.PayrollRunId);
        Assert.Equal(2000, again.SalaryAdvancesPaid); Assert.Equal(again.Id, row.PayrollItemId);
        Assert.Single(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Editing_A_Draft_Linked_Advance_Invalidates_Evidence_Until_Refresh()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var s = Advances(db); var row = await Save(s, 2000); var payroll = Payroll(db);
        var first = await payroll.GenerateAsync(2024, 10, "hr", "u1");
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == first.PayrollRunId);
        await Save(s, 4000, row.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PayrollFinancialEvidence.EnsureCurrentAsync(db, run));
        var next = await payroll.GenerateAsync(2024, 10, "hr", "u1");
        var nextRun = await db.PayrollRuns.SingleAsync(r => r.Id == next.PayrollRunId);
        await PayrollFinancialEvidence.EnsureCurrentAsync(db, nextRun);
        Assert.Equal(4000, (await db.PayrollItems.SingleAsync(i => i.PayrollRunId == nextRun.Id)).SalaryAdvancesPaid);
    }

    [Fact]
    public async Task Recorded_Cash_Payment_Is_Not_Overwritten_As_An_Editable_Deduction()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var period = PayrollPeriod.ForMonth(2024, 10);
        var paid = new SalaryAdvance { EmployeeId = 1, Year = 2024, Month = 10, PeriodStart = period.Start, PeriodEnd = period.End,
            CycleKey = SalaryAdvanceService.MonthlyCycle, Status = SalaryAdvanceStatus.Paid, PaidAmount = 2000, PaidAt = new(2024, 10, 15), PaidBy = "cashier" };
        db.SalaryAdvances.Add(paid); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Advances(db), 4000));
        Assert.Equal(2000, paid.PaidAmount); Assert.Equal("cashier", paid.PaidBy); Assert.Null(paid.RegisteredAmount);
    }

    [Fact]
    public async Task Balance_And_Payroll_Earned_Net_Include_The_Same_Credits_And_Loan_Deductions()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext();
        var settings = new SettingsService(db);
        var today = await CompanyPayrollClock.TodayAsync(db);
        var period = PayrollPeriod.FromDate(today);
        var day = period.Start;
        db.Shifts.Add(TestDb.FirstShift());
        var employee = TestDb.Employee(salary: 6000); employee.HireDate = period.Start;
        db.Employees.Add(employee); await db.SaveChangesAsync();
        db.DailyAttendances.Add(new()
        {
            EmployeeId = 1, WorkDate = day, ShiftId = 1, ShiftStart = day.AddHours(7), ShiftEnd = day.AddHours(15),
            CheckInTime = day.AddHours(7), CheckOutTime = day.AddHours(15), Status = AttendanceStatus.Present,
            PunchReviewStatus = ClassificationStatus.Classified
        });
        db.AppSettings.Add(new() { Key = SettingKeys.MonthlySalaryIncludesPaidRestDays, Value = "false" });
        db.PayrollAdjustments.AddRange(
            new() { EmployeeId = 1, Year = period.Year, Month = period.Month, Type = AdjustmentType.Bonus, Amount = 500, Status = ApprovalStatus.Approved },
            new() { EmployeeId = 1, Year = period.Year, Month = period.Month, Type = AdjustmentType.Allowance, Amount = 50, Status = ApprovalStatus.Approved },
            new() { EmployeeId = 1, Year = period.Year, Month = period.Month, Type = AdjustmentType.AdditionalDays, Days = 0.5m, DailyRateOverride = 100, Status = ApprovalStatus.Approved },
            new() { EmployeeId = 1, Year = period.Year, Month = period.Month, Type = AdjustmentType.OtherDeduction, Amount = 25, Status = ApprovalStatus.Approved });
        db.Deductions.Add(new() { EmployeeId = 1, Year = period.Year, Month = period.Month, DeductionDate = day, Amount = 20, Status = ApprovalStatus.Approved });
        db.EmployeeExpenses.AddRange(
            new() { EmployeeId = 1, Year = period.Year, Month = period.Month, ExpenseDate = day, Amount = 10, Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.DeductFromPayroll },
            new() { EmployeeId = 1, Year = period.Year, Month = period.Month, ExpenseDate = day, Amount = 999, Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.IncludedInAdvance },
            new() { EmployeeId = 1, Year = period.Year, Month = period.Month, ExpenseDate = day, Amount = 888, Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.PaidCash });
        db.Loans.AddRange(
            new() { EmployeeId = 1, Amount = 100, LoanDate = day, DeductionYear = period.Year, DeductionMonth = period.Month, Status = ApprovalStatus.Approved },
            new() { EmployeeId = 1, Amount = 1000, OutstandingBalance = 1000, Installments = 10, InstallmentAmount = 100, LoanDate = day,
                RepaymentStartYear = period.Year, RepaymentStartMonth = period.Month, Status = ApprovalStatus.Approved });
        await db.SaveChangesAsync();
        var service = new SalaryAdvanceService(db, settings, new AuditService(db), NullLogger<SalaryAdvanceService>.Instance);
        var before = await service.GetBalanceAsync(1, today);
        Assert.Equal(800, before.Earned); Assert.Equal(255, before.KnownDeductions); Assert.Equal(545, before.Available);
        Assert.Empty(await db.LoanRepayments.ToListAsync()); // Merely loading the page never creates installments.
        var generated = await Payroll(db).GenerateAsync(period.Year, period.Month);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == generated.PayrollRunId);
        Assert.Equal(item.EarnedNetSalary, before.Available);
        Assert.Equal(before.Available, (await service.GetBalanceAsync(1, today)).Available);
    }

    [Fact]
    public async Task Unpaid_Or_Cancelled_History_Is_Not_A_Fake_Cash_Recovery()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var period = PayrollPeriod.ForMonth(2024, 10);
        db.SalaryAdvances.Add(new() { EmployeeId = 1, Year = 2024, Month = 10, PeriodStart = period.Start, PeriodEnd = period.End,
            Status = SalaryAdvanceStatus.Cancelled, PaidAmount = 500 });
        await db.SaveChangesAsync();
        Assert.Equal(0, (await Advances(db).GetBalanceAsync(1, new(2024, 10, 20))).PreviousPaid);
    }

    [Fact]
    public async Task Explicit_Legacy_Mapping_Preserves_Cash_Evidence_And_Is_Audited_Once()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var paidAt = new DateTime(2024, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var paymentRequest = Guid.NewGuid();
        var legacy = new SalaryAdvance { EmployeeId = 1, Year = 2024, Month = 10, Status = SalaryAdvanceStatus.Paid,
            PaidAmount = 1234, PaidAt = paidAt, PaidBy = "original-cashier", PaymentRequestId = paymentRequest, CutoffDate = paidAt.Date,
            ApprovedBy = "original-approver", ApprovedAmount = 1234, Notes = "original accounting evidence" };
        db.SalaryAdvances.Add(legacy); await db.SaveChangesAsync();
        var s = Advances(db);
        await s.MapLegacyPeriodAsync(legacy.Id, 2024, 11, "Reconciled cash voucher and payroll cutover", "admin", "u1");
        await s.MapLegacyPeriodAsync(legacy.Id, 2024, 11, "Reconciled cash voucher and payroll cutover", "admin", "u1");
        Assert.Equal(new DateTime(2024, 10, 26), legacy.PeriodStart); Assert.Equal(new DateTime(2024, 11, 25), legacy.PeriodEnd);
        Assert.Equal(11, legacy.Month); Assert.Equal(1234, legacy.PaidAmount); Assert.Equal(paidAt, legacy.PaidAt);
        Assert.Equal("original-cashier", legacy.PaidBy); Assert.Equal(paymentRequest, legacy.PaymentRequestId);
        Assert.Equal("original-approver", legacy.ApprovedBy); Assert.Equal(1234, legacy.ApprovedAmount);
        Assert.Equal(paidAt.Date, legacy.CutoffDate); Assert.Equal("original accounting evidence", legacy.Notes);
        Assert.Null(legacy.RegisteredAmount); Assert.Equal(SalaryAdvanceStatus.Paid, legacy.Status);
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "ربط سلفة تاريخية بدورة الرواتب").ToListAsync());
    }

    [Fact]
    public async Task Legacy_Mapping_Rejects_A_Target_With_An_Active_Registered_Deduction()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var legacy = new SalaryAdvance { EmployeeId = 1, Year = 2024, Month = 9, PaidAmount = 1000, Status = SalaryAdvanceStatus.Paid };
        db.SalaryAdvances.Add(legacy); await db.SaveChangesAsync();
        await Save(Advances(db), 1000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Advances(db).MapLegacyPeriodAsync(legacy.Id, 2024, 10,
            "Reviewed legacy payment voucher", "admin", "u1"));
        Assert.Null(legacy.PeriodStart); Assert.Equal(9, legacy.Month); Assert.Equal(1000, legacy.PaidAmount);
    }

    [Fact]
    public async Task Legacy_Mapping_Requires_Reason_And_Protects_Original_Closed_Period()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var legacy = new SalaryAdvance { EmployeeId = 1, Year = 2024, Month = 10, PaidAmount = 1000, Status = SalaryAdvanceStatus.Paid };
        db.SalaryAdvances.Add(legacy); await db.SaveChangesAsync();
        var s = Advances(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MapLegacyPeriodAsync(legacy.Id, 2024, 12, "", "admin", "u1"));
        db.PayrollRuns.Add(new() { Year = 2024, Month = 10, Version = 1, Status = PayrollStatus.Locked });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MapLegacyPeriodAsync(legacy.Id, 2024, 12,
            "Reviewed legacy payment voucher", "admin", "u1"));
        Assert.Null(legacy.PeriodStart); Assert.Equal(10, legacy.Month);
    }

    [Fact]
    public async Task Legacy_Mapping_Does_Not_Transform_Settled_Or_Unpaid_Records()
    {
        using var fixture = new TestDb(); await using var db = fixture.CreateContext(); await Seed(db);
        var legacy = new SalaryAdvance { EmployeeId = 1, Year = 2024, Month = 10, PaidAmount = 1000, Status = SalaryAdvanceStatus.Settled };
        db.SalaryAdvances.Add(legacy); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Advances(db).MapLegacyPeriodAsync(legacy.Id, 2024, 11,
            "Reviewed legacy payment voucher", "admin", "u1"));
        Assert.Equal(SalaryAdvanceStatus.Settled, legacy.Status); Assert.Null(legacy.PeriodStart);
    }
}
