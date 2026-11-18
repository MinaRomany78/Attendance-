using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Entities;

namespace HrPayroll.Domain.Services;

public static class PayrollDurationCalculator
{
    public static IReadOnlyList<PayrollAttendanceDuration> FromDaily(IEnumerable<DailyAttendance> days)
        => days.Where(d => d.IsPresent && !d.NeedsReview && d.MissingScheduledMinutes.HasValue
            && d.Status is not (AttendanceStatus.RestDay or AttendanceStatus.Holiday or AttendanceStatus.PaidLeave
                or AttendanceStatus.UnpaidLeave or AttendanceStatus.Leave or AttendanceStatus.Absent or AttendanceStatus.UnexcusedAbsence))
            .Select(d =>
            {
                var missing = d.MissingScheduledMinutes!.Value;
                var before = Math.Min(missing, AttendanceCalculator.MinutesBetween(d.ShiftStart,
                    d.CheckInTime!.Value < d.ShiftEnd ? d.CheckInTime.Value : d.ShiftEnd));
                return new PayrollAttendanceDuration(d.WorkDate, d.LateMinutes, d.EarlyLeaveMinutes, missing,
                    new decimal[] { before, missing - before }, d.ActualLateMinutes);
            }).ToList();

    public static PayrollDurationSnapshot Attendance(PayrollInputs inputs, AttendancePolicy policy)
    {
        var rows = inputs.AttendanceDurations;
        var actualLate = rows.Count > 0 ? rows.Sum(r => Math.Max(0m, r.ActualLateMinutes ?? r.LateMinutes)) : Math.Max(0, inputs.LateMinutes);
        var actualEarly = rows.Count > 0 ? rows.Sum(r => Math.Max(0m, r.EarlyDepartureMinutes)) : Math.Max(0, inputs.EarlyLeaveMinutes);
        var actualMissing = rows.Count > 0 ? rows.Sum(r => Math.Max(0m, r.MissingMinutes)) : (decimal?)null;
        if (!policy.RoundedAttendanceDeductionsEnabled)
            return new PayrollDurationSnapshot
            {
                ActualLateMinutes = actualLate, ActualEarlyDepartureMinutes = actualEarly, ActualMissingMinutes = actualMissing,
                ActualDurationMinutes = actualLate + actualEarly,
                PolicyDurationMinutes = (policy.LateDeductionEnabled ? Math.Max(0, inputs.LateMinutes) : 0m)
                    + (policy.EarlyLeaveDeductionEnabled ? actualEarly : 0m),
                ChargeableHours = ((policy.LateDeductionEnabled ? Math.Max(0, inputs.LateMinutes) : 0m)
                    + (policy.EarlyLeaveDeductionEnabled ? actualEarly : 0m)) / 60m,
                Basis = policy.EarlyLeaveDeductionEnabled ? AttendanceDeductionBasis.LateAndEarly : AttendanceDeductionBasis.LateOnly
            };

        policy.HourRounding.Validate(requireScope: true);
        if (policy.AttendanceDeductionBasis == AttendanceDeductionBasis.Unspecified)
            throw new InvalidOperationException("يجب اعتماد أساس واحد لخصم فروق الحضور قبل تفعيل تقريب الساعات.");
        var basis = policy.EarlyLeaveDeductionEnabled && policy.AttendanceDeductionBasis == AttendanceDeductionBasis.LateOnly
            ? AttendanceDeductionBasis.LateAndEarly : policy.AttendanceDeductionBasis;
        if (rows.Count == 0 && basis == AttendanceDeductionBasis.TotalMissing)
            throw new InvalidOperationException("تحتاج سياسة خصم الوقت المفقود إلى تفاصيل حضور يومية كاملة.");
        if (rows.Count == 0 && policy.HourRounding.Scope != HourRoundingScope.Monthly)
            throw new InvalidOperationException("لا يمكن توزيع إجمالي التأخير على الأحداث أو الأيام دون تفاصيلها.");
        var events = rows.SelectMany(r => Events(r, basis, policy.LateDeductionEnabled)).ToList();
        var days = rows.Select(r => new PayrollOvertimeDuration(r.WorkDate, Events(r, basis, policy.LateDeductionEnabled).Sum())).ToList();
        var policyMinutes = rows.Count > 0 ? days.Sum(d => d.Minutes)
            : (policy.LateDeductionEnabled ? Math.Max(0, inputs.LateMinutes) : 0m)
                + (basis == AttendanceDeductionBasis.LateAndEarly ? actualEarly : 0m);
        var rounded = policy.HourRounding.Scope switch
        {
            HourRoundingScope.PerEvent => events.Sum(m => HourDurationCalculator.RoundMinutes(m, policy.HourRounding)),
            HourRoundingScope.PerDay => days.GroupBy(d => d.WorkDate.Date).Sum(g => HourDurationCalculator.RoundMinutes(g.Sum(x => x.Minutes), policy.HourRounding)),
            HourRoundingScope.Monthly => HourDurationCalculator.RoundMinutes(policyMinutes, policy.HourRounding),
            _ => throw new InvalidOperationException("نطاق تقريب الساعات غير معتمد.")
        };
        var actual = basis switch
        {
            AttendanceDeductionBasis.TotalMissing => actualMissing ?? 0m,
            AttendanceDeductionBasis.LateAndEarly => actualLate + actualEarly,
            _ => actualLate
        };
        return new PayrollDurationSnapshot
        {
            ActualLateMinutes = actualLate, ActualEarlyDepartureMinutes = actualEarly, ActualMissingMinutes = actualMissing,
            ActualDurationMinutes = actual, PolicyDurationMinutes = policyMinutes, RoundedDurationHours = rounded,
            ChargeableHours = policy.LateDeductionEnabled || policy.EarlyLeaveDeductionEnabled ? rounded : 0m,
            Basis = basis, RoundingEnabled = true, Mode = policy.HourRounding.Mode,
            IncrementHours = policy.HourRounding.IncrementHours, Scope = policy.HourRounding.Scope
        };
    }

