using System.Text.Json;
using HrPayroll.Domain.Enums;

namespace HrPayroll.Domain.Models;

/// <summary>
/// سياسة/إعدادات الحساب (قابلة للتعديل من الإعدادات).
/// كل الافتراضات هنا هي افتراضات سياسة الشركة القابلة للتغيير وليست قواعد قانونية عامة.
/// </summary>
public sealed class AttendancePolicy
{
    /// <summary>عدد أيام الشهر المستخدم لحساب الأجر اليومي (افتراضي 30).</summary>
    public int WorkDaysPerMonth { get; init; } = 30;

    /// <summary>عدد ساعات العمل في اليوم (افتراضي 8).</summary>
    public int WorkHoursPerDay { get; init; } = 8;

    /// <summary>معامل الإضافي الافتراضي. سياسة الشركة = 1 (بدون أي مضاعفة).</summary>
    public decimal DefaultOvertimeMultiplier { get; init; } = 1m;

    /// <summary>هل يُحتسب خصم التأخير من الراتب؟</summary>
    public bool LateDeductionEnabled { get; init; } = true;

    /// <summary>هل يُحتسب خصم الغياب من الراتب؟</summary>
    public bool AbsenceDeductionEnabled { get; init; } = true;

    /// <summary>هل يُحتسب خصم الإجازة بدون أجر؟</summary>
    public bool UnpaidLeaveDeductionEnabled { get; init; } = true;

    /// <summary>دقائق السماح عند الدخول الافتراضية إذا لم تُضبط في الوردية.</summary>
    public int EntryGraceMinutes { get; init; } = 30;

    /// <summary>دقائق السماح عند الخروج الافتراضية إذا لم تُضبط في الوردية.</summary>
    public int ExitGraceMinutes { get; init; } = 30;

    /// <summary>خطوة تقريب الإضافي النهائي بالدقائق (1 = بلا تقريب، 5/15/30 ...).</summary>
    public int OvertimeRoundingStepMinutes { get; init; } = 1;

    /// <summary>هل يُقرَّب الإضافي للأسفل؟</summary>
    public bool RoundOvertimeDown { get; init; } = true;

    /// <summary>دقائق قبل بداية الوردية تُعتبر بصمة دخول مقبولة.</summary>
    public int EarlyArrivalToleranceMinutes { get; init; } = 120;

    /// <summary>دقائق بعد نهاية الوردية تُعتبر بصمة خروج مقبولة (سقف الإضافي).</summary>
    public int OvertimeToleranceMinutes { get; init; } = 240;

    /// <summary>نسبة الطول التي يُقبل خلالها اعتبار أول بصمة "دخول" (من بداية الوردية).</summary>
    public double EntryZoneRatio { get; init; } = 0.5;

    /// <summary>أقل دقائق إضافي معتبرة.</summary>
    public int MinimumOvertimeMinutes { get; init; } = 1;

    /// <summary>هل الراتب الشهري يشمل بالفعل أيام الراحة المدفوعة؟ (سياسة الشركة: نعم)</summary>
    public bool MonthlySalaryIncludesPaidRestDays { get; init; } = true;

    /// <summary>الأجر اليومي = الراتب الأساسي / أيام الشهر، إلا إذا كان للموظف أجر يومي مستقل.</summary>
    public decimal DailyRateFor(decimal basicSalary, decimal? dailyRateOverride = null)
    {
        if (dailyRateOverride is > 0m) return Math.Round(dailyRateOverride.Value, 6, MidpointRounding.AwayFromZero);
        if (WorkDaysPerMonth <= 0) return 0m;
        return Math.Round(basicSalary / WorkDaysPerMonth, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>الأجر بالساعة = الأجر اليومي / ساعات العمل اليومية.</summary>
    public decimal HourlyRateFor(decimal dailyRate)
        => WorkHoursPerDay <= 0 ? 0m : Math.Round(dailyRate / WorkHoursPerDay, 6, MidpointRounding.AwayFromZero);

    public decimal MinuteRateFor(decimal dailyRate)
        => Math.Round(HourlyRateFor(dailyRate) / 60m, 8, MidpointRounding.AwayFromZero);

    public decimal HourlyRateOfSalary(decimal basicSalary, decimal? dailyRateOverride = null)
        => HourlyRateFor(DailyRateFor(basicSalary, dailyRateOverride));

    /// <summary>يقرب دقائق الإضافي النهائية مرة واحدة وفق الخطوة المُعدّة.</summary>
    public int RoundOvertimeMinutes(int minutes)
    {
        if (minutes <= 0) return 0;
        if (OvertimeRoundingStepMinutes <= 1) return minutes;
        var step = OvertimeRoundingStepMinutes;
        var steps = minutes / (decimal)step;
        var rounded = RoundOvertimeDown
            ? Math.Floor(steps)
            : Math.Round(steps, MidpointRounding.AwayFromZero);
        return (int)rounded * step;
    }
}
