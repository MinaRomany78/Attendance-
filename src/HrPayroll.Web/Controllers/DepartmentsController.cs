using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using HrPayroll.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

/// <summary>إدارة بيانات الأقسام المرجعية.</summary>
[Authorize(Policy = "ManageEmployees")]
public class DepartmentsController : Controller
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    private const long MaxFileSize = 10 * 1024 * 1024;

    public DepartmentsController(AppDbContext db, AuditService audit,
        DepartmentImportService import, ExcelExportService excel)
    {
        _db = db;
        _audit = audit;
        _import = import;
        _excel = excel;
    }

    private readonly DepartmentImportService _import;
    private readonly ExcelExportService _excel;

    public async Task<IActionResult> Index(string? search, bool? isActive, CancellationToken ct)
    {
        var q = _db.Departments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(d => d.Name.Contains(s) || (d.Code != null && d.Code.Contains(s)));
        }
        if (isActive.HasValue) q = q.Where(d => d.IsActive == isActive.Value);

        ViewBag.Search = search;
        ViewBag.IsActive = isActive;
        var departments = await q.OrderBy(d => d.Name).ToListAsync(ct);
        ViewBag.EmployeeCounts = await _db.Employees.AsNoTracking()
            .Where(e => e.DepartmentId != null)
            .GroupBy(e => e.DepartmentId!.Value)
            .Select(g => new { DepartmentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DepartmentId, x => x.Count, ct);
        ViewBag.JobTitleCounts = await _db.JobTitles.AsNoTracking()
            .Where(j => j.DepartmentId != null)
            .GroupBy(j => j.DepartmentId!.Value)
            .Select(g => new { DepartmentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DepartmentId, x => x.Count, ct);
        return View(departments);
    }

    /// <summary>الهيكل التنظيمي: قسم ← مسمى وظيفي ← موظفون، من البيانات الفعلية.</summary>
    [HttpGet]
    public async Task<IActionResult> Structure(string? search, CancellationToken ct)
    {
        var q = _db.Employees.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(e => e.FullName.Contains(s) || e.EmployeeCode.Contains(s));
        }
        var employees = await q.OrderBy(e => e.FullName).ToListAsync(ct);
        var departments = await _db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        ViewBag.Search = search;
        ViewBag.Departments = departments;
        ViewBag.Employees = employees;
        return View(employees);
    }

    [HttpGet]
    public IActionResult Create() => View("Form", new DepartmentFormViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(DepartmentFormViewModel vm, CancellationToken ct)
    {
        if (await _db.Departments.AnyAsync(d => d.Name == vm.Name.Trim(), ct))
            ModelState.AddModelError(nameof(vm.Name), "يوجد قسم بنفس الاسم");

        if (!ModelState.IsValid) return View("Form", vm);

        var dept = new Department
        {
            Name = vm.Name.Trim(),
            Code = vm.Code?.Trim(),
            ManagerName = vm.ManagerName,
            IsActive = vm.IsActive,
            Notes = vm.Notes,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = this.CurrentUserName()
        };
        _db.Departments.Add(dept);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("إضافة قسم", nameof(Department), dept.Id.ToString(), null, dept.Name, this.CurrentUserName(), this.CurrentUserId(), ct);

        TempData["Success"] = "تم إضافة القسم بنجاح";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var dept = await _db.Departments.FindAsync(new object?[] { id }, ct);
        if (dept is null) return NotFound();
        return View("Form", new DepartmentFormViewModel
        {
            Id = dept.Id, Name = dept.Name, Code = dept.Code, ManagerName = dept.ManagerName,
            IsActive = dept.IsActive, Notes = dept.Notes
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, DepartmentFormViewModel vm, CancellationToken ct)
    {
        if (id != vm.Id) return BadRequest();
        if (await _db.Departments.AnyAsync(d => d.Name == vm.Name.Trim() && d.Id != id, ct))
            ModelState.AddModelError(nameof(vm.Name), "يوجد قسم بنفس الاسم");
        if (!ModelState.IsValid) return View("Form", vm);

        var dept = await _db.Departments.FindAsync(new object?[] { id }, ct);
        if (dept is null) return NotFound();

        var old = $"{dept.Name}|{dept.Code}|{dept.IsActive}";
        dept.Name = vm.Name.Trim();
        dept.Code = vm.Code?.Trim();
        dept.ManagerName = vm.ManagerName;
        dept.IsActive = vm.IsActive;
        dept.Notes = vm.Notes;
        dept.UpdatedAt = DateTime.UtcNow;
        dept.UpdatedBy = this.CurrentUserName();
        await SyncEmployeeDepartmentNameAsync(dept, ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("تعديل قسم", nameof(Department), dept.Id.ToString(), old,
            $"{dept.Name}|{dept.Code}|{dept.IsActive}", this.CurrentUserName(), this.CurrentUserId(), ct);

        TempData["Success"] = "تم تحديث القسم";
        return RedirectToAction(nameof(Index));
    }

    // ===================== استيراد/تصدير Excel =====================

    [HttpGet]
    public IActionResult Import() => View(new DepartmentImportPreview());

    [HttpGet]
    public async Task<IActionResult> DownloadTemplate(CancellationToken ct)
    {
        await Task.CompletedTask;
        var bytes = await _excel.BuildDepartmentImportTemplateAsync(ct);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Department_Import_Template.xlsx");
    }

    [HttpGet]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var bytes = await _excel.ExportDepartmentsAsync(ct);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Departments.xlsx");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "الرجاء اختيار ملف Excel صالح.";
            return RedirectToAction(nameof(Import));
        }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        DepartmentImportAnalysis analysis;
        try { analysis = await _import.AnalyzeAsync(ms.ToArray(), file.FileName, ct); }
        catch (Exception)
        {
            TempData["Error"] = "تعذّرت قراءة الملف. تأكد من أنه ملف Excel صالح.";
            return RedirectToAction(nameof(Import));
        }
        if (analysis.Rows.Count == 0)
        {
            TempData["Error"] = "لم يتم التعرف على عمود اسم القسم في الملف.";
            return RedirectToAction(nameof(Import));
        }
        TempData["ImportFile"] = Convert.ToBase64String(ms.ToArray());
        TempData["ImportFileName"] = file.FileName;
        return View(new DepartmentImportPreview { Analysis = analysis, FileName = file.FileName });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportConfirm(CancellationToken ct)
    {
        var b64 = TempData["ImportFile"] as string;
        var fileName = TempData["ImportFileName"] as string ?? "departments.xlsx";
        if (string.IsNullOrEmpty(b64))
        {
            TempData["Error"] = "انتهت صلاحية المعاينة. الرجاء رفع الملف مرة أخرى.";
            return RedirectToAction(nameof(Import));
        }
        var bytes = Convert.FromBase64String(b64);
        var analysis = await _import.AnalyzeAsync(bytes, fileName, ct);
        var (created, skipped) = await _import.CommitAsync(analysis, fileName,
            this.CurrentUserId(), this.CurrentUserName(), ct);
        TempData["Success"] = $"تم الاستيراد: {created} قسم جديد، {skipped} متجاهَل (مكرر/موجود/غير صالح).";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// حذف قسم: مسموح فقط عندما لا يرتبط به موظفون أو مسميات وظيفية.
    /// القسم الذي له موظفون يُعطَّل ولا يُحذف حفاظًا على التاريخ.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPolicies.AdminOnly)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var dept = await _db.Departments.FindAsync(new object?[] { id }, ct);
        if (dept is null) return NotFound();
        var empCount = await _db.Employees.CountAsync(e => e.DepartmentId == id, ct);
        var jobCount = await _db.JobTitles.CountAsync(j => j.DepartmentId == id, ct);
        if (empCount > 0 || jobCount > 0)
        {
            TempData["Error"] = $"لا يمكن حذف القسم: مرتبط به {empCount} موظف و{jobCount} مسمى وظيفي. عطّله بدلًا من حذفه.";
            return RedirectToAction(nameof(Index));
        }
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.Departments.Remove(dept);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("حذف قسم", nameof(Department), id.ToString(),
            dept.Name, null, this.CurrentUserName(), this.CurrentUserId(), ct);
        await tx.CommitAsync(ct);
        TempData["Success"] = "تم حذف القسم";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, CancellationToken ct)
    {
        var dept = await _db.Departments.FindAsync(new object?[] { id }, ct);
        if (dept is null) return NotFound();
        dept.IsActive = !dept.IsActive;
        dept.UpdatedAt = DateTime.UtcNow;
        dept.UpdatedBy = this.CurrentUserName();
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync(dept.IsActive ? "تفعيل قسم" : "تعطيل قسم", nameof(Department), dept.Id.ToString(),
            null, dept.IsActive.ToString(), this.CurrentUserName(), this.CurrentUserId(), ct);
        TempData["Success"] = dept.IsActive ? "تم تفعيل القسم" : "تم تعطيل القسم";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>يحدّث النص المخزّن على الموظفين عند إعادة تسمية القسم.</summary>
    private async Task SyncEmployeeDepartmentNameAsync(Department dept, CancellationToken ct)
    {
        var employees = await _db.Employees.Where(e => e.DepartmentId == dept.Id).ToListAsync(ct);
        foreach (var e in employees) e.Department = dept.Name;
    }
}
