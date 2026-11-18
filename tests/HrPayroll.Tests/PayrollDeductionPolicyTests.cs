using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Domain.ValueObjects;

namespace HrPayroll.Tests;

public sealed class PayrollDeductionPolicyTests
{
    private static PayrollInputs Example() => new()
    {
        BasicSalary = 10_000m, LateMinutes = 63, EarlyLeaveMinutes = 164, AttendanceDays = 1,
        AttendanceDurations = [new(new DateTime(2026, 11, 1), 63, 164, 227, [63m, 164m], 63)]
    };

    [Fact]
    public void Corrected_Checkout_Measurements_Produce_Separate_Exact_Minute_Deductions()
    {
        var date = new DateTime(2026, 11, 1);
        var facts = AttendanceCalculator.CalculateDay(new ShiftWindow(date.AddHours(7), date.AddHours(15)),
            date.AddHours(8).AddMinutes(3), date.AddHours(12).AddMinutes(16), 30, 30);
        Assert.Equal(63, facts.ActualLateMinutes);
        Assert.Equal(164, facts.EarlyLeaveMinutes);
        Assert.Equal(253, facts.WorkedMinutes);
        Assert.Equal(227, facts.MissingScheduledMinutes);

        var result = PayrollCalculator.Compute(Example(), new AttendancePolicy { EarlyLeaveDeductionEnabled = true });
        Assert.Equal(43.75m, result.LateDeduction);
        Assert.Equal(113.89m, result.EarlyLeaveDeduction);
        Assert.Equal(0m, result.ShortageDeduction);
        Assert.Equal(157.64m, result.TotalDeductions);
        Assert.Equal(9_842.36m, result.NetSalary);
        Assert.Equal(227m, result.Duration.ActualMissingMinutes);
        Assert.Equal(result.AttendanceDeductions, result.Duration.DeductionAmount);
    }

    [Fact]
    public void Early_Departure_Policy_Is_Explicit_And_Does_Not_Enable_Rounding()
    {
        var disabled = PayrollCalculator.Compute(Example(), new AttendancePolicy());
        var enabled = PayrollCalculator.Compute(Example(), new AttendancePolicy { EarlyLeaveDeductionEnabled = true });
        Assert.Equal(0m, disabled.EarlyLeaveDeduction);
        Assert.Equal(disabled.LateDeduction, enabled.LateDeduction);
        Assert.False(enabled.Duration.RoundingEnabled);
        Assert.Equal(164, enabled.EarlyLeaveMinutes);
        var earlyOnly = PayrollCalculator.Compute(Example(), new AttendancePolicy
        {
            LateDeductionEnabled = false, EarlyLeaveDeductionEnabled = true
        });
        Assert.Equal(0m, earlyOnly.LateDeduction);
        Assert.Equal(113.89m, earlyOnly.TotalDeductions);
    }

    [Fact]
    public void Configured_Total_Missing_Charges_Shortage_Exactly_Once()
    {
        var result = PayrollCalculator.Compute(Example(), new AttendancePolicy
        {
            EarlyLeaveDeductionEnabled = true,
            RoundedAttendanceDeductionsEnabled = true,
            AttendanceDeductionBasis = AttendanceDeductionBasis.TotalMissing,
            HourRounding = new() { Scope = HourRoundingScope.Monthly, Mode = HourRoundingMode.Down, IncrementHours = 0.25m }
        });
        Assert.Equal(0m, result.LateDeduction);
        Assert.Equal(0m, result.EarlyLeaveDeduction);
        Assert.Equal(156.25m, result.ShortageDeduction);
        Assert.Equal(result.ShortageDeduction, result.TotalDeductions);
    }

    [Fact]
    public void Explicit_Rounded_Late_And_Early_Policy_Remains_Active_And_Splits_One_Total()
    {
        var result = PayrollCalculator.Compute(Example(), new AttendancePolicy
        {
            RoundedAttendanceDeductionsEnabled = true,
            AttendanceDeductionBasis = AttendanceDeductionBasis.LateAndEarly,
            HourRounding = new() { Scope = HourRoundingScope.Monthly, Mode = HourRoundingMode.Down, IncrementHours = 0.25m }
        });
        Assert.True(result.EarlyLeaveDeduction > 0m);
        Assert.Equal(0m, result.ShortageDeduction);
        Assert.Equal(156.25m, result.TotalDeductions);
        Assert.Equal(result.LateDeduction + result.EarlyLeaveDeduction, result.TotalDeductions);
    }

