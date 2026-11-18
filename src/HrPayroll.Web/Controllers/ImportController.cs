using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using HrPayroll.Domain.Enums;
using HrPayroll.Web.Services;
using HrPayroll.Web.ViewModels;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

/// <summary>
/// استيراد ملفات البصمة: منطقة مستقلة لملف الدخول ومنطقة مستقلة لملف الخروج،
/// ثم معاينة موحّدة لكل ملف على حدة (مقبول/مكرر/يحتاج مراجعة/فترة البيانات)
/// وتأكيد الاستيراد مع معالجة الحضور.
/// </summary>
[Authorize(Policy = "ManageAttendance")]
public class ImportController : Controller
{
    private const long MaxFileSize = 25 * 1024 * 1024; // 25 MB لكل ملف
    private readonly AttendanceImportStagingStore _staging;
    private string Owner => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException("يجب تسجيل الدخول لمتابعة الاستيراد.");

    /// <summary>أقصى فترة تُعالج تلقائيًا بعد الاستيراد (حماية من نطاق تاريخ غير منطقي في الملف).</summary>
    private const int MaxAutoProcessDays = 92;

    private readonly AttendanceImportService _import;
    private readonly AttendanceProcessingService _processing;
    private readonly AppDbContext _db;
    private readonly ILogger<ImportController> _logger;

    public ImportController(
        AttendanceImportService import,
        AttendanceProcessingService processing,
        AppDbContext db,
        ILogger<ImportController> logger,
        IDataProtectionProvider protection)
    {
        _import = import;
        _processing = processing;
        _db = db;
        _logger = logger;
        _staging = new AttendanceImportStagingStore(protection);
    }

