using HrPayroll.Domain.Models;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using HrPayroll.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

/// <summary>
/// شاشة اعتماد الإضافي: تعرض الموظف والتاريخ والوردية ونهاية الوردية والخروج الفعلي
/// والإضافي الخام والمعتمد وسعر الساعة والمبلغ المحسوب — ثم تسمح بالاعتماد أو الرفض.
/// </summary>
[Authorize(Policy = "ManagePayroll")]
public class OvertimeController : Controller
{
    private readonly AppDbContext _db;
    private readonly ReportService _reports;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly ExcelExportService _excel;

    public OvertimeController(AppDbContext db, ReportService reports, SettingsService settings,
        AuditService audit, ExcelExportService excel)
    {
        _db = db;
        _reports = reports;
        _settings = settings;
        _audit = audit;
        _excel = excel;
    }

    private async Task<AttendanceReportFilter> BuildFilterAsync(DateTime? from, DateTime? to, string? department, int? employeeId, int? shiftId, CancellationToken ct = default)
    {
        var today = await CompanyPayrollClock.TodayAsync(_db,ct);
        var f = (from ?? PayrollPeriod.FromDate(today).Start).Date;
        var t = (to ?? today).Date;
        if (t < f) (f, t) = (t, f);
        return new AttendanceReportFilter { From = f, To = t, Department = department, EmployeeId = employeeId, ShiftId = shiftId };
    }

