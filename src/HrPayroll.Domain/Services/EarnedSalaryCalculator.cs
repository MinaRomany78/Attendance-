namespace HrPayroll.Domain.Services;

/// <summary>Verified payable days only; the caller excludes future, unresolved and ineligible dates.</summary>
public static class EarnedSalaryCalculator
{
    public static decimal Calculate(decimal contractSalary, decimal dailyRate,
        int confirmedWorkedDays, int paidRestDays, decimal paidLeaveDays)
    {
        if (contractSalary < 0m || dailyRate < 0m || confirmedWorkedDays < 0 || paidRestDays < 0 || paidLeaveDays < 0m)
            throw new ArgumentOutOfRangeException(nameof(contractSalary), "لا يمكن أن تكون أيام أو مبالغ الاستحقاق سالبة.");
        var verifiedDays = confirmedWorkedDays + paidRestDays + paidLeaveDays;
        return PayrollCalculator.Round2(Math.Min(contractSalary, verifiedDays * dailyRate));
    }
}
