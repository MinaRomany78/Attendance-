using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using HrPayroll.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

/// <summary>Saving registers a current-cycle payroll deduction; cash payments remain separate history.</summary>
[Authorize(Policy = AppPolicies.ManageSalaryAdvances)]
public class SalaryAdvancesController : Controller
{
    private readonly AppDbContext _db;
    private readonly SalaryAdvanceService _service;
    private readonly ExcelExportService _excel;
    private readonly ILogger<SalaryAdvancesController> _logger;

    public SalaryAdvancesController(AppDbContext db, SalaryAdvanceService service,
        ExcelExportService excel, ILogger<SalaryAdvancesController> logger)
    {
        _db = db; _service = service; _excel = excel; _logger = logger;
    }

    public async Task<IActionResult> Index(int? year, int? month, string? department, string? search, CancellationToken ct)
    {
        var today = await _service.CompanyTodayAsync(ct);
        var current = PayrollPeriod.FromDate(today);
        var y = year ?? current.Year; var m = month ?? current.Month;
        if (y is < 2 or > 9998 || m is < 1 or > 12) return BadRequest("فترة غير صالحة.");
        var period = PayrollPeriod.ForMonth(y, m);
        var vm = new SalaryAdvancesPage
        {
            Year = y, Month = m, Department = department, Search = search,
            Date = today < period.Start ? period.Start : today < period.End ? today : period.End,
            PeriodText = period.Display,
            IsReadOnly = period.Start != current.Start || await FinancialPeriodGuard.OverlapsProtectedAsync(_db, period.Start, period.End, ct),
            Departments = await _db.Employees.AsNoTracking().Where(e => e.Department != null)
                .Select(e => e.Department!).Distinct().OrderBy(d => d).ToListAsync(ct)
        };
        if (!string.IsNullOrEmpty(department))
        {
            var employees = _db.Employees.AsNoTracking().Where(e => e.Department == department
                && (e.IsActive || _db.SalaryAdvances.Any(a => a.EmployeeId == e.Id && a.Year == y && a.Month == m)));
            if (!string.IsNullOrWhiteSpace(search)) employees = employees.Where(e => e.FullName.Contains(search) || e.EmployeeCode.Contains(search));
            var ids = await employees.OrderBy(e => e.FullName).Select(e => e.Id).ToListAsync(ct);
            vm.Balances = await _service.GetBalancesAsync(ids, vm.Date, ct, period);
        }
        var history = _db.SalaryAdvances.Include(a => a.Employee).AsNoTracking().Where(a => a.Year == y && a.Month == m);
        if (!string.IsNullOrEmpty(department)) history = history.Where(a => a.Employee!.Department == department);
        if (!string.IsNullOrWhiteSpace(search)) history = history.Where(a => a.Employee!.FullName.Contains(search) || a.Employee.EmployeeCode.Contains(search));
        vm.History = await history.OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt).ToListAsync(ct);
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 8000)]
    public async Task<IActionResult> Save(SalaryAdvancesPage input, CancellationToken ct)
    {
        if (!ModelState.IsValid || input.Year is < 2 or > 9998 || input.Month is < 1 or > 12)
            return BadRequest(new { error = "أدخل دورة ومبالغ صحيحة غير سالبة." });
        try
        {
            var rows = await _service.RegisterAsync(input.Year, input.Month, input.Payments.Where(p => p.Selected)
                .Select(p => new PreparedAdvanceEdit(p.EmployeeId, p.Amount, p.Revision)).ToList(), this.CurrentUserName(), this.CurrentUserId(), ct);
            var balances = await _service.GetBalancesAsync(rows.Select(r => r.EmployeeId).ToList(), await _service.CompanyTodayAsync(ct), ct,
                PayrollPeriod.ForMonth(input.Year, input.Month));
            return Json(new
            {
                message = "تم حفظ السلف للخصم من مرتب الدورة الحالية. لم يتم تسجيل صرف نقدي.",
                rows = rows.Select(r =>
                {
                    var balance = balances.Single(b => b.Employee.Id == r.EmployeeId);
                    return new
                    {
                        employeeId = r.EmployeeId, id = r.Id, revision = r.Revision, amount = r.RegisteredAmount ?? 0,
                        available = balance.Available, capacity = balance.RegistrationCapacity, earned = balance.Earned, workedDays = balance.WorkedDays,
                        warning = r.RegisteredAmount > balance.RegistrationCapacity ? "قيمة السلفة أكبر من المستحق الحالي للموظف." : ""
                    };
                })
            });
        }
        catch (DbUpdateConcurrencyException)
        { return Conflict(new { error = "تم تعديل السلفة من مستخدم آخر. حدّث الصفحة قبل الحفظ." }); }
        catch (Exception ex)
        { return BadRequest(new { error = ex.UserMessage(_logger, "تعذر حفظ السلفة. أعد المحاولة بعد تحديث الصفحة") }); }
    }

    public async Task<IActionResult> ExportExcel(int year, int month, string? department, CancellationToken ct)
    {
        if (year is < 2 or > 9998 || month is < 1 or > 12) return BadRequest("فترة غير صالحة.");
        var bytes = await _excel.ExportSalaryAdvancesAsync(year, month, ct, department);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"SalaryAdvances_{year}_{month:00}.xlsx");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> MapLegacyPeriod(int id, int year, int month, string reason, CancellationToken ct)
    {
        if (!ModelState.IsValid || year is < 2 or > 9998 || month is < 1 or > 12)
            return BadRequest("أدخل دورة وسبب تحقق صحيحين.");
        try
        {
            await _service.MapLegacyPeriodAsync(id, year, month, reason, this.CurrentUserName(), this.CurrentUserId(), ct);
            TempData["Success"] = "تم ربط السلفة التاريخية بالدورة مع حفظ بيانات الصرف الأصلية وسجل التحقق.";
        }
        catch (Exception ex) { TempData["Error"] = ex.UserMessage(_logger, "تعذر ربط السلفة التاريخية"); }
        return RedirectToAction(nameof(Index), new { year, month });
    }
}