    private static IEnumerable<decimal> Events(PayrollAttendanceDuration row, AttendanceDeductionBasis basis, bool lateEnabled)
    {
        if (basis == AttendanceDeductionBasis.TotalMissing)
        {
            if (row.MissingIntervalsMinutes is { Count: > 0 })
            {
                if (row.MissingIntervalsMinutes.Any(m => m < 0) || row.MissingIntervalsMinutes.Sum() != Math.Max(0m, row.MissingMinutes))
                    throw new InvalidOperationException("تفاصيل فترات العمل المفقودة لا تطابق إجمالي اليوم.");
                return row.MissingIntervalsMinutes;
            }
            return new[] { Math.Max(0m, row.MissingMinutes) };
        }
        return basis == AttendanceDeductionBasis.LateAndEarly
            ? new[] { lateEnabled ? Math.Max(0m, row.LateMinutes) : 0m, Math.Max(0m, row.EarlyDepartureMinutes) }
            : new[] { lateEnabled ? Math.Max(0m, row.LateMinutes) : 0m };
    }

    public static decimal OvertimeHours(PayrollInputs inputs, AttendancePolicy policy, int payableMinutes)
    {
        if (!policy.RoundedOvertimeEnabled) return payableMinutes / 60m;
        policy.HourRounding.Validate(requireScope: true);
        if (policy.HourRounding.Scope == HourRoundingScope.Monthly)
            return HourDurationCalculator.RoundMinutes(payableMinutes, policy.HourRounding);
        if (inputs.ApprovedOvertimeDurations.Count == 0 && payableMinutes != 0)
            throw new InvalidOperationException("يجب توفير توزيع الإضافي المعتمد حسب التاريخ قبل التقريب لكل حدث أو يوم.");
        if (inputs.ApprovedOvertimeDurations.Sum(r => r.Minutes) != payableMinutes)
            throw new InvalidOperationException("تفاصيل الإضافي المعتمد لا تطابق إجمالي الوقت المدفوع.");
        return policy.HourRounding.Scope == HourRoundingScope.PerEvent
            ? inputs.ApprovedOvertimeDurations.Sum(r => HourDurationCalculator.RoundMinutes(r.Minutes, policy.HourRounding))
            : inputs.ApprovedOvertimeDurations.GroupBy(r => r.WorkDate.Date).Sum(g => HourDurationCalculator.RoundMinutes(g.Sum(r => r.Minutes), policy.HourRounding));
    }
}
