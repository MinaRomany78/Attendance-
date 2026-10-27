using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

/// <summary>
/// مصاريف الموظفين: معاملات مستقلة قابلة للتتبع.
/// التسوية صريحة: خصم من الراتب، أو مشمول ضمن سلفة، أو مدفوع نقدًا (توثيق فقط).
/// </summary>
[Authorize(Policy = "ManageLoans")]
public class ExpensesController : Controller
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ExcelExportService _excel;

    public ExpensesController(AppDbContext db, AuditService audit, ExcelExportService excel)
    {
        _db = db;
        _audit = audit;
        _excel = excel;
    }

    public async Task<IActionResult> Index(int? year, int? month, int? employeeId, string? search, CancellationToken ct)
    {
        var y = year ?? DateTime.Today.Year;
        var q = _db.EmployeeExpenses.Include(e => e.Employee).AsNoTracking().Where(e => e.Year == y);
        if (month.HasValue) q = q.Where(e => e.Month == month.Value);
        if (employeeId.HasValue) q = q.Where(e => e.EmployeeId == employeeId.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(e => e.Employee!.FullName.Contains(s) || e.Employee!.EmployeeCode.Contains(s)
                             || e.Category.Contains(s) || (e.Description != null && e.Description.Contains(s)));
        }
        ViewBag.Year = y; ViewBag.Month = month; ViewBag.EmployeeId = employeeId; ViewBag.Search = search;
        ViewBag.Employees = new SelectList(await _db.Employees.AsNoTracking().Where(e => e.IsActive)
            .OrderBy(e => e.FullName).ToListAsync(ct), "Id", "FullName", employeeId);
        ViewBag.DeductibleTotal = await q
            .Where(e => e.Status == ApprovalStatus.Approved && e.SettlementMode == ExpenseSettlementMode.DeductFromPayroll)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
        return View(await q.OrderByDescending(e => e.ExpenseDate).ToListAsync(ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await FillAsync(null, ct);
        return View("Form", new EmployeeExpense
        {
            ExpenseDate = DateTime.Today, Month = DateTime.Today.Month, Year = DateTime.Today.Year, Currency = "EGP"
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(EmployeeExpense model, CancellationToken ct)
    {
        model.Month = model.ExpenseDate.Month;
        model.Year = model.ExpenseDate.Year;
        Validate(model);
        if (!ModelState.IsValid) { await FillAsync(model.SalaryAdvanceId, ct); return View("Form", model); }

        model.CreatedBy = this.CurrentUserName();
        _db.EmployeeExpenses.Add(model);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("إضافة مصروف موظف", nameof(EmployeeExpense), model.Id.ToString(),
            null, $"{model.Amount:N2} EGP - {model.Category}", this.CurrentUserName(), this.CurrentUserId(), ct);
        TempData["Success"] = "تم تسجيل المصروف — بانتظار الاعتماد";
        return RedirectToAction(nameof(Index), new { year = model.Year, month = model.Month });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var e = await _db.EmployeeExpenses.FindAsync(new object?[] { id }, ct);
        if (e is null) return NotFound();
        if (e.PayrollItemId.HasValue)
        {
            TempData["Error"] = "لا يمكن تعديل مصروف تمت تسويته في كشف رواتب.";
            return RedirectToAction(nameof(Index));
        }
        await FillAsync(e.SalaryAdvanceId, ct);
        return View("Form", e);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, EmployeeExpense model, CancellationToken ct)
    {
        if (id != model.Id) return BadRequest();
        var e = await _db.EmployeeExpenses.FindAsync(new object?[] { id }, ct);
        if (e is null) return NotFound();
        if (e.PayrollItemId.HasValue)
        {
            TempData["Error"] = "لا يمكن تعديل مصروف تمت تسويته في كشف رواتب.";
            return RedirectToAction(nameof(Index));
        }
        model.Month = model.ExpenseDate.Month;
        model.Year = model.ExpenseDate.Year;
        Validate(model);
        if (!ModelState.IsValid) { await FillAsync(model.SalaryAdvanceId, ct); return View("Form", model); }

        var old = $"{e.Amount:N2}/{e.Status}/{e.SettlementMode}";
        e.EmployeeId = model.EmployeeId;
        e.Amount = model.Amount;
        e.ExpenseDate = model.ExpenseDate;
        e.Month = model.Month; e.Year = model.Year;
        e.Category = model.Category;
        e.Description = model.Description;
        e.ReceiptReference = model.ReceiptReference;
        e.SettlementMode = model.SettlementMode;
        e.SalaryAdvanceId = model.SettlementMode == ExpenseSettlementMode.IncludedInAdvance ? model.SalaryAdvanceId : null;
        e.Notes = model.Notes;
        e.UpdatedAt = DateTime.UtcNow; e.UpdatedBy = this.CurrentUserName();
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("تعديل مصروف موظف", nameof(EmployeeExpense), e.Id.ToString(),
            old, $"{e.Amount:N2}/{e.Status}/{e.SettlementMode}", this.CurrentUserName(), this.CurrentUserId(), ct);
        TempData["Success"] = "تم تحديث المصروف";
        return RedirectToAction(nameof(Index), new { year = e.Year, month = e.Month });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        var e = await _db.EmployeeExpenses.FindAsync(new object?[] { id }, ct);
        if (e is null) return NotFound();
        if (e.PayrollItemId.HasValue)
        {
            TempData["Error"] = "لا يمكن تغيير اعتماد مصروف تمت تسويته في كشف رواتب.";
            return RedirectToAction(nameof(Index));
        }
        var old = e.Status.ToString();
        e.Status = e.Status == ApprovalStatus.Approved ? ApprovalStatus.Pending : ApprovalStatus.Approved;
        e.ApprovedBy = this.CurrentUserName();
        e.ApprovedAt = DateTime.UtcNow;
        e.UpdatedAt = DateTime.UtcNow; e.UpdatedBy = this.CurrentUserName();
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("تغيير اعتماد مصروف", nameof(EmployeeExpense), e.Id.ToString(),
            old, e.Status.ToString(), this.CurrentUserName(), this.CurrentUserId(), ct);
        TempData["Success"] = "تم تحديث حالة المصروف";
        return RedirectToAction(nameof(Index), new { year = e.Year, month = e.Month });
    }

    public async Task<IActionResult> ExportExcel(int year, int month, CancellationToken ct)
    {
        var bytes = await _excel.ExportExpensesAsync(year, month, ct);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Expenses_{year}_{month:00}.xlsx");
    }

    private async Task FillAsync(int? advanceId, CancellationToken ct)
    {
        ViewBag.Employees = new SelectList(await _db.Employees.AsNoTracking().Where(e => e.IsActive)
            .OrderBy(e => e.FullName).ToListAsync(ct), "Id", "FullName");
        var advances = await _db.SalaryAdvances.AsNoTracking()
            .Include(a => a.Employee)
            .Where(a => a.Status == SalaryAdvanceStatus.Approved || a.Status == SalaryAdvanceStatus.Paid)
            .OrderByDescending(a => a.Year).ThenByDescending(a => a.Month).Take(200).ToListAsync(ct);
        ViewBag.Advances = new SelectList(
            advances.Select(a => new { a.Id, Text = $"{a.Employee?.FullName} — {a.Year}/{a.Month:00} ({a.ApprovedAmount:N2} EGP)" }),
            "Id", "Text", advanceId);
    }

    private void Validate(EmployeeExpense model)
    {
        if (model.Amount <= 0) ModelState.AddModelError(nameof(EmployeeExpense.Amount), "المبلغ يجب أن يكون أكبر من صفر");
        if (string.IsNullOrWhiteSpace(model.Category)) ModelState.AddModelError(nameof(EmployeeExpense.Category), "الفئة مطلوبة");
        if (model.ExpenseDate.Date > DateTime.Today) ModelState.AddModelError(nameof(EmployeeExpense.ExpenseDate), "تاريخ المصروف لا يمكن أن يكون في المستقبل");
        if (model.SettlementMode == ExpenseSettlementMode.IncludedInAdvance && !model.SalaryAdvanceId.HasValue)
            ModelState.AddModelError(nameof(EmployeeExpense.SalaryAdvanceId), "اختر السلفة المشمول فيها هذا المصروف");
    }
}
