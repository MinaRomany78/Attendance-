using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

/// <summary>سلفة منتصف الشهر (سلفة يوم 15): حساب من الحضور الفعلي ← مراجعة ← اعتماد ← دفع ← تسوية مع الراتب.</summary>
[Authorize(Policy = "ManagePayroll")]
public class SalaryAdvancesController : Controller
{
    private readonly AppDbContext _db;
    private readonly SalaryAdvanceService _service;
    private readonly ExcelExportService _excel;
    private readonly ILogger<SalaryAdvancesController> _logger;

    public SalaryAdvancesController(AppDbContext db, SalaryAdvanceService service,
        ExcelExportService excel, ILogger<SalaryAdvancesController> logger)
    {
        _db = db;
        _service = service;
        _excel = excel;
        _logger = logger;
    }

    public async Task<IActionResult> Index(int? year, int? month, CancellationToken ct)
    {
        var y = year ?? DateTime.Today.Year;
        var m = month ?? DateTime.Today.Month;
        var q = _db.SalaryAdvances.Include(a => a.Employee).AsNoTracking()
            .Where(a => a.Year == y && a.Month == m);
        ViewBag.Year = y; ViewBag.Month = m;
        ViewBag.Employees = new SelectList(await _db.Employees.AsNoTracking().Where(e => e.IsActive)
            .OrderBy(e => e.FullName).ToListAsync(ct), "Id", "FullName");
        var list = await q.OrderBy(a => a.Employee!.FullName).ToListAsync(ct);
        ViewBag.TotalProposed = list.Where(a => a.Status != SalaryAdvanceStatus.Cancelled).Sum(a => a.ProposedAmount);
        ViewBag.TotalApproved = list.Where(a => a.Status != SalaryAdvanceStatus.Cancelled).Sum(a => a.ApprovedAmount);
        ViewBag.TotalPaid = list.Sum(a => a.PaidAmount);
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Calculate(int employeeId, int year, int month, DateTime cutoff, CancellationToken ct)
    {
        try
        {
            var advance = await _service.CalculateAsync(employeeId, year, month, cutoff,
                this.CurrentUserName(), this.CurrentUserId(), ct);
            TempData["Success"] = $"تم حساب السلفة: المستحق {advance.EarnedAmount:N2} — المقترح {advance.ProposedAmount:N2} EGP.";
        }
        catch (Exception ex) { TempData["Error"] = ex.UserMessage(_logger, "فشل حساب سلفة منتصف الشهر"); }
        return RedirectToAction(nameof(Index), new { year, month });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(int id, decimal approvedAmount, string? reason,
        int year, int month, CancellationToken ct)
    {
        try
        {
            await _service.ReviewAsync(id, approvedAmount, reason,
                this.CurrentUserName(), this.CurrentUserId(), ct);
            TempData["Success"] = "تم حفظ المراجعة.";
        }
        catch (Exception ex) { TempData["Error"] = ex.UserMessage(_logger, "فشل مراجعة السلفة"); }
        return RedirectToAction(nameof(Index), new { year, month });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, int year, int month, CancellationToken ct)
    {
        try
        {
            await _service.ApproveAsync(id, this.CurrentUserName(), this.CurrentUserId(), ct);
            TempData["Success"] = "تم اعتماد السلفة.";
        }
        catch (Exception ex) { TempData["Error"] = ex.UserMessage(_logger, "فشل اعتماد السلفة"); }
        return RedirectToAction(nameof(Index), new { year, month });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(int id, decimal paidAmount, int year, int month, CancellationToken ct)
    {
        try
        {
            await _service.MarkPaidAsync(id, paidAmount, this.CurrentUserName(), this.CurrentUserId(), ct);
            TempData["Success"] = "تم تسجيل الدفع — ستُسوَّى السلفة تلقائيًا مع كشف راتب نفس الشهر.";
        }
        catch (Exception ex) { TempData["Error"] = ex.UserMessage(_logger, "فشل تسجيل دفع السلفة"); }
        return RedirectToAction(nameof(Index), new { year, month });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string reason, int year, int month, CancellationToken ct)
    {
        try
        {
            await _service.CancelAsync(id, reason, this.CurrentUserName(), this.CurrentUserId(), ct);
            TempData["Success"] = "تم إلغاء السلفة.";
        }
        catch (Exception ex) { TempData["Error"] = ex.UserMessage(_logger, "فشل إلغاء السلفة"); }
        return RedirectToAction(nameof(Index), new { year, month });
    }

    public async Task<IActionResult> ExportExcel(int year, int month, CancellationToken ct)
    {
        var bytes = await _excel.ExportSalaryAdvancesAsync(year, month, ct);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SalaryAdvances_{year}_{month:00}.xlsx");
    }
}
