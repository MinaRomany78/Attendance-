using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HrPayroll.Tests;

/// <summary>
/// سياسة أهلية سلفة منتصف الشهر: الاستحقاق حتى يوم 15 من نفس الشهر (يوم 15 نفسه مسموح)،
/// ولا تُعتبر السلفة مدفوعة بمجرد الاعتماد، ولا يمكن تعديل سلفة مدفوعة.
/// </summary>
public class AdvanceEligibilityTests
{
    private static (TestDb Db, AppDbContext Ctx) Seed()
    {
        var db = new TestDb();
        var ctx = db.CreateContext();
        ctx.Shifts.Add(TestDb.FirstShift());
        ctx.Employees.Add(TestDb.Employee(salary: 9000m));
        ctx.SaveChanges();
        return (db, ctx);
    }

    private static SalaryAdvanceService Service(AppDbContext ctx)
        => new(ctx, new SettingsService(ctx), new AuditService(ctx),
            NullLogger<SalaryAdvanceService>.Instance);

    private static void AddWorkDays(AppDbContext ctx, DateTime from, DateTime to)
    {
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek == DayOfWeek.Friday) continue;
            ctx.DailyAttendances.Add(new DailyAttendance
            {
                EmployeeId = 1,
                WorkDate = day,
                ShiftId = 1,
                ShiftStart = day.AddHours(7),
                ShiftEnd = day.AddHours(15),
                CheckInTime = day.AddHours(7),
                CheckOutTime = day.AddHours(15),
                Status = AttendanceStatus.Present,
                PunchReviewStatus = ClassificationStatus.Classified
            });
        }
        ctx.SaveChanges();
    }

    [Theory]
    [InlineData(14, true)]   // قبل الاستحقاق: مسموح
    [InlineData(15, true)]   // يوم الاستحقاق نفسه: مسموح (سياسة الشركة)
    [InlineData(16, false)]  // بعد الاستحقاق: مرفوض
    [InlineData(20, false)]  // بعد الاستحقاق: مرفوض
    public async Task Cutoff_Day_Is_Enforced_Including_The_Fifteenth(int day, bool allowed)
    {
        var (db, ctx) = Seed();
        using var _ = db;

        var start = new DateTime(2024, 10, 1);
        AddWorkDays(ctx, start, start.AddDays(day - 1));

        var service = Service(ctx);
        var cutoff = new DateTime(2024, 10, day);

        if (allowed)
        {
            var advance = await service.CalculateAsync(1, 2024, 10, cutoff);
            Assert.Equal(cutoff, advance.CutoffDate);
            Assert.Equal(SalaryAdvanceStatus.Calculated, advance.Status);
            Assert.True(advance.ProposedAmount >= 0m);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CalculateAsync(1, 2024, 10, cutoff));
            Assert.Contains("15", ex.Message);
            Assert.Empty(await ctx.SalaryAdvances.ToListAsync());
        }
    }

    [Fact]
    public async Task Cutoff_Outside_The_Advance_Month_Is_Rejected()
    {
        var (db, ctx) = Seed();
        using var _ = db;
        AddWorkDays(ctx, new DateTime(2024, 10, 1), new DateTime(2024, 10, 15));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(ctx).CalculateAsync(1, 2024, 10, new DateTime(2024, 11, 10)));

        Assert.Contains("شهر السلفة", ex.Message);
        Assert.Empty(await ctx.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Approved_Advance_Is_Not_Paid_Automatically()
    {
        var (db, ctx) = Seed();
        using var _ = db;
        AddWorkDays(ctx, new DateTime(2024, 10, 1), new DateTime(2024, 10, 15));

        var service = Service(ctx);
        var advance = await service.CalculateAsync(1, 2024, 10, new DateTime(2024, 10, 15));
        await service.ApproveAsync(advance.Id, "tester", "u1");

        ctx.ChangeTracker.Clear();
        var approved = await ctx.SalaryAdvances.SingleAsync(a => a.Id == advance.Id);
        Assert.Equal(SalaryAdvanceStatus.Approved, approved.Status);
        Assert.Equal(0m, approved.PaidAmount);         // الاعتماد ليس دفعًا
        Assert.Null(approved.PaidAt);
        Assert.Equal("tester", approved.ApprovedBy);

        await service.MarkPaidAsync(advance.Id, approved.ApprovedAmount, "tester", "u1");

        ctx.ChangeTracker.Clear();
        var paid = await ctx.SalaryAdvances.SingleAsync(a => a.Id == advance.Id);
        Assert.Equal(SalaryAdvanceStatus.Paid, paid.Status);
        Assert.Equal(approved.ApprovedAmount, paid.PaidAmount);
        Assert.NotNull(paid.PaidAt);
    }

    [Fact]
    public async Task Paid_Advance_Cannot_Be_Reviewed_Or_Cancelled()
    {
        var (db, ctx) = Seed();
        using var _ = db;
        AddWorkDays(ctx, new DateTime(2024, 10, 1), new DateTime(2024, 10, 15));

        var service = Service(ctx);
        var advance = await service.CalculateAsync(1, 2024, 10, new DateTime(2024, 10, 15));
        await service.ApproveAsync(advance.Id, "tester", "u1");
        await service.MarkPaidAsync(advance.Id, advance.ApprovedAmount, "tester", "u1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReviewAsync(advance.Id, advance.ApprovedAmount, "محاولة تعديل بعد الدفع", "tester", "u1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CancelAsync(advance.Id, "محاولة إلغاء بعد الدفع", "tester", "u1"));

        ctx.ChangeTracker.Clear();
        var still = await ctx.SalaryAdvances.SingleAsync(a => a.Id == advance.Id);
        Assert.Equal(SalaryAdvanceStatus.Paid, still.Status);
    }

    [Fact]
    public async Task Two_Advances_In_Two_Different_Months_Are_Allowed()
    {
        var (db, ctx) = Seed();
        using var _ = db;
        AddWorkDays(ctx, new DateTime(2024, 10, 1), new DateTime(2024, 10, 15));
        AddWorkDays(ctx, new DateTime(2024, 11, 1), new DateTime(2024, 11, 15));

        var service = Service(ctx);
        await service.CalculateAsync(1, 2024, 10, new DateTime(2024, 10, 15));
        await service.CalculateAsync(1, 2024, 11, new DateTime(2024, 11, 15));

        Assert.Equal(2, await ctx.SalaryAdvances.CountAsync());
    }
}
