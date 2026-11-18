using HrPayroll.Domain.ValueObjects;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;

namespace HrPayroll.Domain.Services;

/// <summary>
/// حسابات التأخير والانصراف المبكر والإضافي. منطق نقي لا يعتمد على قاعدة البيانات.
///
/// قواعد الشركة المطبقة:
/// - التأخير: فترة السماح عتبة وليست إعفاءً. من حضر داخل السماح لا تأخير عليه،
///   ومن تجاوزها يُحسب تأخيره كاملًا من وقت بداية الوردية.
/// - الإضافي: فترة السماح عتبة أيضًا. من خرج داخل سماح الخروج لا إضافي له،
///   ومن تجاوزها يُحسب إضافيه كاملًا من وقت نهاية الوردية الرسمية.
/// </summary>
public static class AttendanceCalculator
{
    public static int MinutesBetween(DateTime start, DateTime end)
        => Math.Max(0, checked((int)decimal.Round((end.Ticks - start.Ticks) / (decimal)TimeSpan.TicksPerMinute,
            0, MidpointRounding.AwayFromZero)));

    /// <summary>Shared facts for biometric processing, manual correction and reports. No financial hour rounding.</summary>
    public static AttendanceTimeCalculation CalculateDay(ShiftWindow window, DateTime? checkIn, DateTime? checkOut,
        int entryGraceMinutes, int exitGraceMinutes, int minimumOvertimeMinutes = 1,
        GracePeriodMode graceMode = GracePeriodMode.Threshold)
    {
        var incomplete = checkIn is null || checkOut is null;
        var invalid = window.End <= window.Start || (!incomplete && checkOut <= checkIn);
        var worked = !incomplete && !invalid ? MinutesBetween(checkIn!.Value, checkOut!.Value) : 0;
        var actualLate = checkIn.HasValue ? MinutesBetween(window.Start, checkIn.Value) : 0;
        var late = CalculateLateMinutes(window, entryGraceMinutes, checkIn, graceMode);
        var early = CalculateEarlyLeaveMinutes(window, checkOut);
        var overtime = CalculateOvertimeMinutes(window, exitGraceMinutes, checkOut, minimumOvertimeMinutes);
        var scheduled = MinutesBetween(window.Start, window.End);
        int? missing = null;
        if (!incomplete && !invalid)
        {
            var overlapStart = checkIn!.Value > window.Start ? checkIn.Value : window.Start;
            var overlapEnd = checkOut!.Value < window.End ? checkOut.Value : window.End;
            missing = Math.Max(0, scheduled - MinutesBetween(overlapStart, overlapEnd));
        }
        var review = incomplete || invalid || (missing == scheduled && scheduled > 0);
        var status = checkIn is null && checkOut is null ? AttendanceStatus.NeedsReview
            : checkIn is null ? AttendanceStatus.MissingCheckIn
            : checkOut is null ? AttendanceStatus.MissingCheckOut
            : invalid || review ? AttendanceStatus.NeedsReview
            : late > 0 ? AttendanceStatus.Late
            : early > 0 ? AttendanceStatus.EarlyDeparture : AttendanceStatus.Present;
        return new(worked, actualLate, late, early, overtime, scheduled, missing, incomplete, review, status);
    }
    /// <summary>
    /// يحسب دقائق التأخير للوردية.
    /// مثال (بداية 07:00 وسماح 30): 07:10 => 0 ، 07:25 => 0 ، 07:30 => 0 ، 07:35 => 35 ، 08:00 => 60.
    /// </summary>
    public static int CalculateLateMinutes(ShiftWindow window, int entryGraceMinutes, DateTime? checkIn,
        GracePeriodMode graceMode = GracePeriodMode.Threshold)
    {
        if (checkIn is null) return 0;

        var graceEnd = window.Start.AddMinutes(Math.Max(0, entryGraceMinutes));
        if (checkIn.Value <= graceEnd) return 0;

        return MinutesBetween(graceMode == GracePeriodMode.ExcludeGrace ? graceEnd : window.Start, checkIn.Value);
    }

    /// <summary>يحسب دقائق الانصراف المبكر (لا تُنتج قيمًا سالبة).</summary>
    public static int CalculateEarlyLeaveMinutes(ShiftWindow window, DateTime? checkOut)
    {
        if (checkOut is null) return 0;
        return MinutesBetween(checkOut.Value, window.End);
    }

    /// <summary>
    /// يحسب دقائق الإضافي الخام.
    /// مثال (نهاية 15:00 وسماح خروج 30): 15:00 => 0 ، 15:15 => 0 ، 15:30 => 0 ، 15:35 => 35 ،
    /// 15:40 => 40 ، 16:00 => 60 ، 17:00 => 120.
    /// ويعمل بنفس المنطق للورديات الليلية (نهاية 07:00 وسماح 30 وخروج 08:00 => 60).
    /// </summary>
    public static int CalculateOvertimeMinutes(
        ShiftWindow window, int exitGraceMinutes, DateTime? checkOut, int minimumOvertimeMinutes = 1)
    {
        if (checkOut is null) return 0;

        var graceEnd = window.End.AddMinutes(Math.Max(0, exitGraceMinutes));
        if (checkOut.Value <= graceEnd) return 0;

        var rounded = MinutesBetween(window.End, checkOut.Value);
        return rounded < Math.Max(1, minimumOvertimeMinutes) ? 0 : rounded;
    }
}
