using System.Text.Json;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Services;

namespace HrPayroll.Domain.Models;

public sealed record PayrollAttendanceDuration(DateTime WorkDate, decimal LateMinutes,
    decimal EarlyDepartureMinutes, decimal MissingMinutes,
    IReadOnlyList<decimal>? MissingIntervalsMinutes = null, decimal? ActualLateMinutes = null);

public sealed record PayrollOvertimeDuration(DateTime WorkDate, decimal Minutes);

/// <summary>Auditable duration snapshot, independent of the current company settings.</summary>
public sealed record PayrollDurationSnapshot
{
    public decimal ActualLateMinutes { get; init; }
    public decimal? ActualEarlyDepartureMinutes { get; init; }
    public decimal? ActualMissingMinutes { get; init; }
    public decimal ActualDurationMinutes { get; init; }
    public decimal ActualDurationHours => ActualDurationMinutes / 60m;
    public decimal PolicyDurationMinutes { get; init; }
    public decimal? RoundedDurationHours { get; init; }
    public decimal ChargeableHours { get; init; }
    public decimal DeductionAmount { get; init; }
    public AttendanceDeductionBasis Basis { get; init; } = AttendanceDeductionBasis.LateOnly;
    public bool RoundingEnabled { get; init; }
    public HourRoundingMode Mode { get; init; } = HourRoundingMode.Nearest;
    public decimal IncrementHours { get; init; } = 1m;
    public HourRoundingScope Scope { get; init; } = HourRoundingScope.Unspecified;

    public string BasisText => Basis switch
    {
        AttendanceDeductionBasis.LateAndEarly => "التأخير والانصراف المبكر",
        AttendanceDeductionBasis.TotalMissing => "إجمالي الوقت المجدول المفقود",
        _ => "التأخير فقط"
    };
    public string Description => !RoundingEnabled ? "السياسة المحفوظة دون تقريب ساعات الخصم" :
        $"{BasisText}؛ {(Mode == HourRoundingMode.Up ? "لأعلى" : Mode == HourRoundingMode.Down ? "لأسفل" : "للأقرب")} {IncrementHours:0.##} ساعة؛ " +
        (Scope == HourRoundingScope.PerEvent ? "لكل حدث" : Scope == HourRoundingScope.PerDay ? "لكل يوم" : "لإجمالي الشهر");

    public static PayrollDurationSnapshot Read(PayrollItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CalculationDetails))
        {
            try
            {
                using var doc = JsonDocument.Parse(item.CalculationDetails);
                if (doc.RootElement.TryGetProperty("duration", out var value))
                {
                    var snapshot = value.Deserialize<PayrollDurationSnapshot>();
                    if (snapshot is not null) return snapshot;
                }
            }
            catch (JsonException) { /* Historical entries may predate structured snapshots. */ }
        }
        return new PayrollDurationSnapshot
        {
            ActualLateMinutes = item.LateMinutes, ActualDurationMinutes = item.LateMinutes,
            ActualEarlyDepartureMinutes = item.EarlyLeaveMinutes,
            PolicyDurationMinutes = item.LateMinutes + (item.EarlyLeaveMinutes ?? 0),
            ChargeableHours = item.AttendanceDeductions == 0m ? 0m : (item.LateMinutes + (item.EarlyLeaveMinutes ?? 0)) / 60m,
            DeductionAmount = item.AttendanceDeductions
        };
    }
}
