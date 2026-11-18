using System.Text.Json;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;

namespace HrPayroll.Infrastructure.Services;

/// <summary>Editing a draft preserves its recorded rates and policies unless explicitly recalculated.</summary>
internal static class PayrollSnapshotReader
{
    public static decimal Number(PayrollItem item, string section, string property, decimal fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(item.CalculationDetails ?? "{}");
            return document.RootElement.TryGetProperty(section, out var node) && node.TryGetProperty(property, out var value)
                && value.TryGetDecimal(out var number) ? number : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    public static AttendancePolicy Policy(PayrollItem item, AttendancePolicy current)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(item.CalculationDetails ?? "{}");
            var hasPolicy = document.RootElement.TryGetProperty("policy", out var node);
            bool Bool(string key, bool fallback) => hasPolicy && node.TryGetProperty(key, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
            var rule = hasPolicy && node.TryGetProperty("hourRounding", out var rounding)
                ? rounding.Deserialize<HourRoundingPolicy>() ?? new() : new HourRoundingPolicy();
            var basis = hasPolicy && node.TryGetProperty("attendanceDeductionBasis", out var basisValue) && basisValue.TryGetInt32(out var basisInt)
                ? (AttendanceDeductionBasis)basisInt : AttendanceDeductionBasis.Unspecified;
            return new AttendancePolicy
            {
                WorkDaysPerMonth = item.DailyRateBasisDays > 0 ? item.DailyRateBasisDays : 30,
                WorkHoursPerDay = item.WorkHoursPerDay > 0 ? item.WorkHoursPerDay : 8,
                DefaultOvertimeMultiplier = item.OvertimeMultiplier,
                LateDeductionEnabled = Bool("lateDeductionEnabled", item.LateMinutes == 0 ? current.LateDeductionEnabled : item.LateDeduction > 0),
                EarlyLeaveDeductionEnabled = Bool("earlyLeaveDeductionEnabled", item.EarlyLeaveDeduction > 0),
                AbsenceDeductionEnabled = Bool("absenceDeductionEnabled", item.AbsenceDays == 0 ? current.AbsenceDeductionEnabled : item.AbsenceDeduction > 0),
                AbsenceDeductionMaxDays = (int)Number(item, "policy", "absenceDeductionMaxDays", 0),
                UnpaidLeaveDeductionEnabled = Bool("unpaidLeaveDeductionEnabled", item.UnpaidLeaveDays == 0 ? current.UnpaidLeaveDeductionEnabled : item.UnpaidLeaveDeduction > 0),
                RoundedAttendanceDeductionsEnabled = Bool("roundedAttendanceDeductionsEnabled", false),
                RoundedOvertimeEnabled = Bool("roundedOvertimeEnabled", false),
                AttendanceDeductionBasis = basis, HourRounding = rule
            };
        }
        catch (JsonException)
        {
            return new AttendancePolicy
            {
                WorkDaysPerMonth = Math.Max(1, item.DailyRateBasisDays), WorkHoursPerDay = Math.Max(1, item.WorkHoursPerDay),
                LateDeductionEnabled = item.LateMinutes == 0 ? current.LateDeductionEnabled : item.LateDeduction > 0,
                EarlyLeaveDeductionEnabled = item.EarlyLeaveDeduction > 0,
                AbsenceDeductionEnabled = item.AbsenceDays == 0 ? current.AbsenceDeductionEnabled : item.AbsenceDeduction > 0,
                AbsenceDeductionMaxDays = 0,
                UnpaidLeaveDeductionEnabled = item.UnpaidLeaveDays == 0 ? current.UnpaidLeaveDeductionEnabled : item.UnpaidLeaveDeduction > 0
            };
        }
        finally { document?.Dispose(); }
    }
}
