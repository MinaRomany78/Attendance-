using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

[Authorize(Policy = "ManageLoans")]
public class LoansController : Controller
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public LoansController(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(int? year, int? month, CancellationToken ct)
    {
        var y = year ?? DateTime.Today.Year;
        var q = _db.Loans.Include(l => l.Employee).AsNoTracking().Where(l => l.DeductionYear == y);
        if (month.HasValue) q = q.Where(l => l.DeductionMonth == month.Value);
        ViewBag.Year = y; ViewBag.Month = month;
        return View(await q.OrderByDescending(l => l.LoanDate).ToListAsync(ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await FillAsync(ct);
        return View("Form", new Loan { LoanDate = DateTime.Today, DeductionMonth = DateTime.Today.Month, DeductionYear = DateTime.Today.Year, Installments = 1 });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Loan model, CancellationToken ct)
    {
        ValidateLoan(model);
        if (!ModelState.IsValid) { await FillAsync(ct); return View("Form", model); }

        model.CreatedBy = User.Identity?.Name;
        if (model.RepaymentStartYear <= 0) { model.RepaymentStartYear = model.DeductionYear; model.RepaymentStartMonth = model.DeductionMonth; }
        model.OutstandingBalance = model.Amount; // قرض جديد: لم يُسدد منه شيء بعد
        model.PaidAmount = 0m;
        _db.Loans.Add(model);
        await _db.SaveChangesAsync(ct);
        _audit.Log("إضافة سلفة", nameof(Loan), model.Id.ToString(), null, model.Amount.ToString("0.00"), User.Identity?.Name);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "تم تسجيل السلفة";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var loan = await _db.Loans.FindAsync(new object?[] { id }, ct);
        if (loan is null) return NotFound();
        if (loan.PayrollItemId.HasValue)
        {
            TempData["Error"] = "لا يمكن تعديل سلفة تم خصمها في كشف رواتب معتمد. ألغِ الاعتماد أولًا.";
            return RedirectToAction(nameof(Index));
        }
        await FillAsync(ct);
        return View("Form", loan);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Loan model, CancellationToken ct)
    {
        if (id != model.Id) return BadRequest();
        ValidateLoan(model);
        if (!ModelState.IsValid) { await FillAsync(ct); return View("Form", model); }

        var loan = await _db.Loans.FindAsync(new object?[] { id }, ct);
        if (loan is null) return NotFound();
        if (loan.PayrollItemId.HasValue)
        {
            TempData["Error"] = "لا يمكن تعديل سلفة تم خصمها في كشف رواتب";
            return RedirectToAction(nameof(Index));
        }

        loan.EmployeeId = model.EmployeeId;
        loan.Amount = model.Amount;
        loan.LoanDate = model.LoanDate;
        loan.DeductionMonth = model.DeductionMonth;
        loan.DeductionYear = model.DeductionYear;
        loan.Installments = model.Installments;
        loan.Notes = model.Notes;
        loan.UpdatedAt = DateTime.UtcNow; loan.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "تم تحديث السلفة";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        var loan = await _db.Loans.FindAsync(new object?[] { id }, ct);
        if (loan is null) return NotFound();
        loan.Status = loan.Status == ApprovalStatus.Approved ? ApprovalStatus.Pending : ApprovalStatus.Approved;
        loan.UpdatedAt = DateTime.UtcNow; loan.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        _audit.Log("تغيير حالة اعتماد سلفة", nameof(Loan), loan.Id.ToString(), null, loan.Status.ToString(), User.Identity?.Name);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "تم تحديث حالة السلفة";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>سجل أقساط قرض: المستحق/المحصل/المتخطى لكل شهر — يمنع الخصم المزدوج.</summary>
    [HttpGet]
    public async Task<IActionResult> Repayments(int id, CancellationToken ct)
    {
        var loan = await _db.Loans.Include(l => l.Employee).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loan is null) return NotFound();
        ViewBag.Repayments = await _db.LoanRepayments.AsNoTracking()
            .Where(r => r.LoanId == id).OrderBy(r => r.Year).ThenBy(r => r.Month).ToListAsync(ct);
        return View(loan);
    }

    /// <summary>ضبط جدول السداد: القسط الشهري وشهر البدء (لا يمس المحصل فعليًا).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSchedule(int id, decimal installmentAmount, int startYear, int startMonth, CancellationToken ct)
    {
        var loan = await _db.Loans.FindAsync(new object?[] { id }, ct);
        if (loan is null) return NotFound();
        if (loan.PayrollItemId.HasValue)
        {
            TempData["Error"] = "لا يمكن تغيير جدول قرض خُصم دفعة واحدة في كشف رواتب.";
            return RedirectToAction(nameof(Repayments), new { id });
        }
        if (installmentAmount <= 0 || installmentAmount > loan.Amount)
        {
            TempData["Error"] = "القسط الشهري يجب أن يكون بين 0 ومبلغ القرض.";
            return RedirectToAction(nameof(Repayments), new { id });
        }
        if (startMonth is < 1 or > 12)
        {
            TempData["Error"] = "شهر بدء السداد غير صالح.";
            return RedirectToAction(nameof(Repayments), new { id });
        }
        var old = $"{loan.InstallmentAmount}/{loan.RepaymentStartYear}-{loan.RepaymentStartMonth}";
        loan.InstallmentAmount = installmentAmount;
        loan.RepaymentStartYear = startYear;
        loan.RepaymentStartMonth = startMonth;
        loan.UpdatedAt = DateTime.UtcNow; loan.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        _audit.Log("ضبط جدول سداد قرض", nameof(Loan), loan.Id.ToString(), old,
            $"{installmentAmount}/{startYear}-{startMonth}", User.Identity?.Name);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "تم حفظ جدول السداد";
        return RedirectToAction(nameof(Repayments), new { id });
    }

    /// <summary>إيقاف/استئناف السداد بقرار معتمد — الموقوف لا تُنشأ له أقساط جديدة.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePause(int id, CancellationToken ct)
    {
        var loan = await _db.Loans.FindAsync(new object?[] { id }, ct);
        if (loan is null) return NotFound();
        loan.IsPaused = !loan.IsPaused;
        loan.UpdatedAt = DateTime.UtcNow; loan.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        _audit.Log(loan.IsPaused ? "إيقاف سداد قرض" : "استئناف سداد قرض", nameof(Loan),
            loan.Id.ToString(), null, loan.IsPaused.ToString(), User.Identity?.Name);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = loan.IsPaused ? "تم إيقاف السداد مؤقتًا" : "تم استئناف السداد";
        return RedirectToAction(nameof(Repayments), new { id });
    }

    /// <summary>تخطي قسط شهر: يبقى في الرصيد المتبقي ولا يُخصم من الراتب.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SkipMonth(int id, int year, int month, CancellationToken ct)
    {
        var loan = await _db.Loans.FindAsync(new object?[] { id }, ct);
        if (loan is null) return NotFound();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var row = await _db.LoanRepayments.FirstOrDefaultAsync(
            r => r.LoanId == id && r.Year == year && r.Month == month, ct);
        if (row is null)
        {
            row = new LoanRepayment
            {
                LoanId = id, EmployeeId = loan.EmployeeId, Year = year, Month = month,
                DueAmount = 0m, Status = LoanRepaymentStatus.Skipped,
                Notes = "تخطي بقرار معتمد قبل إنشاء الاستحقاق",
                CreatedAt = DateTime.UtcNow, CreatedBy = User.Identity?.Name
            };
            _db.LoanRepayments.Add(row);
        }
        else
        {
            if (row.Status == LoanRepaymentStatus.Collected || row.PayrollItemId.HasValue)
            {
                TempData["Error"] = "لا يمكن تخطي قسط تم تحصيله في كشف رواتب.";
                return RedirectToAction(nameof(Repayments), new { id });
            }
            row.Status = LoanRepaymentStatus.Skipped;
            row.Notes = "تخطي بقرار معتمد — المبلغ باقٍ في الرصيد";
            row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = User.Identity?.Name;
        }
        await _db.SaveChangesAsync(ct);
        _audit.Log("تخطي قسط قرض", nameof(LoanRepayment), row.Id.ToString(), null,
            $"قرض {id} شهر {year}-{month:00}", User.Identity?.Name);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        TempData["Success"] = "تم تخطي القسط — المبلغ باقٍ في الرصيد المتبقي ولن يُخصم";
        return RedirectToAction(nameof(Repayments), new { id });
    }

    private async Task FillAsync(CancellationToken ct)
        => ViewBag.Employees = new SelectList(await _db.Employees.Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync(ct), "Id", "FullName");

    private void ValidateLoan(Loan model)
    {
        if (model.Amount <= 0) ModelState.AddModelError(nameof(Loan.Amount), "المبلغ يجب أن يكون أكبر من صفر");
        if (model.DeductionMonth is < 1 or > 12) ModelState.AddModelError(nameof(Loan.DeductionMonth), "شهر غير صالح");
        if (model.Installments < 1) ModelState.AddModelError(nameof(Loan.Installments), "عدد الأقساط غير صالح");
    }
}
