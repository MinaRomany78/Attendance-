using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HrPayroll.Tests;

/// <summary>
/// المتطلبات 9 و10 و11 و12: سلفة يوم 15 من محرك الرواتب، منع التكرار،
/// المصاريف لا تُخصم مرتين، أقساط القروض (تخطي/رصيد/منع ازدواج)،
/// وصيغة صافي الراتب، وحماية الكشف المقفل.
/// </summary>
public class FinancialDocumentsTests
{
    private static PayrollService Payroll(AppDbContext ctx)
        => new(ctx, new SettingsService(ctx), new AuditService(ctx), NullLogger<PayrollService>.Instance);

    private static SalaryAdvanceService Advances(AppDbContext ctx)
        => new(ctx, new SettingsService(ctx), new AuditService(ctx), NullLogger<SalaryAdvanceService>.Instance);

    private static async Task<(TestDb Db, AppDbContext Ctx)> SeedAsync(decimal salary = 9000m)
    {
        var db = new TestDb();
        var ctx = db.CreateContext();
        ctx.Shifts.Add(TestDb.FirstShift());
        ctx.Employees.Add(TestDb.Employee(salary: salary));
        await ctx.SaveChangesAsync();
        return (db, ctx);
    }

    private static void AddWorkDays(AppDbContext ctx, int employeeId, DateTime from, DateTime to)
    {
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Friday) continue;
            ctx.DailyAttendances.Add(new DailyAttendance
            {
                EmployeeId = employeeId,
                WorkDate = d,
                ShiftId = 1,
                ShiftStart = d.AddHours(7),
                ShiftEnd = d.AddHours(15),
                CheckInTime = d.AddHours(7),
                CheckOutTime = d.AddHours(15),
                Status = AttendanceStatus.Present,
                PunchReviewStatus = ClassificationStatus.Classified
            });
        }
    }

    [Fact]
    public async Task Advance_UsesPayrollEngine_AndCapsAtBasicSalary()
    {
        using var db = new TestDb();
        using var ctx = db.CreateContext();
        ctx.Shifts.Add(TestDb.FirstShift());
        ctx.Employees.Add(TestDb.Employee(salary: 9000m));
        await ctx.SaveChangesAsync();

        AddWorkDays(ctx, 1, new DateTime(2024, 10, 1), new DateTime(2024, 10, 15));
        await ctx.SaveChangesAsync();

        var svc = Advances(ctx);
        var advance = await svc.CalculateAsync(1, 2024, 10, new DateTime(2024, 10, 15));

        Assert.Equal(13, advance.AttendanceDays); // 1–15 أكتوبر 2024 ناقص يومي الجمعة (4 و11)
        Assert.True(advance.EarnedAmount > 0);
        Assert.True(advance.ProposedAmount <= 9000m);
        Assert.Equal(advance.ProposedAmount, advance.ApprovedAmount);
        Assert.Equal(SalaryAdvanceStatus.Calculated, advance.Status);
    }

    [Fact]
    public async Task Advance_DuplicatePeriod_IsRejected()
    {
        using var db = new TestDb();
        using var ctx = db.CreateContext();
        ctx.Shifts.Add(TestDb.FirstShift());
        ctx.Employees.Add(TestDb.Employee(salary: 9000m));
        await ctx.SaveChangesAsync();

        AddWorkDays(ctx, 1, new DateTime(2024, 10, 1), new DateTime(2024, 10, 15));
        await ctx.SaveChangesAsync();

        var svc = Advances(ctx);
        await svc.CalculateAsync(1, 2024, 10, new DateTime(2024, 10, 15));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CalculateAsync(1, 2024, 10, new DateTime(2024, 10, 15)));
    }

    [Fact]
    public async Task Advance_IncludedExpenses_ReduceProposal_AndNotDeductedTwice()
    {
        using var db = new TestDb();
        using var ctx = db.CreateContext();
        ctx.Shifts.Add(TestDb.FirstShift());
        ctx.Employees.Add(TestDb.Employee(salary: 9000m));
        await ctx.SaveChangesAsync();

        AddWorkDays(ctx, 1, new DateTime(2024, 10, 1), new DateTime(2024, 10, 15));
        ctx.EmployeeExpenses.Add(new EmployeeExpense
        {
            EmployeeId = 1, Amount = 500m, ExpenseDate = new DateTime(2024, 10, 5),
            Category = "مواصلات", Month = 10, Year = 2024,
            Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.IncludedInAdvance
        });
        await ctx.SaveChangesAsync();

        var svc = Advances(ctx);
        var advance = await svc.CalculateAsync(1, 2024, 10, new DateTime(2024, 10, 15));

        Assert.Equal(500m, advance.IncludedExpenses);
        // المقترح = min(الأساسي، الصافي) − المشمول: لا يُدفع المشمول مرتين.
        Assert.True(advance.ProposedAmount <= 9000m - 500m + 0.01m);
    }

    [Fact]
    public async Task Advance_ManualChange_RequiresReason_AndKeepsOriginal()
    {
        using var db = new TestDb();
        using var ctx = db.CreateContext();
        ctx.Shifts.Add(TestDb.FirstShift());
        ctx.Employees.Add(TestDb.Employee(salary: 9000m));
        await ctx.SaveChangesAsync();

        AddWorkDays(ctx, 1, new DateTime(2024, 10, 1), new DateTime(2024, 10, 15));
        await ctx.SaveChangesAsync();

        var svc = Advances(ctx);
        var advance = await svc.CalculateAsync(1, 2024, 10, new DateTime(2024, 10, 15));
        var original = advance.ProposedAmount;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReviewAsync(advance.Id, original + 100m, null));

        await svc.ReviewAsync(advance.Id, original + 100m, "ظرف عائلي معتمد");
        using var ctx2 = db.CreateContext();
        var updated = await ctx2.SalaryAdvances.FindAsync(advance.Id);
        Assert.NotNull(updated);
        Assert.Equal(original, updated.ProposedAmount); // الأصل محفوظ
        Assert.Equal(original + 100m, updated.ApprovedAmount);
        Assert.Equal("ظرف عائلي معتمد", updated.AdjustmentReason);
    }

    [Fact]
    public void PayrollNet_MatchesApprovedFormula_Example()
    {
        // مثال المتطلب: مستحق 10000 + استرداد سلفة 1500 + مصاريف 500 + قسط 1000 + خصم حضور 200 = 6800.
        var policy = new AttendancePolicy
        {
            WorkDaysPerMonth = 30, WorkHoursPerDay = 8,
            LateDeductionEnabled = true, AbsenceDeductionEnabled = true, UnpaidLeaveDeductionEnabled = true
        };
        var inputs = new PayrollInputs
        {
            BasicSalary = 10000m,
            Advances = 1500m + 1000m,       // استرداد سلفة + قسط قرض
            OtherDeductions = 500m + 200m,  // استرداد مصاريف + خصم حضور إضافي
        };
        var result = PayrollCalculator.Compute(inputs, policy);
        Assert.Equal(10000m, result.GrossEarnings);
        Assert.Equal(3200m, result.TotalDeductions);
        Assert.Equal(6800m, result.NetSalary);
    }

    [Fact]
    public async Task ScheduledLoan_DeductsInstallmentOnly_AndCreatesSingleRow()
    {
        var (db, ctx) = await SeedAsync();
        using (db)
        {
            AddWorkDays(ctx, 1, new DateTime(2024, 10, 1), new DateTime(2024, 10, 31));
            var loan = new Loan
            {
                EmployeeId = 1, Amount = 12000m, LoanDate = new DateTime(2024, 10, 1),
                DeductionMonth = 10, DeductionYear = 2024, Installments = 12,
                InstallmentAmount = 1000m, RepaymentStartYear = 2024, RepaymentStartMonth = 10,
                OutstandingBalance = 12000m, Status = ApprovalStatus.Approved
            };
            ctx.Loans.Add(loan);
            await ctx.SaveChangesAsync();

            var result = await Payroll(ctx).GenerateAsync(2024, 10, user: "test");
            Assert.Equal(1, result.ItemCount);

            using var ctx2 = db.CreateContext();
            var rows = await ctx2.LoanRepayments.Where(r => r.LoanId == 1).ToListAsync();
            Assert.Single(rows); // قسط واحد فقط — لا ازدواج
            Assert.Equal(1000m, rows[0].DueAmount);
            Assert.Equal(LoanRepaymentStatus.Collected, rows[0].Status);

            var savedLoan = await ctx2.Loans.FindAsync(loan.Id);
            Assert.NotNull(savedLoan);
            Assert.Equal(1000m, savedLoan.PaidAmount);
            Assert.Equal(11000m, savedLoan.OutstandingBalance);

            var item = await ctx2.PayrollItems.FirstAsync(i => i.PayrollRunId == result.PayrollRunId);
            Assert.Equal(1000m, item.LoansTotal); // القسط فقط — لا كامل المبلغ
        }
    }

    [Fact]
    public async Task SkippedInstallment_IsNotDeducted_AndStaysOutstanding()
    {
        var (db, ctx) = await SeedAsync();
        using (db)
        {
            AddWorkDays(ctx, 1, new DateTime(2024, 10, 1), new DateTime(2024, 10, 31));
            var loan = new Loan
            {
                EmployeeId = 1, Amount = 12000m, LoanDate = new DateTime(2024, 10, 1),
                DeductionMonth = 10, DeductionYear = 2024, Installments = 12,
                InstallmentAmount = 1000m, RepaymentStartYear = 2024, RepaymentStartMonth = 10,
                OutstandingBalance = 12000m, Status = ApprovalStatus.Approved
            };
            ctx.Loans.Add(loan);
            await ctx.SaveChangesAsync();
            ctx.LoanRepayments.Add(new LoanRepayment
            {
                LoanId = loan.Id, EmployeeId = 1, Year = 2024, Month = 10,
                DueAmount = 1000m, Status = LoanRepaymentStatus.Skipped, Notes = "تخطي معتمد"
            });
            await ctx.SaveChangesAsync();

            var result = await Payroll(ctx).GenerateAsync(2024, 10, user: "test");

            using var ctx2 = db.CreateContext();
            var item = await ctx2.PayrollItems.FirstAsync(i => i.PayrollRunId == result.PayrollRunId);
            Assert.Equal(0m, item.LoansTotal); // المتخطى لا يُخصم
            var savedLoan = await ctx2.Loans.FindAsync(loan.Id);
            Assert.NotNull(savedLoan);
            Assert.Equal(12000m, savedLoan.OutstandingBalance); // باقٍ في الرصيد
        }
    }

    [Fact]
    public async Task DeductibleExpenses_AreRecoveredOnce_InPayroll()
    {
        var (db, ctx) = await SeedAsync();
        using (db)
        {
            AddWorkDays(ctx, 1, new DateTime(2024, 10, 1), new DateTime(2024, 10, 31));
            ctx.EmployeeExpenses.Add(new EmployeeExpense
            {
                EmployeeId = 1, Amount = 500m, ExpenseDate = new DateTime(2024, 10, 5),
                Category = "علاج", Month = 10, Year = 2024,
                Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.DeductFromPayroll
            });
            ctx.EmployeeExpenses.Add(new EmployeeExpense
            {
                EmployeeId = 1, Amount = 500m, ExpenseDate = new DateTime(2024, 10, 6),
                Category = "مواصلات", Month = 10, Year = 2024,
                Status = ApprovalStatus.Approved, SettlementMode = ExpenseSettlementMode.PaidCash
            });
            await ctx.SaveChangesAsync();

            var result = await Payroll(ctx).GenerateAsync(2024, 10, user: "test");

            using var ctx2 = db.CreateContext();
            var item = await ctx2.PayrollItems.FirstAsync(i => i.PayrollRunId == result.PayrollRunId);
            Assert.Equal(500m, item.OtherDeductions); // القابل للخصم فقط — لا ازدواج
            var settled = await ctx2.EmployeeExpenses.CountAsync(e => e.PayrollItemId != null);
            Assert.Equal(1, settled);
        }
    }

    [Fact]
    public async Task LockedPayroll_CannotBeRegeneratedSilently()
    {
        var (db, ctx) = await SeedAsync();
        using (db)
        {
            AddWorkDays(ctx, 1, new DateTime(2024, 10, 1), new DateTime(2024, 10, 31));
            await ctx.SaveChangesAsync();
            ctx.GetType(); // إبقاء السياق

            var payroll = Payroll(ctx);
            var result = await payroll.GenerateAsync(2024, 10, user: "test");
            await payroll.ApproveAsync(result.PayrollRunId, "test", "test-id");
            await payroll.LockAsync(result.PayrollRunId, "test", "test-id");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Payroll(ctx).GenerateAsync(2024, 10, user: "test"));
        }
    }
