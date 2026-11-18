using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Domain.ValueObjects;

namespace HrPayroll.Infrastructure.Services;

/// <summary>Apply shared attendance facts without changing source punches or intentional status decisions.</summary>
public static class AttendanceDayRecalculator
{
    public static AttendanceTimeCalculation Apply(DailyAttendance daily, Shift? shift, AttendancePolicy policy,
        bool preserveStatus = false)
    {
        var window = daily.ShiftEnd > daily.ShiftStart
            ? new ShiftWindow(daily.ShiftStart, daily.ShiftEnd)
            : shift is not null ? ShiftWindowResolver.Resolve(shift, daily.WorkDate)
            : new ShiftWindow(daily.WorkDate.Date, daily.WorkDate.Date);
        if (window.End > window.Start)
        {
            daily.ShiftStart = window.Start;
            daily.ShiftEnd = window.End;
        }
        var facts = AttendanceCalculator.CalculateDay(window, daily.CheckInTime, daily.CheckOutTime,
            shift?.EntryGraceMinutes ?? policy.EntryGraceMinutes, shift?.ExitGraceMinutes ?? policy.ExitGraceMinutes,
            policy.MinimumOvertimeMinutes, policy.GracePeriodMode);
        daily.LateMinutes = facts.LateMinutes;
        daily.EarlyLeaveMinutes = facts.EarlyLeaveMinutes;
        daily.RawOvertimeMinutes = facts.RawOvertimeMinutes;
        daily.HasIncompletePunches = facts.HasIncompletePunches;
        var ambiguous = daily.DetectionSource == ShiftDetectionSource.Ambiguous || window.End <= window.Start;
        daily.PunchReviewStatus = facts.NeedsReview || ambiguous ? ClassificationStatus.NeedsReview : ClassificationStatus.Classified;
        daily.NeedsReviewReason = facts.HasIncompletePunches ? "البصمات غير مكتملة؛ لا تُفترض ساعات عمل أو إضافي مدفوع."
            : ambiguous ? "الوردية غير محسومة؛ راجع الجدول قبل اعتماد الحساب."
            : facts.NeedsReview ? "تسلسل أوقات الحضور لا يطابق وردية عمل مكتملة." : null;
        if (!preserveStatus) daily.Status = facts.DerivedStatus;
        daily.ApprovedOvertimeMinutes = facts.HasIncompletePunches || facts.NeedsReview ? 0
            : Math.Clamp(daily.ApprovedOvertimeMinutes, 0, facts.RawOvertimeMinutes);
        daily.PaidOvertimeMinutes = 0; // Draft payroll must explicitly recalculate its approved payable duration.
        return facts;
    }

    public static bool HasIntentionalStatus(IEnumerable<AttendanceCorrection> history)
    {
        var approved = history.Where(c => c.Status == ApprovalStatus.Approved).OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToList();
        var marker = approved.FirstOrDefault(c => c.FieldName == "StatusOverride");
        return marker is not null ? marker.NewValue == "Manual"
            : approved.Any(c => c.FieldName == nameof(DailyAttendance.Status));
    }
}
