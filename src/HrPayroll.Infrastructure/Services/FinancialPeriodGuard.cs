using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace HrPayroll.Infrastructure.Services;
public static class FinancialPeriodGuard
{
    public static async Task<bool> IsProtectedAsync(AppDbContext db,int year,int month,CancellationToken ct=default)
    {
        if (year is < 2 or > 9998 || month is < 1 or > 12) return false;
        var p=PayrollPeriod.ForMonth(year,month);
        return await OverlapsProtectedAsync(db,p.Start,p.End,ct);
    }
    public static async Task<bool> OverlapsProtectedAsync(AppDbContext db,DateTime from,DateTime to,CancellationToken ct=default)
    {
        var protectedRuns=await db.PayrollRuns.AsNoTracking().Where(r=>r.Status==PayrollStatus.Approved||r.Status==PayrollStatus.Locked).ToListAsync(ct);
        return protectedRuns.Any(r=>PayrollPeriod.ForRun(r).Overlaps(from,to));
    }
    public static async Task EnsureEditableAsync(AppDbContext db,DateTime from,DateTime to,CancellationToken ct=default)
    {
        if(await OverlapsProtectedAsync(db,from,to,ct))
            throw new InvalidOperationException("التواريخ مرتبطة بدورة رواتب معتمدة أو مقفلة. استخدم إعادة الفتح المعتمدة قبل تعديل المصدر.");
    }
}
