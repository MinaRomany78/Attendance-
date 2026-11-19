using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;

namespace HrPayroll.Domain.Services;

/// <summary>Decimal-only unit conversion and a single explicit rounding operation.</summary>
public static class HourDurationCalculator
{
    public static decimal ToHours(decimal minutes) => minutes / 60m;
    public static decimal DisplayHours(decimal minutes) => decimal.Round(ToHours(minutes), 2, MidpointRounding.AwayFromZero);

    public static decimal RoundHours(decimal hours, HourRoundingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (hours < 0m) throw new ArgumentOutOfRangeException(nameof(hours));
        var increments = hours / policy.IncrementHours;
        var rounded = policy.Mode switch
        {
            HourRoundingMode.Up => decimal.Ceiling(increments),
            HourRoundingMode.Down => decimal.Floor(increments),
            HourRoundingMode.Nearest => decimal.Round(increments, 0, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
        return rounded * policy.IncrementHours;
    }

    // Work in minute increments directly so the repeating decimal 1/60 cannot move an exact midpoint.
    public static decimal RoundMinutes(decimal minutes, HourRoundingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (minutes < 0m) throw new ArgumentOutOfRangeException(nameof(minutes));
        var steps = minutes / (policy.IncrementHours * 60m);
        var rounded = policy.Mode switch
        {
            HourRoundingMode.Up => decimal.Ceiling(steps),
            HourRoundingMode.Down => decimal.Floor(steps),
            _ => decimal.Round(steps, 0, MidpointRounding.AwayFromZero)
        };
        return rounded * policy.IncrementHours;
    }

    /// <summary>Input resolution of existing integer-minute storage; separate from financial hour rounding.</summary>
    public static int InputHoursToMinutes(decimal hours)
    {
        if (hours < 0m || hours > int.MaxValue / 60m) throw new ArgumentOutOfRangeException(nameof(hours));
        return checked((int)decimal.Round(hours * 60m, 0, MidpointRounding.AwayFromZero));
    }

    public static DurationCalculation Describe(decimal minutes, HourRoundingPolicy rounding,
        bool chargeable = false, decimal hourlyRate = 0m)
    {
        if (minutes < 0m) throw new ArgumentOutOfRangeException(nameof(minutes));
        var rounded = RoundMinutes(minutes, rounding);
        var charged = chargeable ? rounded : 0m;
        return new(minutes, ToHours(minutes), rounded, charged,
            decimal.Round(charged * hourlyRate, 2, MidpointRounding.AwayFromZero));
    }
}
