namespace HrPayroll.Domain.Models;
public sealed record BasicSalaryEntitlement(decimal BasicSalary, decimal PayableDays, int EligibleCalendarDays)
{
    public static BasicSalaryEntitlement Calculate(decimal monthlySalary,DateTime hireDate,DateTime? terminationDate,PayrollPeriod period)
    {
        var first=hireDate.Date>period.Start?hireDate.Date:period.Start;
        var last=terminationDate.HasValue&&terminationDate.Value.Date<period.End?terminationDate.Value.Date:period.End;
        var eligible=Math.Max(0,(last-first).Days+1);
        var full=first==period.Start&&last==period.End;
        var payable=full?30:Math.Min(30,eligible);
        return new(Math.Round(monthlySalary*payable/30m,2,MidpointRounding.AwayFromZero),payable,eligible);
    }
}
