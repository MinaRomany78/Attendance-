using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace HrPayroll.Infrastructure.Services;

/// <summary>Changed attendance invalidates draft evidence, never rewrites stored monetary amounts.</summary>
public static class PayrollEvidenceGuard
{
    public static async Task MarkAttendanceChangesAsync(AppDbContext db,string? user,CancellationToken ct=default)
    {
        string[] fields = [nameof(DailyAttendance.CheckInTime),nameof(DailyAttendance.CheckOutTime),nameof(DailyAttendance.LateMinutes),
            nameof(DailyAttendance.EarlyLeaveMinutes),nameof(DailyAttendance.RawOvertimeMinutes),nameof(DailyAttendance.ApprovedOvertimeMinutes),
            nameof(DailyAttendance.PaidOvertimeMinutes),nameof(DailyAttendance.Status),nameof(DailyAttendance.PunchReviewStatus),
            nameof(DailyAttendance.HasIncompletePunches),nameof(DailyAttendance.ShiftStart),nameof(DailyAttendance.ShiftEnd),nameof(DailyAttendance.LeaveRequestId)];
        var changes=db.ChangeTracker.Entries<DailyAttendance>().Where(e=>e.State==EntityState.Added
            || e.State==EntityState.Modified&&fields.Any(f=>!Equals(e.OriginalValues[f],e.CurrentValues[f])))
            .Select(e=>new{e.Entity.EmployeeId,Date=e.Entity.WorkDate.Date}).Distinct().ToList();
        if(changes.Count==0)return;
        var runs=await db.PayrollRuns.Where(r=>r.Status!=PayrollStatus.Cancelled&&r.Status!=PayrollStatus.Locked&&r.Status!=PayrollStatus.Approved).ToListAsync(ct);
        foreach(var run in runs)
        {
            var dates=changes.Where(c=>PayrollPeriod.ForRun(run).Contains(c.Date)).ToList();if(dates.Count==0)continue;
            var employeeIds=dates.Select(c=>c.EmployeeId).Distinct().ToList();
            var items=await db.PayrollItems.Where(i=>i.PayrollRunId==run.Id&&employeeIds.Contains(i.EmployeeId)).ToListAsync(ct);
            if(items.Count==0)continue;
            foreach(var item in items)item.FinancialReviewReason="تغير الحضور بعد احتساب الكشف. أعد احتساب نفس الدورة قبل التحرير أو الاعتماد.";
            run.Status=PayrollStatus.PendingReview;run.UpdatedAt=DateTime.UtcNow;run.UpdatedBy=user;
        }
    }
}
