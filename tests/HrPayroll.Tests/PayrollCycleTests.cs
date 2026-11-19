using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
namespace HrPayroll.Tests;
public sealed class PayrollCycleTests
{
    private static PayrollService Service(AppDbContext db)=>new(db,new SettingsService(db),new AuditService(db),NullLogger<PayrollService>.Instance);
    private static DailyAttendance Day(DateTime date)=>new(){EmployeeId=1,WorkDate=date,ShiftId=1,ShiftStart=date.AddHours(7),ShiftEnd=date.AddHours(15),CheckInTime=date.AddHours(7),CheckOutTime=date.AddHours(15),Status=AttendanceStatus.Present,PunchReviewStatus=ClassificationStatus.Classified};
    private static async Task Seed(AppDbContext db){db.Shifts.Add(TestDb.FirstShift());db.Employees.Add(TestDb.Employee(salary:6000));await db.SaveChangesAsync();}
    [Theory]
    [InlineData(2026,11,"2026-10-26","2026-11-25",31)]
    [InlineData(2026,12,"2026-11-26","2026-12-25",30)]
    [InlineData(2027,1,"2026-12-26","2027-01-25",31)]
    [InlineData(2025,3,"2025-02-26","2025-03-25",28)]
    [InlineData(2024,3,"2024-02-26","2024-03-25",29)]
    public void Inclusive_Periods_Respect_Month_Length_And_Year_Transition(int year,int month,string start,string end,int count)
    {
        var p=PayrollPeriod.ForMonth(year,month);Assert.Equal(DateTime.Parse(start),p.Start);Assert.Equal(DateTime.Parse(end),p.End);Assert.Equal(count,p.CalendarDays);Assert.Equal($"{start}/{end}",p.Key);
        Assert.Equal(p,PayrollPeriod.FromDate(p.Start));Assert.Equal(p,PayrollPeriod.FromDate(p.End));Assert.NotEqual(p,PayrollPeriod.FromDate(p.End.AddDays(1)));
    }
    [Theory][InlineData("2026-10-31",2026,11)][InlineData("2026-11-24",2026,11)][InlineData("2026-11-26",2026,12)]
    public void Work_Dates_Map_To_The_Closing_Month(string date,int year,int month){var p=PayrollPeriod.FromDate(DateTime.Parse(date));Assert.Equal(year,p.Year);Assert.Equal(month,p.Month);}
    [Theory][InlineData(2025,3,28)][InlineData(2024,3,29)][InlineData(2025,12,30)][InlineData(2025,11,31)]
    public async Task All_Actual_Days_Are_Retained_While_Full_Basic_Salary_Stays_On_Thirty_Days(int year,int month,int days)
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);var p=PayrollPeriod.ForMonth(year,month);
        for(var d=p.Start;d<=p.End;d=d.AddDays(1))db.DailyAttendances.Add(Day(d));
        db.DailyAttendances.Add(Day(p.Start.AddDays(-1)));db.DailyAttendances.Add(Day(p.End.AddDays(1)));await db.SaveChangesAsync();
        var result=await Service(db).GenerateAsync(year,month);var item=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==result.PayrollRunId);var run=await db.PayrollRuns.FindAsync(result.PayrollRunId);
        Assert.Equal(p.Start,run!.PeriodStart);Assert.Equal(p.End,run.PeriodEnd);Assert.Equal(days,item.AttendanceDays);Assert.Equal(6000,item.BasicSalary);Assert.Equal(200,item.DailyRate);Assert.Equal(30,item.PayableBasicDays);Assert.Equal(6000,item.NetSalary);Assert.Equal(days+2,await db.DailyAttendances.CountAsync());
    }
    [Fact]
    public async Task Approved_Proration_Uses_Contract_Rate_And_Employment_Intersection()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);var p=PayrollPeriod.ForMonth(2024,11);var emp=await db.Employees.SingleAsync();emp.HireDate=new(2024,11,20);db.DailyAttendances.Add(Day(new(2024,11,20)));await db.SaveChangesAsync();
        var result=await Service(db).GenerateAsync(2024,11);var item=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==result.PayrollRunId);Assert.Equal(1200,item.BasicSalary);Assert.Equal(200,item.DailyRate);Assert.Equal(6000,item.ContractBasicSalary);Assert.Equal(6,item.PayableBasicDays);
    }
    [Fact]
    public async Task October_Advance_Is_Settled_Once_In_November_Cycle_With_Explicit_Negative_Net()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);var clock=new AdvanceTestClock(new(2024,10,30,18,0,0,TimeSpan.Zero));
        var s=new SalaryAdvanceService(db,new SettingsService(db),new AuditService(db),NullLogger<SalaryAdvanceService>.Instance,clock);
        db.DailyAttendances.Add(Day(new(2024,10,30)));await db.SaveChangesAsync();await s.SetPaymentWindowAsync(26,31,"admin","u1");
        await PayrollTestAttendance.CompleteAsync(db,PayrollPeriod.ForMonth(2024,11));
        var row=Assert.Single(await s.PrepareAsync(2024,11,new[]{new PreparedAdvanceEdit(1,7000,0)},"admin","u1"));Assert.Equal(new DateTime(2024,10,26),row.PeriodStart);Assert.Equal(0,row.PaidAmount);
        var first=await Service(db).GenerateAsync(2024,11);Assert.Equal(0,(await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==first.PayrollRunId)).SalaryAdvancesPaid);
        await s.ConfirmPreparedAsync(new[]{new PreparedAdvanceConfirmation(row.Id,row.Revision,7000,Guid.NewGuid())},"payer","u2");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(db).ApproveAsync(first.PayrollRunId,"admin","u1"));
        Assert.Equal(7000,(await s.GetBalanceAsync(1,new(2024,10,30))).PreviousPaid);
        var second=await Service(db).GenerateAsync(2024,11);var item=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==second.PayrollRunId);Assert.Equal(7000,item.SalaryAdvancesPaid);Assert.Equal(-1000,item.NetSalary);Assert.Equal(1000,item.UncoveredDeductions);
        var again=await Service(db).GenerateAsync(2024,11);var regenerated=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==again.PayrollRunId);Assert.Equal(-1000,regenerated.NetSalary);Assert.Equal(regenerated.Id,row.PayrollItemId);Assert.Single(await db.SalaryAdvances.ToListAsync());
        var sheet=new PayrollSheetService(db,new SettingsService(db),new AuditService(db));await sheet.SaveAsync(again.PayrollRunId,new[]{new PayrollRowEdit{PayrollItemId=regenerated.Id,Bonuses=100}},false,"admin","u1");Assert.Equal(-900,regenerated.NetSalary);
        await Service(db).ApproveAsync(again.PayrollRunId,"admin","u1");await Service(db).LockAsync(again.PayrollRunId,"admin","u1");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(db).GenerateAsync(2024,11));
        Assert.True(await FinancialPeriodGuard.OverlapsProtectedAsync(db,new(2024,10,31),new(2024,10,31)));Assert.False(await FinancialPeriodGuard.OverlapsProtectedAsync(db,new(2024,11,26),new(2024,11,26)));
    }
    [Fact]
    public async Task Corrected_Mina_Checkout_Refreshes_All_Measurements_And_Preserves_Raw_Punches()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);var date=new DateTime(2026,11,10);var daily=Day(date);daily.CheckInTime=date.AddHours(8).AddMinutes(3);db.DailyAttendances.Add(daily);
        db.RawPunches.AddRange(new RawPunch{EmployeeId=1,DeviceEmployeeCode="0001",PunchTime=daily.CheckInTime.Value,AttType="I",DedupHash="mina-in"},new RawPunch{EmployeeId=1,DeviceEmployeeCode="0001",PunchTime=date.AddHours(15),AttType="O",DedupHash="mina-out"});await db.SaveChangesAsync();
        var correction=new AttendanceCorrectionService(db,new AuditService(db));await correction.CorrectAsync(new(){DailyAttendanceId=daily.Id,CheckOut=date.AddHours(12).AddMinutes(16),Reason="Supervisor verified checkout",Approve=true},"admin","u1");
        Assert.Equal(63,daily.ActualLateMinutes);Assert.Equal(63,daily.LateMinutes);Assert.Equal(164,daily.EarlyLeaveMinutes);Assert.Equal(253,daily.WorkedMinutes);Assert.Equal(227,daily.MissingScheduledMinutes);Assert.Equal(1.05m,daily.ActualLateHours);
        var count=await db.AttendanceCorrections.CountAsync();await new AttendanceProcessingService(db,new SettingsService(db),NullLogger<AttendanceProcessingService>.Instance).ProcessAsync(date,date);
        Assert.Equal(164,daily.EarlyLeaveMinutes);Assert.Equal(253,daily.WorkedMinutes);Assert.Equal(count,await db.AttendanceCorrections.CountAsync());Assert.Equal(2,await db.RawPunches.CountAsync());Assert.Equal(date.AddHours(15),(await db.RawPunches.SingleAsync(p=>p.AttType=="O")).PunchTime);
    }
    [Fact]
    public async Task Historical_Calendar_Run_Is_Not_Reinterpreted_Or_Replaced()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);var legacy=new PayrollRun{Year=2024,Month=11,Version=1,Status=PayrollStatus.Computed,TotalNet=1234};db.PayrollRuns.Add(legacy);await db.SaveChangesAsync();Assert.Equal(new DateTime(2024,11,1),PayrollPeriod.ForRun(legacy).Start);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(db).GenerateAsync(2024,11));Assert.Equal(1234,legacy.TotalNet);Assert.Equal(PayrollStatus.Computed,legacy.Status);Assert.Single(await db.PayrollRuns.ToListAsync());
    }
    [Fact]
    public async Task Leave_Spanning_Two_Cycles_Is_Clipped_Using_The_Shared_Working_Day_Calendar()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);
        db.AppSettings.Add(new AppSetting{Key=SettingKeys.AllowPayrollWithoutAttendance,Value="true"});
        var leave=new LeaveRequest{EmployeeId=1,StartDate=new(2024,10,24),EndDate=new(2024,11,2),IsPaid=false,LeaveType=LeaveType.Unpaid,Status=ApprovalStatus.Approved,WorkingDays=8,DaysCount=10};
        db.LeaveRequests.Add(leave);await db.SaveChangesAsync();
        var october=await Service(db).GenerateAsync(2024,10);var one=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==october.PayrollRunId);Assert.Equal(1,one.UnpaidLeaveDays);Assert.Equal(200,one.UnpaidLeaveDeduction);Assert.Null(leave.PayrollItemId);
        var november=await Service(db).GenerateAsync(2024,11);var two=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==november.PayrollRunId);Assert.Equal(7,two.UnpaidLeaveDays);Assert.Equal(1400,two.UnpaidLeaveDeduction);Assert.Equal(two.Id,leave.PayrollItemId);
    }
    [Fact]
    public async Task Correction_Invalidates_Affected_Draft_Until_Explicit_Regeneration()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);var date=new DateTime(2024,10,30);var day=Day(date);db.DailyAttendances.Add(day);await db.SaveChangesAsync();
        var result=await Service(db).GenerateAsync(2024,11);var item=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==result.PayrollRunId);var old=item.NetSalary;
        await new AttendanceCorrectionService(db,new AuditService(db)).CorrectAsync(new(){DailyAttendanceId=day.Id,CheckIn=date.AddHours(8).AddMinutes(3),CheckOut=date.AddHours(12).AddMinutes(16),Reason="Verified attendance",Approve=true},"admin","u1");
        Assert.NotNull(item.FinancialReviewReason);Assert.Equal(old,item.NetSalary);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(db).ApproveAsync(result.PayrollRunId,"admin","u1"));
        var regenerated=await Service(db).GenerateAsync(2024,11);var refreshed=await db.PayrollItems.SingleAsync(i=>i.PayrollRunId==regenerated.PayrollRunId);Assert.Null(refreshed.FinancialReviewReason);Assert.Equal(63,refreshed.LateMinutes);Assert.Equal(26.25m,refreshed.LateDeduction);
    }
    [Fact]
    public async Task Future_Cycle_Cannot_Be_Approved_Before_Closing()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);var future=PayrollPeriod.ForMonth(DateTime.UtcNow.Year+1,11);db.DailyAttendances.Add(Day(future.Start));await db.SaveChangesAsync();
        var result=await Service(db).GenerateAsync(future.Year,future.Month);await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(db).ApproveAsync(result.PayrollRunId,"admin","u1"));
    }
    [Fact]
    public async Task Aggregate_Overtime_Crossing_A_Cycle_Is_Not_Silently_Discarded_Or_Allocated()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();await Seed(db);db.DailyAttendances.Add(Day(new(2024,10,30)));db.OvertimeApprovals.Add(new(){EmployeeId=1,PeriodStart=new(2024,10,1),PeriodEnd=new(2024,11,30),ApprovedHours=8,CalculatedHours=8,Status=ApprovalStatus.Approved});await db.SaveChangesAsync();
        var exception=await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(db).GenerateAsync(2024,11));Assert.Contains("حدود الدورة",exception.Message);Assert.Empty(await db.PayrollRuns.ToListAsync());
    }


    [Theory]
    [InlineData("deduction")]
    [InlineData("expense")]
    [InlineData("adjustment")]
    [InlineData("leave")]
    [InlineData("contract")]
    public async Task Changed_Financial_Evidence_Requires_Regeneration_Before_Editing_Or_Approval(string source)
    {
        using var fixture = new TestDb();
        await using var db = fixture.CreateContext();
        await Seed(db);
        db.DailyAttendances.Add(Day(new(2024,10,30)));
        await db.SaveChangesAsync();
        await PayrollTestAttendance.CompleteAsync(db,PayrollPeriod.ForMonth(2024,11));
        var service = Service(db);
        var result = await service.GenerateAsync(2024,11);
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == result.PayrollRunId);
        var item = await db.PayrollItems.SingleAsync(i => i.PayrollRunId == run.Id);
        Assert.Equal(64, run.FinancialEvidenceHash!.Length);
        var net = item.NetSalary;
        switch (source)
        {
            case "deduction":
                db.Deductions.Add(new() { EmployeeId=1, Year=2024, Month=11, Amount=100, DeductionDate=new(2024,10,30), Reason="Verified", Status=ApprovalStatus.Approved });
                break;
            case "expense":
                db.EmployeeExpenses.Add(new() { EmployeeId=1, Year=2024, Month=11, Amount=150, ExpenseDate=new(2024,10,30), Status=ApprovalStatus.Approved });
                break;
            case "adjustment":
                db.PayrollAdjustments.Add(new() { EmployeeId=1, Year=2024, Month=11, Amount=100, Type=AdjustmentType.Bonus, Description="Verified", Status=ApprovalStatus.Approved });
                break;
            case "leave":
                db.LeaveRequests.Add(new() { EmployeeId=1, StartDate=new(2024,10,31), EndDate=new(2024,10,31), LeaveType=LeaveType.Unpaid, IsPaid=false, WorkingDays=1, DaysCount=1, Status=ApprovalStatus.Approved });
                break;
            case "contract":
                (await db.Employees.SingleAsync()).BasicSalary = 6300;
                break;
        }
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(run.Id,"admin","u1"));
        var sheet = new PayrollSheetService(db,new SettingsService(db),new AuditService(db));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sheet.SaveAsync(run.Id,new[] { new PayrollRowEdit { PayrollItemId=item.Id, Bonuses=50 } },false,"admin","u1"));
        Assert.Equal(net,item.NetSalary);
        var refreshed = await service.GenerateAsync(2024,11);
        await service.ApproveAsync(refreshed.PayrollRunId,"admin","u1");
    }

}