    public async Task<IActionResult> Index(DateTime? from, DateTime? to, string? department, int? employeeId,
        int? shiftId, bool onlyWithOvertime = true, CancellationToken ct = default)
    {
        var filter = await BuildFilterAsync(from, to, department, employeeId, shiftId, ct);
        var policy = await _settings.GetPolicyAsync(ct);

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync(ct);

        var rates = employees.ToDictionary(
            e => e.Id,
            e => decimal.Round(policy.DailyRateFor(e.BasicSalary, e.DailyRate)
                / (e.StandardWorkHours > 0m ? e.StandardWorkHours : policy.WorkHoursPerDay), 6, MidpointRounding.AwayFromZero));

        return View(new OvertimeIndexViewModel
        {
            Filter = filter,
            Rows = await _reports.GetOvertimeAsync(filter, onlyWithOvertime, ct),
            Departments = await _db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct),
            Shifts = await _db.Shifts.AsNoTracking().OrderBy(s => s.ShiftNumber).ToListAsync(ct),
            Employees = employees,
            HourlyRates = rates
        });
    }

    /// <summary>اعتماد ساعات إضافي؛ تبقى دقة التخزين بالدقائق ولا يُعد الاعتماد دفعًا.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveDay(int employeeId, string workDate, int approvedMinutes,
        string? returnUrl, CancellationToken ct, decimal? approvedHours = null)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "قيمة ساعات الإضافي غير صالحة.";
            return Back(returnUrl);
        }
        if (approvedHours.HasValue)
        {
            try { approvedMinutes = HourDurationCalculator.InputHoursToMinutes(approvedHours.Value); }
            catch (ArgumentOutOfRangeException) { TempData["Error"] = "ساعات الإضافي غير صالحة."; return Back(returnUrl); }
        }
        if (!DateTime.TryParse(workDate, out var date))
        {
            TempData["Error"] = "تاريخ غير صالح";
            return Back(returnUrl);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var daily = await _db.DailyAttendances
            .FirstOrDefaultAsync(d => d.EmployeeId == employeeId && d.WorkDate == date.Date, ct);
        if (daily is null)
        {
            TempData["Error"] = "لا يوجد سجل حضور لهذا اليوم";
            return Back(returnUrl);
        }

        if (await IsPeriodLockedAsync(date, ct))
        {
            TempData["Error"] = $"فترة {date:yyyy-MM} مقفلة براتب معتمد. أعد فتح الكشف أولًا.";
            return Back(returnUrl);
        }

        if (approvedMinutes < 0 || approvedMinutes > Math.Max(0, daily.RawOvertimeMinutes))
        {
            TempData["Error"] = "الاعتماد يجب أن يكون بين صفر وساعات الإضافي الخام المسجلة.";
            return Back(returnUrl);
        }

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        var policy = await _settings.GetPolicyAsync(ct);
        var standardHours = employee is { StandardWorkHours: > 0m } ? employee.StandardWorkHours : policy.WorkHoursPerDay;
        var hourlyRate = employee is null || standardHours <= 0m ? 0m
            : decimal.Round(policy.DailyRateFor(employee.BasicSalary, employee.DailyRate) / standardHours, 6, MidpointRounding.AwayFromZero);
        var amount = Math.Round(approvedMinutes / 60m * hourlyRate, 2);

        var old = daily.ApprovedOvertimeMinutes.ToString();
        daily.ApprovedOvertimeMinutes = approvedMinutes;
        daily.PaidOvertimeMinutes = 0; // Payment is determined once in the payroll snapshot.
        daily.UpdatedAt = DateTime.UtcNow;
        daily.UpdatedBy = this.CurrentUserName();

        await _audit.LogAsync(approvedMinutes > 0 ? "اعتماد إضافي" : "إلغاء اعتماد إضافي",
            nameof(Domain.Entities.DailyAttendance), daily.Id.ToString(), old, approvedMinutes.ToString(),
            this.CurrentUserName(), this.CurrentUserId(), ct);
        await PayrollEvidenceGuard.MarkAttendanceChangesAsync(_db, this.CurrentUserName(), ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        TempData["Success"] = approvedMinutes > 0
            ? $"تم اعتماد {HourDurationCalculator.DisplayHours(approvedMinutes)} ساعة إضافي بقيمة تقديرية قبل تقريب الرواتب {amount:N2}."
            : "تم إلغاء اعتماد الإضافي لهذا اليوم.";
        return Back(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAll(DateTime? from, DateTime? to, string? department, int? employeeId,
        int? shiftId, string? returnUrl, CancellationToken ct)
    {
        var filter = await BuildFilterAsync(from, to, department, employeeId, shiftId, ct);
        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var rows = await _db.DailyAttendances
            .Where(d => d.WorkDate >= filter.From && d.WorkDate <= filter.To && d.RawOvertimeMinutes > 0
                        && d.ApprovedOvertimeMinutes == 0)
            .Where(d => !filter.EmployeeId.HasValue || d.EmployeeId == filter.EmployeeId.Value)
            .Where(d => !filter.ShiftId.HasValue || d.ShiftId == filter.ShiftId.Value)
            .Where(d => string.IsNullOrEmpty(filter.Department) || d.Employee!.Department == filter.Department)
            .ToListAsync(ct);

        var lockedRuns = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.Status == PayrollStatus.Locked || r.Status == PayrollStatus.Approved)
            .ToListAsync(ct);
        var lockedPeriods = lockedRuns.Select(PayrollPeriod.ForRun).ToList();

        var skipped = 0;
        foreach (var d in rows)
        {
            if (lockedPeriods.Any(p => p.Contains(d.WorkDate))) { skipped++; continue; }
            d.ApprovedOvertimeMinutes = d.RawOvertimeMinutes;
            d.PaidOvertimeMinutes = 0;
            d.UpdatedAt = DateTime.UtcNow;
            d.UpdatedBy = this.CurrentUserName();
        }

        await _audit.LogAsync("اعتماد إضافي جماعي", nameof(Domain.Entities.DailyAttendance), null, null,
            $"اعتماد {rows.Count - skipped} يوم" + (skipped > 0 ? $" (تم تجاوز {skipped} يوم في فترات مقفلة)" : string.Empty),
            this.CurrentUserName(), this.CurrentUserId(), ct);
        await PayrollEvidenceGuard.MarkAttendanceChangesAsync(_db, this.CurrentUserName(), ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        TempData["Success"] = $"تم اعتماد الإضافي الخام لـ {rows.Count - skipped} يوم.";
        if (skipped > 0) TempData["Warning"] = $"تم تجاوز {skipped} يوم لأن فترة رواتبها مقفلة.";
        return Back(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectDay(int employeeId, string workDate, string? returnUrl, CancellationToken ct)
        => await ApproveDay(employeeId, workDate, 0, returnUrl, ct);

    public async Task<IActionResult> Export(DateTime? from, DateTime? to, string? department, int? employeeId,
        int? shiftId, CancellationToken ct)
    {
        var filter = await BuildFilterAsync(from, to, department, employeeId, shiftId, ct);
        var bytes = await _excel.ExportOvertimeAsync(filter, ct);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Overtime_{filter.From:yyyyMMdd}_{filter.To:yyyyMMdd}.xlsx");
    }

    private async Task<bool> IsPeriodLockedAsync(DateTime date, CancellationToken ct)
        => await FinancialPeriodGuard.OverlapsProtectedAsync(_db,date,date,ct);

    private IActionResult Back(string? returnUrl)
        => Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Index));
}