    private const string EmployeeImportServiceMarker = HrPayroll.Infrastructure.Import.EmployeeImportService.EmployeeImportMarker;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewBag.Batches = await _db.ImportBatches.AsNoTracking()
            .Where(b => b.DetectedFormat != EmployeeImportServiceMarker)
            .OrderByDescending(b => b.ImportedAt).Take(30).ToListAsync(ct);
        ViewBag.Devices = await _db.AttendanceDevices.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return View();
    }

    /// <summary>
    /// يستقبل ملف الدخول و/أو ملف الخروج في منطقتين منفصلتين، ويتحقق من كل ملف على حدة
    /// دون كتابة أي بيانات، ثم يعرض معاينة مفصلة لكل ملف.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxFileSize * 3 + 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? entryFile, IFormFile? exitFile, CancellationToken ct, IFormFile? combinedFile = null)
    {
        var zones = new List<ImportFileZoneViewModel>
        {
            BuildZone("combined", "ملف البصمة الموحد", "↔",
                "ملف واحد يحتوي على حركات الدخول والخروج الصريحة I / O.", "combinedFile", combinedFile),
            BuildZone("entry", "رفع ملف الدخول", "⬇️",
                "ملف تسجيل الدخول إلى العمل كما صُدِّر من جهاز البصمة.", "entryFile", entryFile),
            BuildZone("exit", "رفع ملف الخروج", "⬆️",
                "ملف تسجيل الخروج من العمل كما صُدِّر من جهاز البصمة.", "exitFile", exitFile)
        };

        if (zones.All(z => !z.Provided))
        {
            TempData["Error"] = "لم يتم اختيار ملف. اختر ملف البصمة الموحد أو ملفات الدخول والخروج.";
            return RedirectToAction(nameof(Index));
        }

        if (combinedFile is { Length: > 0 } && (entryFile is { Length: > 0 } || exitFile is { Length: > 0 }))
        {
            TempData["Error"] = "اختر طريقة استيراد واحدة: الملف الموحد أو الملفات المنفصلة.";
            return RedirectToAction(nameof(Index));
        }

        foreach (var zone in zones.Where(z => z.Provided))
        {
            var file = zone.Key switch { "combined" => combinedFile!, "entry" => entryFile!, _ => exitFile! };

            if (file.Length > MaxFileSize)
            {
                zone.Error = $"حجم الملف أكبر من الحد المسموح (25 ميجابايت).";
                continue;
            }

            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();

                zone.Analysis = await _import.AnalyzeAsync(bytes, Path.GetFileName(file.FileName), ct, DirectionFor(zone.Key));
                if (!zone.Analysis.HasMapping)
                {
                    zone.Error = "لم يتم التعرف على أعمدة كود الموظف ووقت البصمة في هذا الملف. تأكد من أنه ملف بصمات صادر من الجهاز.";
                    continue;
                }

                zone.Token = await _staging.StageAsync(bytes, Owner, zone.Key, file.FileName, ct, MappingFingerprint(zone.Analysis));
            }
            catch (InvalidDataException ex) { zone.Error = ex.Message; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "تعذّر تحليل ملف البصمة {FileName}", file.FileName);
                zone.Error = "تعذّرت قراءة الملف. تأكد من أنه ملف بصمات صالح غير تالف أو مفتوح في برنامج آخر.";
            }
        }

        MarkCrossFileDuplicates(zones);
        await LoadMappingChoicesAsync(ct);
        return View("Preview", new ImportFilesViewModel { Zones = zones });
    }

    /// <summary>يؤكّد استيراد الملفات الصالحة (كل ملف على حدة) ثم يعالج الحضور للفترة المستخلصة.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(
        string? entryToken, string? entryName, string? exitToken, string? exitName,
        bool processAttendance = true, CancellationToken ct = default, string? combinedToken = null, string? combinedName = null)
    {
        var staged = new[]
        {
            (Title: "ملف البصمة الموحد", Token: combinedToken, Name: combinedName),
            (Title: "ملف الدخول", Token: entryToken, Name: entryName),
            (Title: "ملف الخروج", Token: exitToken, Name: exitName)
        }.Where(s => !string.IsNullOrWhiteSpace(s.Token)).ToList();

        if (staged.Count == 0)
        {
            TempData["Error"] = "انتهت صلاحية المعاينة. الرجاء رفع الملفات مرة أخرى.";
            return RedirectToAction(nameof(Index));
        }

        var messages = new List<string>();
        var errors = new List<string>();
        var importedBatchIds = new List<int>();
        DateTime? periodFrom = null;
        DateTime? periodTo = null;

        foreach (var s in staged)
        {
            try
            {
                var (file, bytes) = await _staging.ReadAsync(s.Token!, Owner, ct);
                var fileName = file.FileName;
                var analysis = await _import.AnalyzeAsync(bytes, fileName, ct, DirectionFor(file.Zone));

                if (file.MappingFingerprint != MappingFingerprint(analysis))
                {
                    errors.Add($"{fileName}: تغير ربط الموظفين بعد المعاينة. أعد رفع الملف ومراجعة المطابقة قبل التأكيد.");
                    continue;
                }

                if (!analysis.HasMapping)
                {
                    errors.Add($"{s.Title} ({fileName}): ملف غير صالح — تعذّر التعرف على الأعمدة المطلوبة.");
                    continue;
                }

                var result = await _import.CommitAsync(analysis, fileName,
                    User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name, ct);

                importedBatchIds.Add(result.ImportBatchId);

                var times = analysis.Rows.Where(r => r.Status == ImportRowStatus.Success && r.PunchTime.HasValue)
                    .Select(r => r.PunchTime!.Value).ToList();
                if (times.Count > 0)
                {
                    // A checkout can complete the previous day's overnight shift.
                    var fileFrom = times.Min().Date > DateTime.MinValue.Date ? times.Min().Date.AddDays(-1) : times.Min().Date;
                    var fileTo = times.Max().Date;
                    periodFrom = periodFrom is null || fileFrom < periodFrom ? fileFrom : periodFrom;
                    periodTo = periodTo is null || fileTo > periodTo ? fileTo : periodTo;
                }

                messages.Add(
                    $"{s.Title} ({fileName}): تمت قراءة {analysis.Total} سجل — مقبول {result.Inserted} — " +
                    $"مكرر {result.SkippedDuplicate} — غير مستورد للتحقق {result.Failed}");
            }
            catch (InvalidDataException ex) { errors.Add($"{s.Title}: {ex.Message}"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "تعذّر استيراد {Title} ({FileName})", s.Title, s.Name);
                errors.Add($"{s.Title} ({s.Name}): فشل الاستيراد بسبب خطأ غير متوقع. راجع سجل النظام ثم أعد المحاولة.");
            }
            finally
            {
                try { _staging.Delete(s.Token!, Owner); } catch (InvalidDataException) { /* Untrusted/expired token. */ }
            }
        }

        if (messages.Count > 0)
            TempData["Success"] = "نتيجة رفع الملفات — " + string.Join(" | ", messages);
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" | ", errors);

        if (importedBatchIds.Count == 0)
            return RedirectToAction(nameof(Index));

        // ===== معالجة الحضور للفترة التي غطّتها الملفات المستوردة =====
        if (processAttendance && periodFrom.HasValue && periodTo.HasValue)
        {
            var from = periodFrom.Value;
            var to = periodTo.Value;
            if (to < from) (from, to) = (to, from);
            if ((to - from).TotalDays > MaxAutoProcessDays)
            {
                to = from.AddDays(MaxAutoProcessDays);
                TempData["Warning"] =
                    $"فترة البيانات في الملف كبيرة، لذلك تمت معالجة أول {MaxAutoProcessDays} يوم فقط. " +
                    "أعد المعالجة لبقية الفترة من شاشة الحضور اليومي.";
            }

            try
            {
                var processed = await _processing.ProcessAsync(from, to, null, this.CurrentUserName(), ct);
                var text = $"تمت معالجة الحضور من {from:yyyy-MM-dd} إلى {to:yyyy-MM-dd}: " +
                           $"{processed.EmployeesProcessed} موظف، {processed.DaysProcessed} يوم، " +
                           $"غياب {processed.AbsentDays}، تأخير {processed.LateDays}، إضافي {processed.OvertimeDays}، " +
                           $"يحتاج مراجعة {processed.ReviewCases}.";
                TempData["Success"] = (TempData["Success"] as string) is { Length: > 0 } prior
                    ? prior + " — " + text
                    : text;

                if (processed.Warnings.Count > 0)
                    TempData["Warning"] = string.Join(" | ", processed.Warnings.Take(5));

                return RedirectToAction("Index", "Attendance", new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "تعذّرت معالجة الحضور بعد الاستيراد {From}-{To}", from, to);
                TempData["Error"] = (TempData["Error"] as string) is { Length: > 0 } priorErr
                    ? priorErr + " | تعذّرت معالجة الحضور لهذه الفترة، ويمكنك إعادة المعالجة من شاشة الحضور اليومي."
                    : "تم الاستيراد بنجاح، لكن تعذّرت معالجة الحضور لهذه الفترة. أعد المعالجة من شاشة الحضور اليومي.";
            }
        }

        return importedBatchIds.Count == 1
            ? RedirectToAction(nameof(Details), new { id = importedBatchIds[0] })
            : RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var batch = await _db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound();
        ViewBag.Punches = await _db.RawPunches.AsNoTracking().Where(p => p.ImportBatchId == id)
            .OrderBy(p => p.PunchTime).Take(100).ToListAsync(ct);
        var period = await _db.RawPunches.Where(p => p.ImportBatchId == id).GroupBy(p => p.ImportBatchId)
            .Select(g => new { From = g.Min(p => p.PunchTime), To = g.Max(p => p.PunchTime) }).FirstOrDefaultAsync(ct);
        ViewBag.PeriodFrom = period?.From;
        ViewBag.PeriodTo = period?.To;
        return View(batch);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Validation(string token, CancellationToken ct, List<string>? tokens = null)
    {
        try
        {
            var preview = await ReadPreviewAsync((tokens ?? new List<string>()).Append(token)
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(3), ct);
            var analysis = preview.Zones.FirstOrDefault(z => z.Token == token)?.Analysis;
            if (analysis is null) throw new InvalidDataException("المعاينة غير صالحة أو انتهت صلاحيتها. أعد رفع الملف.");
            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("نتائج التحقق");
            sheet.RightToLeft = true;
            var headers = new[] { "الصف", "كود البصمة", "الاسم من الملف", "الموظف في النظام", "الوقت المحلي", "الحركة", "الجهاز", "الحالة", "رسالة التحقق", "الحقول الأصلية" };
            for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
            for (var i = 0; i < analysis.Rows.Count; i++)
            {
                var row = analysis.Rows[i];
                var values = new[] { row.RowNumber.ToString(), row.DeviceEmployeeCode, row.Name, row.MatchedEmployeeName,
                    row.PunchTime?.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture) ?? row.RawPunchTime,
                    row.AttType, row.DeviceId, StatusArabic(row.Status), row.Message, System.Text.Json.JsonSerializer.Serialize(row.OriginalFields) };
                for (var c = 0; c < values.Length; c++) sheet.Cell(i + 2, c + 1).Value = values[c] ?? string.Empty;
            }
            sheet.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
            sheet.SheetView.FreezeRows(1);
            sheet.RangeUsed()?.SetAutoFilter();
            sheet.Columns().Width = 22;
            sheet.Column(9).Width = 65;
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "attendance-validation.xlsx");
        }
        catch (InvalidDataException ex) { TempData["Error"] = ex.Message; return RedirectToAction(nameof(Index)); }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = "ManageEmployees")]
    public async Task<IActionResult> ResolveMapping(string token, string deviceEmployeeCode, int employeeId,
        List<string>? tokens, CancellationToken ct)
    {
        var selected = (tokens ?? new List<string>()).Append(token).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(3).ToList();
        try
        {
            var (file, bytes) = await _staging.ReadAsync(token, Owner, ct);
            var analysis = await _import.AnalyzeAsync(bytes, file.FileName, ct, DirectionFor(file.Zone));
            if (!analysis.Rows.Any(r => r.Status == ImportRowStatus.UnknownEmployee
                && string.Equals(r.DeviceEmployeeCode, EmployeeAttendanceMatcher.NormalizeCode(deviceEmployeeCode), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("هذا الكود ليس من الأكواد غير المطابقة في المعاينة الحالية.");
            var service = new AttendanceEmployeeMappingService(_db, new AuditService(_db));
            await service.ResolveAsync(deviceEmployeeCode, employeeId, Owner, User.Identity?.Name, ct);
            TempData["Success"] = "تم حفظ ربط كود البصمة وإعادة تحليل السجلات. لم تتغير أي بيانات مالية.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or DbUpdateException)
        {
            TempData["Error"] = ex is DbUpdateException ? "تعذر حفظ الربط؛ قد يكون الكود مرتبطًا بواسطة مستخدم آخر. أعد التحليل." : ex.Message;
        }
        return View("Preview", await ReadPreviewAsync(selected, ct, refreshMapping: true));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Cancel(List<string> tokens)
    {
        foreach (var token in tokens.Distinct().Take(3))
            try { _staging.Delete(token, Owner); } catch (InvalidDataException) { /* Cannot delete another user's upload. */ }
        TempData["Success"] = "تم إلغاء المعاينة وحذف الملفات المؤقتة الخاصة بك.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public IActionResult Roster() => View(new RosterReconciliationResult());

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxFileSize + 1024 * 1024)]
    public async Task<IActionResult> Roster(IFormFile? rosterFile, CancellationToken ct)
    {
        var result = new RosterReconciliationResult();
        if (rosterFile is not { Length: > 0 } || rosterFile.Length > MaxFileSize)
        {
            ModelState.AddModelError(string.Empty, "اختر ملف قائمة موظفي الجهاز بحجم لا يتجاوز 25 ميجابايت.");
            return View(result);
        }
        try
        {
            using var stream = new MemoryStream();
            await rosterFile.CopyToAsync(stream, ct);
            result = await new ZkRosterReconciliationService(_db).AnalyzeAsync(stream.ToArray(), ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        { ModelState.AddModelError(string.Empty, ex.Message); }
        return View(result);
    }

    private async Task<ImportFilesViewModel> ReadPreviewAsync(IEnumerable<string> tokens, CancellationToken ct, bool refreshMapping = false)
    {
        var zones = new List<ImportFileZoneViewModel>();
        foreach (var token in tokens)
        {
            var zone = new ImportFileZoneViewModel { Provided = true, Token = token };
            zones.Add(zone);
            try
            {
                var (file, bytes) = await _staging.ReadAsync(token, Owner, ct);
                zone.Key = file.Zone;
                zone.Title = file.Zone switch { "entry" => "ملف الدخول", "exit" => "ملف الخروج", _ => "ملف البصمة الموحد" };
                zone.FileName = file.FileName;
                zone.Analysis = await _import.AnalyzeAsync(bytes, file.FileName, ct, DirectionFor(file.Zone));
                if (refreshMapping) zone.Token = _staging.RefreshMapping(token, Owner, MappingFingerprint(zone.Analysis));
            }
            catch (InvalidDataException ex) { zone.Error = ex.Message; }
        }
        MarkCrossFileDuplicates(zones);
        await LoadMappingChoicesAsync(ct);
        return new ImportFilesViewModel { Zones = zones };
    }

    private async Task LoadMappingChoicesAsync(CancellationToken ct)
    {
        var employees = await _db.Employees.AsNoTracking().OrderBy(e => e.FullName)
            .Select(e => new { e.Id, e.FullName, e.EmployeeCode, e.DeviceEmployeeCode }).ToListAsync(ct);
        ViewBag.Employees = employees.Where(e => string.IsNullOrWhiteSpace(e.DeviceEmployeeCode))
            .Select(e => new SelectListItem($"{e.FullName} ({e.EmployeeCode})", e.Id.ToString())).ToList();
    }

    private static void MarkCrossFileDuplicates(IEnumerable<ImportFileZoneViewModel> zones)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in zones.Where(z => z.Ok).SelectMany(z => z.Analysis!.Rows).Where(r => r.Status == ImportRowStatus.Success))
        {
            var key = AttendanceImportService.NaturalKey($"employee:{row.EmployeeId}", row.PunchTime!.Value, row.AttType);
            if (seen.Add(key)) continue;
            row.Status = ImportRowStatus.Duplicate;
            row.Message = "مكرر بين الملفات في المعاينة الحالية.";
        }
    }

    private static PunchDirection DirectionFor(string zone)
        => zone switch { "entry" => PunchDirection.In, "exit" => PunchDirection.Out, _ => PunchDirection.Unknown };

    private static string MappingFingerprint(ImportAnalysisResult analysis)
    {
        var identities = analysis.Rows.Where(r => !string.IsNullOrWhiteSpace(r.DeviceEmployeeCode))
            .Select(r => new { Code = r.DeviceEmployeeCode!.Trim().ToUpperInvariant(), r.EmployeeId })
            .Distinct().OrderBy(r => r.Code, StringComparer.Ordinal).ThenBy(r => r.EmployeeId).ToList();
        return AttendanceImportService.ComputeFileHash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(identities));
    }

    private static string StatusArabic(ImportRowStatus status) => status switch
    {
        ImportRowStatus.Success => "صالح", ImportRowStatus.Duplicate => "مكرر", ImportRowStatus.UnknownEmployee => "موظف غير مطابق",
        ImportRowStatus.NeedsReview => "يحتاج مراجعة", ImportRowStatus.InvalidDateTime => "وقت غير صالح", _ => "سجل غير صالح"
    };

    private static ImportFileZoneViewModel BuildZone(
        string key, string title, string icon, string hint, string inputName, IFormFile? file)
        => new()
        {
            Key = key,
            Title = title,
            Icon = icon,
            Hint = hint,
            InputName = inputName,
            Provided = file is { Length: > 0 },
            FileName = file is { Length: > 0 } ? Path.GetFileName(file.FileName) : null
        };
}
