using HrPayroll.Domain.Models;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

[Authorize(Policy = "ManageLoans")]
public class DeductionsController : Controller
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public DeductionsController(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(int? year, int? month, CancellationToken ct)
    {
        var current = await CompanyPayrollClock.CurrentAsync(_db,ct);
        var y = year ?? current.Year;
        var q = _db.Deductions.Include(d => d.Employee).AsNoTracking().Where(d => d.Year == y);
        if (month.HasValue) q = q.Where(d => d.Month == month.Value);
        ViewBag.Year = y; ViewBag.Month = month;
        return View(await q.OrderByDescending(d => d.DeductionDate).ToListAsync(ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await FillAsync(ct);
        var today = await CompanyPayrollClock.TodayAsync(_db,ct); var period = PayrollPeriod.FromDate(today);
        return View("Form", new Deduction { DeductionDate = today, Month = period.Month, Year = period.Year });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Deduction model, CancellationToken ct)
    {
        if (model.DeductionDate.Year is < 2 or > 9998) ModelState.AddModelError(nameof(model.DeductionDate),"التاريخ غير صالح.");
        else { model.Year = PayrollPeriod.FromDate(model.DeductionDate).Year; model.Month = PayrollPeriod.FromDate(model.DeductionDate).Month; }
        Validate(model);
        if (!await _db.Employees.AnyAsync(e => e.Id == model.EmployeeId, ct)) ModelState.AddModelError(nameof(model.EmployeeId), "الموظف غير موجود");
        if (await FinancialPeriodGuard.IsProtectedAsync(_db, model.Year, model.Month, ct))
            ModelState.AddModelError(string.Empty, "الفترة مرتبطة بكشف رواتب معتمد أو مقفل.");
        if (!ModelState.IsValid) { await FillAsync(ct); return View("Form", model); }
        model.Id = 0; model.PayrollItemId = null; model.PayrollItem = null; model.Employee = null;
        model.Status = ApprovalStatus.Pending; model.ApprovedAt = null; model.ApprovedBy = null;
        model.CreatedBy = User.Identity?.Name;
        _db.Deductions.Add(model);
        await _db.SaveChangesAsync(ct);
        _audit.Log("إضافة خصم", nameof(Deduction), model.Id.ToString(), null, $"{model.Amount:0.00} - {model.Reason}", User.Identity?.Name);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "تم تسجيل الخصم";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var d = await _db.Deductions.FindAsync(new object?[] { id }, ct);
        if (d is null) return NotFound();
        if (d.PayrollItemId.HasValue || await FinancialPeriodGuard.IsProtectedAsync(_db, d.Year, d.Month, ct))
        {
            TempData["Error"] = "لا يمكن تعديل خصم تم تطبيقه في كشف رواتب";
            return RedirectToAction(nameof(Index));
        }
        await FillAsync(ct);
        return View("Form", d);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Deduction model, CancellationToken ct)
    {
        if (id != model.Id) return BadRequest();
        Validate(model);
        if (!ModelState.IsValid) { await FillAsync(ct); return View("Form", model); }

        var d = await _db.Deductions.FindAsync(new object?[] { id }, ct);
        if (d is null) return NotFound();
        if (d.PayrollItemId.HasValue || await FinancialPeriodGuard.IsProtectedAsync(_db, d.Year, d.Month, ct)
            || await FinancialPeriodGuard.IsProtectedAsync(_db, model.Year, model.Month, ct))
        {
            TempData["Error"] = "لا يمكن تعديل خصم مطبّق في كشف رواتب";
            return RedirectToAction(nameof(Index));
        }

        d.EmployeeId = model.EmployeeId;
        d.Amount = model.Amount;
        d.DeductionDate = model.DeductionDate;
        d.Reason = model.Reason;
        d.Month = model.Month;
        d.Year = model.Year;
        d.UpdatedAt = DateTime.UtcNow; d.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "تم تحديث الخصم";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        var d = await _db.Deductions.FindAsync(new object?[] { id }, ct);
        if (d is null) return NotFound();
        if (d.PayrollItemId.HasValue || await FinancialPeriodGuard.IsProtectedAsync(_db, d.Year, d.Month, ct))
        {
            TempData["Error"] = "لا يمكن تغيير اعتماد خصم تمت تسويته أو ينتمي لكشف رواتب معتمد أو مقفل.";
            return RedirectToAction(nameof(Index));
        }
        d.Status = d.Status == ApprovalStatus.Approved ? ApprovalStatus.Pending : ApprovalStatus.Approved;
        d.ApprovedBy = User.Identity?.Name;
        d.ApprovedAt = DateTime.UtcNow;
        d.UpdatedAt = DateTime.UtcNow; d.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        _audit.Log("تغيير اعتماد خصم", nameof(Deduction), d.Id.ToString(), null, d.Status.ToString(), User.Identity?.Name);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "تم تحديث حالة الخصم";
        return RedirectToAction(nameof(Index));
    }

    private async Task FillAsync(CancellationToken ct)
        => ViewBag.Employees = new SelectList(await _db.Employees.Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync(ct), "Id", "FullName");

    private void Validate(Deduction model)
    {
        model.Amount = PayrollCalculator.Round2(model.Amount);
        if (model.Amount <= 0) ModelState.AddModelError(nameof(Deduction.Amount), "المبلغ يجب أن يكون أكبر من صفر");
        if (string.IsNullOrWhiteSpace(model.Reason)) ModelState.AddModelError(nameof(Deduction.Reason), "السبب مطلوب");
        if (model.Month is < 1 or > 12) ModelState.AddModelError(nameof(Deduction.Month), "شهر غير صالح");
        if (model.Year is < 1 or > 9999) ModelState.AddModelError(nameof(Deduction.Year), "سنة غير صالحة");
    }
}
