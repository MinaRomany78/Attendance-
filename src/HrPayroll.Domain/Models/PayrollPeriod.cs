using HrPayroll.Domain.Entities;
namespace HrPayroll.Domain.Models;

/// <summary>Inclusive payroll dates. Label year/month is the month in which the cycle closes.</summary>
public sealed record PayrollPeriod(DateTime Start, DateTime End)
{
    public int Year => End.Year;
    public int Month => End.Month;
    public int CalendarDays => (End.Date - Start.Date).Days + 1;
    public string Key => $"{Start:yyyy-MM-dd}/{End:yyyy-MM-dd}";
    public string Display => $"{Start:yyyy-MM-dd} → {End:yyyy-MM-dd}";
    public bool Contains(DateTime date) => date.Date >= Start && date.Date <= End;
    public bool Overlaps(DateTime from, DateTime to) => Start <= to.Date && End >= from.Date;
    public static PayrollPeriod ForMonth(int year, int month)
    {
        if (year is < 2 or > 9998 || month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(year), "فترة الرواتب غير صالحة.");
        var end = new DateTime(year, month, 25);
        return new(end.AddMonths(-1).AddDays(1), end);
    }
    public static PayrollPeriod FromDate(DateTime date)
    {
        var label = date.Day >= 26 ? date.AddMonths(1) : date;
        return ForMonth(label.Year, label.Month);
    }
    public static PayrollPeriod ForRun(PayrollRun run) => StoredOrLegacy(run.PeriodStart, run.PeriodEnd, run.Year, run.Month);
    public static PayrollPeriod StoredOrLegacy(DateTime? start, DateTime? end, int year, int month)
        => start.HasValue && end.HasValue ? new(start.Value.Date,end.Value.Date) : LegacyCalendarMonth(year,month);
    public static PayrollPeriod LegacyCalendarMonth(int year, int month)
    {
        var start = new DateTime(year,month,1);
        return new(start,start.AddMonths(1).AddDays(-1));
    }
}
