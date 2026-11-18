using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
namespace HrPayroll.Tests;
public sealed class AdvanceTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class PreparedAdvanceRegisterTests
{
    private static SalaryAdvanceService Service(AppDbContext db, AdvanceTestClock? clock = null) => new(db,new SettingsService(db),new AuditService(db),NullLogger<SalaryAdvanceService>.Instance,
        clock ?? new AdvanceTestClock(new DateTimeOffset(2024,10,15,10,0,0,TimeSpan.Zero)));
    private static void Seed(AppDbContext db)
    {
        db.Shifts.Add(TestDb.FirstShift()); db.Employees.Add(TestDb.Employee(salary:6000));db.SaveChanges();
        db.DailyAttendances.Add(new(){EmployeeId=1,WorkDate=new(2024,10,14),ShiftId=1,ShiftStart=new(2024,10,14,7,0,0),ShiftEnd=new(2024,10,14,15,0,0),CheckInTime=new(2024,10,14,7,0,0),CheckOutTime=new(2024,10,14,15,0,0),Status=AttendanceStatus.Present,PunchReviewStatus=ClassificationStatus.Classified});db.SaveChanges();
    }
    private static Task<IReadOnlyList<SalaryAdvance>> Prepare(SalaryAdvanceService s,decimal amount,long revision=0) => s.PrepareAsync(2024,10,new[]{new PreparedAdvanceEdit(1,amount,revision)},"tester","u1");
    [Theory][InlineData(100)][InlineData(200)][InlineData(5000)]
    public async Task Prepare_Below_Equal_Or_Above_Earned_Preserves_Amount_And_Is_Unpaid(decimal amount)
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);var s=Service(db);
        var row=Assert.Single(await Prepare(s,amount));Assert.Equal(800,row.EarnedAmount);Assert.Equal(amount,row.PreparedAmount);
        Assert.Equal(SalaryAdvanceStatus.Prepared,row.Status);Assert.Equal(0,row.PaidAmount);Assert.Null(row.PaidAt);Assert.NotNull(row.PreparedAt);Assert.Equal("tester",row.PreparedBy);
        await Prepare(s,amount);Assert.Single(await db.SalaryAdvances.ToListAsync());
        if(amount>200)Assert.Contains("قيمة السلفة أكبر من المستحق الحالي",(await db.AuditLogs.OrderByDescending(a=>a.Id).FirstAsync()).NewValue!);
    }
    [Fact]
    public async Task Prepared_Amounts_Are_Editable_With_Revision_Checks()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);var s=Service(db);var row=Assert.Single(await Prepare(s,100));var old=row.Revision;
        await Prepare(s,5000,old);Assert.Equal(5000,row.PreparedAmount);Assert.Equal(0,row.PaidAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Prepare(s,6000,old));Assert.Equal(1,await db.SalaryAdvances.CountAsync());
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(1.001)]
    public async Task Invalid_Amounts_Are_Rejected(decimal amount)
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Prepare(Service(db),amount));Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }
    [Fact]
    public async Task Invalid_Employee_Is_Rejected()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Prepare(Service(db),100));
    }
    [Theory][InlineData(14,false)][InlineData(15,true)][InlineData(16,false)]
    public async Task Preparation_Is_Allowed_Before_Window_But_Payment_Defaults_To_Day_15(int day,bool allowed)
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);var clock=new AdvanceTestClock(new(2024,10,day,10,0,0,TimeSpan.Zero));var s=Service(db,clock);
        var row=Assert.Single(await Prepare(s,5000));var c=new[]{new PreparedAdvanceConfirmation(row.Id,row.Revision,5000,Guid.NewGuid())};
        if(allowed){await s.ConfirmPreparedAsync(c,"payer","u2");Assert.Equal(5000,row.PaidAmount);Assert.Equal(clock.Now.UtcDateTime,row.PaidAt);Assert.Equal("payer",row.PaidBy);}
        else{await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ConfirmPreparedAsync(c,"payer","u2"));Assert.Equal(0,row.PaidAmount);}
    }
    [Fact]
    public async Task Configurable_Window_And_Duplicate_Confirmation_Are_Audited()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);var clock=new AdvanceTestClock(new(2024,10,20,10,0,0,TimeSpan.Zero));var s=Service(db,clock);
        await s.SetPaymentWindowAsync(18,22,"admin","u1");var row=Assert.Single(await Prepare(s,5000));var c=new[]{new PreparedAdvanceConfirmation(row.Id,row.Revision,5000,Guid.NewGuid())};
        await s.ConfirmPreparedAsync(c,"payer","u2");await s.ConfirmPreparedAsync(c,"payer","u2");Assert.Equal(5000,row.PaidAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ConfirmPreparedAsync(new[]{c[0] with{RequestId=Guid.NewGuid()}},"payer","u2"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Prepare(s,6000,row.Revision));Assert.Single(await db.SalaryAdvances.ToListAsync());
        Assert.Single(await db.AuditLogs.Where(a=>a.Action=="تأكيد صرف سلفة معدة").ToListAsync());
    }
    [Fact]
    public async Task Unpaid_Preparation_Is_Excluded_And_Payment_Settles_Once_In_The_Same_Month()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);var s=Service(db);var row=Assert.Single(await Prepare(s,1500));
        var payroll=new PayrollService(db,new SettingsService(db),new AuditService(db),NullLogger<PayrollService>.Instance);
        var first=await payroll.GenerateAsync(2024,10,"tester","u1");Assert.Equal(0,(await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==first.PayrollRunId)).SalaryAdvancesPaid);
        await s.ConfirmPreparedAsync(new[]{new PreparedAdvanceConfirmation(row.Id,row.Revision,1500,Guid.NewGuid())},"payer","u2");
        var second=await payroll.GenerateAsync(2024,10,"tester","u1");var paid=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==second.PayrollRunId);Assert.Equal(1500,paid.SalaryAdvancesPaid);Assert.Equal(4500,paid.NetSalary);
        var third=await payroll.GenerateAsync(2024,10,"tester","u1");var regenerated=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==third.PayrollRunId);Assert.Equal(1500,regenerated.SalaryAdvancesPaid);Assert.Equal(regenerated.Id,row.PayrollItemId);
        var sheet=new PayrollSheetService(db,new SettingsService(db),new AuditService(db));await Assert.ThrowsAsync<InvalidOperationException>(()=>sheet.SaveAsync(third.PayrollRunId,new[]{new PayrollRowEdit{PayrollItemId=regenerated.Id,Advances=3000}},false,"tester","u1"));
    }
    [Fact]
    public async Task Locked_Period_And_Changed_Saved_Amount_Block_Payment()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);var s=Service(db);var row=Assert.Single(await Prepare(s,100));var old=row.Revision;
        await Prepare(s,200,old);await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ConfirmPreparedAsync(new[]{new PreparedAdvanceConfirmation(row.Id,old,100,Guid.NewGuid())},"tester","u1"));
        db.PayrollRuns.Add(new(){Year=2024,Month=10,Version=1,Status=PayrollStatus.Locked});await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ConfirmPreparedAsync(new[]{new PreparedAdvanceConfirmation(row.Id,row.Revision,200,Guid.NewGuid())},"tester","u1"));Assert.Equal(0,row.PaidAmount);
    }
    [Fact]
    public async Task Concurrent_Prepare_And_Confirm_Preserve_One_Cycle_And_One_Payment()
    {
        var connectionString=$"Data Source=prepared-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=2";
        await using var keeper=new Microsoft.Data.Sqlite.SqliteConnection(connectionString);await keeper.OpenAsync();
        AppDbContext Context()=>new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);
        await using(var seed=Context()){await seed.Database.EnsureCreatedAsync();Seed(seed);}
        async Task Race(Func<AppDbContext,Task> action)
        {
            var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Attempt()=>Task.Run(async()=>{await start.Task;await using var db=Context();try{await action(db);}catch(Exception e)when(e is InvalidOperationException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException){} });
            var first=Attempt();var second=Attempt();start.SetResult();await Task.WhenAll(first,second);
        }
        await Race(async db=>await Prepare(Service(db),5000));
        await using(var db=Context()){var row=await db.SalaryAdvances.SingleAsync();Assert.Equal(5000,row.PreparedAmount);Assert.Equal(0,row.PaidAmount);}
        PreparedAdvanceConfirmation payment;
        await using(var db=Context()){var row=await db.SalaryAdvances.SingleAsync();payment=new(row.Id,row.Revision,5000,Guid.NewGuid());}
        await Race(db=>Service(db).ConfirmPreparedAsync(new[]{payment},"payer","u1"));
        await using(var db=Context()){var row=await db.SalaryAdvances.SingleAsync();Assert.Equal(5000,row.PaidAmount);Assert.Single(await db.AuditLogs.Where(a=>a.Action=="تأكيد صرف سلفة معدة").ToListAsync());}
    }
    [Fact]
    public async Task Partial_Payment_And_Cross_Month_Payment_Are_Explicitly_Rejected()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);var clock=new AdvanceTestClock(new(2024,10,15,10,0,0,TimeSpan.Zero));var s=Service(db,clock);
        var row=Assert.Single(await Prepare(s,5000));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ConfirmPreparedAsync(new[]{new PreparedAdvanceConfirmation(row.Id,row.Revision,3000,Guid.NewGuid())},"payer","u1"));
        clock.Now=new(2024,11,15,10,0,0,TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ConfirmPreparedAsync(new[]{new PreparedAdvanceConfirmation(row.Id,row.Revision,5000,Guid.NewGuid())},"payer","u1"));Assert.Equal(0,row.PaidAmount);Assert.Null(row.PayrollItemId);
    }

}
