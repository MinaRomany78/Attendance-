using HrPayroll.Domain.Enums;

namespace HrPayroll.Domain.Models;

/// <summary>Display defaults are illustrative. Financial use requires an explicitly selected scope.</summary>
public sealed record HourRoundingPolicy
{
    public HourRoundingMode Mode { get; init; } = HourRoundingMode.Nearest;
    public decimal IncrementHours { get; init; } = 1m;
    public HourRoundingScope Scope { get; init; } = HourRoundingScope.Unspecified;

    public void Validate(bool requireScope = false)
    {
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Scope)) throw new InvalidOperationException("طريقة أو نطاق تقريب الساعات غير صالح.");
        if (IncrementHours is not (0.25m or 0.5m or 1m)) throw new InvalidOperationException("خطوة تقريب الساعات يجب أن تكون 0.25 أو 0.5 أو 1 ساعة.");
        if (requireScope && Scope == HourRoundingScope.Unspecified)
            throw new InvalidOperationException("اختر نطاق التقريب المعتمد قبل تفعيل الحساب المالي بالساعات.");
    }
}

public sealed record DurationCalculation(decimal ActualDurationMinutes, decimal ActualDurationHours,
    decimal RoundedDurationHours, decimal ChargeableHours, decimal DeductionAmount);
