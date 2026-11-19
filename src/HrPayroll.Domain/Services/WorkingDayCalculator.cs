namespace HrPayroll.Domain.Services;
public static class WorkingDayCalculator
{
    public static decimal Count(DateTime start,DateTime end,IReadOnlySet<DayOfWeek> restDays,IReadOnlySet<DateTime> holidays)
    {
        var count=0m;
        for(var d=start.Date;d<=end.Date;d=d.AddDays(1))
            if(!restDays.Contains(d.DayOfWeek)&&!holidays.Contains(d))count++;
        return count;
    }
}
