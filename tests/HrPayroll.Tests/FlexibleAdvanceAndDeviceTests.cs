using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Infrastructure.Devices;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
namespace HrPayroll.Tests;
public sealed class FlexibleAdvanceAndDeviceTests
{
    private static SalaryAdvanceService Advances(HrPayroll.Infrastructure.Persistence.AppDbContext db) =>
        new(db, new SettingsService(db), new AuditService(db), NullLogger<SalaryAdvanceService>.Instance);
    private static void Seed(HrPayroll.Infrastructure.Persistence.AppDbContext db)
    {
        db.Shifts.Add(TestDb.FirstShift()); db.Employees.Add(TestDb.Employee(salary: 6000)); db.SaveChanges();
        db.DailyAttendances.Add(new() { EmployeeId = 1, WorkDate = new(2024,10,14), ShiftId = 1,
            ShiftStart = new(2024,10,14,7,0,0), ShiftEnd = new(2024,10,14,15,0,0),
            CheckInTime = new(2024,10,14,7,0,0), CheckOutTime = new(2024,10,14,15,0,0), Status = AttendanceStatus.Present,
            PunchReviewStatus = ClassificationStatus.Classified }); db.SaveChanges();
    }
    private sealed class Driver(bool exitOffline=false, bool conflict=false, bool overnight=false) : IZkDeviceClient
    {
        public bool Supports(AttendanceDevice d)=>d.Model=="Fixture";
        public Task<ZkConnectionResult> TestConnectionAsync(ZkDeviceConnection d,CancellationToken ct)=>Task.FromResult(new ZkConnectionResult(true,"fixture"));
        public Task<ZkDeviceInfo> GetDeviceInfoAsync(ZkDeviceConnection d,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ZkEmployeeRecord>> DownloadEmployeesAsync(ZkDeviceConnection d,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ZkSyncStatus> GetLastSyncStatusAsync(ZkDeviceConnection d,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<ZkAttendanceRecord>> DownloadAttendanceAsync(ZkDeviceConnection d,CancellationToken ct)
        {
            if(exitOffline && d.Identifier=="exit") throw new IOException("Offline fixture");
            return Task.FromResult<IReadOnlyList<ZkAttendanceRecord>>(new[] {new ZkAttendanceRecord(d.Identifier=="entry"?"01":"99",new DateTime(2024,10,14,d.Identifier=="entry"?(overnight?23:7):(overnight?7:15),0,0).AddDays(overnight && d.Identifier=="exit" ? 1 : 0),conflict?"O":null)});
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Two_Devices_Use_Explicit_Mappings_Roles_And_Independent_Outcomes(bool exitOffline)
    {
        using var fixture=new TestDb(); await using var db=fixture.CreateContext();
        db.Shifts.Add(TestDb.FirstShift());db.Employees.Add(TestDb.Employee());
        var entry=new AttendanceDevice {Name="Entry",Identifier="entry",Role=AttendanceDeviceRole.Entry,Model="Fixture"};
        var exit=new AttendanceDevice {Name="Exit",Identifier="exit",Role=AttendanceDeviceRole.Exit,Model="Fixture"};
        db.AttendanceDevices.AddRange(entry,exit);await db.SaveChangesAsync();
        var service=new AttendanceDeviceService(db,new EphemeralDataProtectionProvider(),new[]{new Driver(exitOffline)},new AuditService(db));
        await service.MapEmployeeAsync(entry.Id,"01",1,"tester",default);await service.MapEmployeeAsync(exit.Id,"99",1,"tester",default);
        var import=new AttendanceImportService(db,NullLogger<AttendanceImportService>.Instance);
        var processing=new AttendanceProcessingService(db,new SettingsService(db),NullLogger<AttendanceProcessingService>.Instance);
        var results=await service.SynchronizeAsync(null,import,processing,"u1","tester",default);
        Assert.Equal(2,results.Count);Assert.True(results[0].Successful);Assert.Equal(!exitOffline,results[1].Successful);
        Assert.Equal(exitOffline?1:2,await db.RawPunches.CountAsync());Assert.Equal(1,await db.Employees.CountAsync());
        Assert.All(await db.RawPunches.ToListAsync(),p=> {Assert.Null(p.AttType);Assert.NotNull(p.NormalizedAttType);Assert.NotNull(p.OriginalFieldsJson);});
        var repeated=await service.SynchronizeAsync(null,import,processing,"u1","tester",default);
        Assert.Equal(exitOffline?1:2,repeated.Sum(r=>r.Duplicates));Assert.Equal(0,repeated.Sum(r=>r.Inserted));
        if(!exitOffline){var attendance=await db.DailyAttendances.SingleAsync(d=>d.WorkDate==new DateTime(2024,10,14));Assert.NotNull(attendance.CheckInTime);Assert.NotNull(attendance.CheckOutTime);}
    }
    [Theory]
    [InlineData(AttendanceDeviceRole.Entry,null,"I")]
    [InlineData(AttendanceDeviceRole.Exit,null,"O")]
    [InlineData(AttendanceDeviceRole.Entry,"O","O")]
    public void Roles_Do_Not_Overwrite_Explicit_Directions(AttendanceDeviceRole role,string? direction,string expected)
        =>Assert.Equal(expected,AttendanceDeviceService.NormalizeDirection(role,direction));


    [Fact]
    public async Task Conflicting_Device_Direction_Is_Retained_For_Review_Without_Import()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();
        db.Employees.Add(TestDb.Employee(code:"01"));var device=new AttendanceDevice {Name="Entry",Identifier="entry",Role=AttendanceDeviceRole.Entry,Model="Fixture"};
        db.AttendanceDevices.Add(device);await db.SaveChangesAsync();
        var service=new AttendanceDeviceService(db,new EphemeralDataProtectionProvider(),new[]{new Driver(conflict:true)},new AuditService(db));
        var import=new AttendanceImportService(db,NullLogger<AttendanceImportService>.Instance);
        var processing=new AttendanceProcessingService(db,new SettingsService(db),NullLogger<AttendanceProcessingService>.Instance);
        var log=Assert.Single(await service.SynchronizeAsync(device.Id,import,processing,"u1","tester",default));
        Assert.Equal(1,log.Review);Assert.Equal(0,log.Inserted);Assert.Contains("2024-10-14",log.ReviewRecordsJson!);
        Assert.Contains("\"AttType\":\"O\"",log.ReviewRecordsJson!);Assert.Empty(await db.RawPunches.ToListAsync());
    }

    [Fact]
    public async Task Overnight_Entry_And_Next_Day_Exit_Combine_Across_Devices()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();
        db.Shifts.Add(TestDb.NightShift());var emp=TestDb.Employee(code:"01");emp.ShiftId=2;db.Employees.Add(emp);
        var entry=new AttendanceDevice {Name="Entry",Identifier="entry",Role=AttendanceDeviceRole.Entry,Model="Fixture"};
        var exit=new AttendanceDevice {Name="Exit",Identifier="exit",Role=AttendanceDeviceRole.Exit,Model="Fixture"};
        db.AttendanceDevices.AddRange(entry,exit);await db.SaveChangesAsync();
        var service=new AttendanceDeviceService(db,new EphemeralDataProtectionProvider(),new[]{new Driver(overnight:true)},new AuditService(db));
        await service.MapEmployeeAsync(exit.Id,"99",1,"tester",default);
        await service.SynchronizeAsync(null,new AttendanceImportService(db,NullLogger<AttendanceImportService>.Instance),
            new AttendanceProcessingService(db,new SettingsService(db),NullLogger<AttendanceProcessingService>.Instance),"u1","tester",default);
        var day=await db.DailyAttendances.SingleAsync(d=>d.WorkDate==new DateTime(2024,10,14));
        Assert.Equal(new DateTime(2024,10,14,23,0,0),day.CheckInTime);Assert.Equal(new DateTime(2024,10,15,7,0,0),day.CheckOutTime);
    }


    [Fact]
    public async Task Employee_Rate_Hire_Date_And_Known_Deductions_Limit_Accrual()
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();Seed(db);
        var emp=await db.Employees.SingleAsync();emp.DailyRate=250;emp.StandardWorkHours=6;
        db.Deductions.Add(new Deduction {EmployeeId=1,Year=2024,Month=10,DeductionDate=new(2024,10,14),Amount=50,Status=ApprovalStatus.Approved});await db.SaveChangesAsync();
        var balance=await Advances(db).GetBalanceAsync(1,new(2024,10,20));Assert.Equal(1250,balance.Earned);Assert.Equal(1200,balance.Available);
        emp.HireDate=new(2024,10,15);await db.SaveChangesAsync();Assert.True((await Advances(db).GetBalanceAsync(1,new(2024,10,20))).NeedsReview);
    }

}
