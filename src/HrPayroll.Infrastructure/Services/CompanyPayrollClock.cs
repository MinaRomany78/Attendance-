using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace HrPayroll.Infrastructure.Services;
public static class CompanyPayrollClock
{
    public static async Task<DateTime> TodayAsync(AppDbContext db, CancellationToken ct = default, TimeProvider? clock = null)
    {
        var zone = await db.AppSettings.AsNoTracking().Where(s=>s.Key==SettingKeys.CompanyTimeZone).Select(s=>s.Value).FirstOrDefaultAsync(ct) ?? "Africa/Cairo";
        return TimeZoneInfo.ConvertTimeFromUtc((clock??TimeProvider.System).GetUtcNow().UtcDateTime,TimeZoneInfo.FindSystemTimeZoneById(zone)).Date;
    }
    public static async Task<PayrollPeriod> CurrentAsync(AppDbContext db, CancellationToken ct = default)
        => PayrollPeriod.FromDate(await TodayAsync(db,ct));
}