    [Fact]
    public void Saved_Historical_Aggregate_Is_Not_Deducted_Again_As_Shortage()
    {
        var original = Example();
        var result = PayrollCalculator.Compute(new PayrollInputs
        {
            BasicSalary = original.BasicSalary, LateMinutes = 63, EarlyLeaveMinutes = 164,
            AttendanceDurations = original.AttendanceDurations, SavedAttendanceDeduction = 157.64m,
            DurationSnapshotOverride = new()
            {
                Basis = AttendanceDeductionBasis.TotalMissing, ChargeableHours = 227m / 60m,
                ActualLateMinutes = 63, ActualEarlyDepartureMinutes = 164, ActualMissingMinutes = 227
            }
        }, new AttendancePolicy { EarlyLeaveDeductionEnabled = true, RoundedAttendanceDeductionsEnabled = true });
        Assert.Equal(157.64m, result.TotalDeductions);
        Assert.Equal(0m, result.EarlyLeaveDeduction);
        Assert.Equal(0m, result.ShortageDeduction);
    }

    [Fact]
    public void Per_Event_Rounding_Keeps_Each_Attendance_Component_Traceable()
    {
        var result = PayrollCalculator.Compute(Example(), new AttendancePolicy
        {
            EarlyLeaveDeductionEnabled = true,
            RoundedAttendanceDeductionsEnabled = true,
            AttendanceDeductionBasis = AttendanceDeductionBasis.LateAndEarly,
            HourRounding = new() { Scope = HourRoundingScope.PerEvent, Mode = HourRoundingMode.Up, IncrementHours = 0.25m }
        });
        Assert.Equal(52.08m, result.LateDeduction); // 63 minutes -> 1.25 hours under the configured rule.
        Assert.Equal(114.59m, result.EarlyLeaveDeduction); // Remainder preserves one rounded money total.
        Assert.Equal(166.67m, result.TotalDeductions);
    }

    [Fact]
    public void Saved_Split_Amounts_Survive_Editing_Another_Payroll_Field()
    {
        var result = PayrollCalculator.Compute(new PayrollInputs
        {
            BasicSalary = 10_000m, LateMinutes = 63, EarlyLeaveMinutes = 164,
            SavedAttendanceDeduction = 43.75m, SavedEarlyLeaveDeduction = 113.89m, SavedShortageDeduction = 0m,
            Bonuses = 200m
        }, new AttendancePolicy { EarlyLeaveDeductionEnabled = false });
        Assert.Equal(157.64m, result.AttendanceDeductions);
        Assert.Equal(10_042.36m, result.NetSalary);
    }

    [Theory]
    [InlineData(1, 0, 0, 333.33)]
    [InlineData(20, 4, 1, 8333.33)]
    [InlineData(27, 4, 0, 10000)]
    public void Earned_Salary_Uses_Verified_Days_And_Caps_At_Contract(int worked, int rest, decimal leave, decimal expected)
    {
        Assert.Equal(expected, EarnedSalaryCalculator.Calculate(10_000m, 10_000m / 30m, worked, rest, leave));
    }

    [Fact]
    public void Incomplete_Period_Earned_Value_Is_Separate_From_Contract_Projection()
    {
        var projected = PayrollCalculator.Compute(Example(), new AttendancePolicy { EarlyLeaveDeductionEnabled = true });
        var earnedBasic = EarnedSalaryCalculator.Calculate(projected.BasicSalary, projected.DailyRate, 1, 0, 0);
        Assert.Equal(10_000m, projected.BasicSalary);
        Assert.Equal(333.33m, earnedBasic);
        Assert.Equal(0m, projected.AbsenceDeduction);
    }

    [Fact]
    public void Historical_Missing_Early_Snapshots_Remain_Unknown()
    {
        var item = new PayrollItem { LateMinutes = 63, LateDeduction = 43.75m };
        Assert.Null(item.ActualEarlyDepartureHours);
        Assert.Equal(43.75m, item.AttendanceDeductions);
    }

    [Theory]
    [InlineData(0, 31, 10333.33)]
    [InlineData(30, 30, 10000)]
    public void Thirty_One_Day_Absence_Cap_Is_Explicit_And_Does_Not_Rewrite_Attendance_Or_Basic(
        int maximum, decimal chargedDays, decimal deduction)
    {
        var result = PayrollCalculator.Compute(new PayrollInputs
        {
            BasicSalary = 10_000m, AbsenceDays = 31m, AllowNegativeNetSalary = true
        }, new AttendancePolicy { AbsenceDeductionMaxDays = maximum });
        Assert.Equal(31m, result.AbsenceDays);
        Assert.Equal(chargedDays, result.ChargeableAbsenceDays);
        Assert.Equal(deduction, result.AbsenceDeduction);
        Assert.Equal(10_000m, result.BasicSalary);
        Assert.Equal(10_000m, result.GrossEarnings);
        using var details = System.Text.Json.JsonDocument.Parse(result.CalculationDetails);
        Assert.Equal(maximum, details.RootElement.GetProperty("policy").GetProperty("absenceDeductionMaxDays").GetInt32());
        Assert.Equal(chargedDays, details.RootElement.GetProperty("deductions").GetProperty("absence").GetProperty("chargedDays").GetDecimal());
    }
}
