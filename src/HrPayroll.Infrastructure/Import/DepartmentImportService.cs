using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HrPayroll.Infrastructure.Import;

/// <summary>صف معاينة استيراد قسم.</summary>
public sealed class DepartmentImportPreviewRow
{
    public int RowNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? ManagerName { get; set; }
    public string? Message { get; set; }
    public bool IsNew { get; set; }
    public bool IsInvalid { get; set; }
    public bool IsDuplicateInFile { get; set; }
}

/// <summary>نتيجة تحليل ملف الأقسام قبل الحفظ.</summary>
public sealed class DepartmentImportAnalysis
{
    public string FileHash { get; set; } = string.Empty;
    public List<DepartmentImportPreviewRow> Rows { get; set; } = new();
    public int NewCount => Rows.Count(r => r.IsNew && !r.IsInvalid);
    public int DuplicateCount => Rows.Count(r => r.IsDuplicateInFile);
    public int InvalidCount => Rows.Count(r => r.IsInvalid);
    public int ExistingCount => Rows.Count(r => !r.IsNew && !r.IsInvalid && !r.IsDuplicateInFile);
}

/// <summary>
/// استيراد الأقسام من Excel: تحليل ← معاينة ← تأكيد.
/// - تطبيع الأسماء (إزالة المسافات الزائدة) لمنع التكرار الشكلي.
/// - المقارنة غير حساسة لحالة الأحرف.
/// - لا يحذف قسمًا له موظفون؛ الأقسام الموجودة تُتخطَّى ولا تُستبدل.
/// </summary>
public class DepartmentImportService
{
    public const string DepartmentImportMarker = "Departments";

    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ILogger<DepartmentImportService> _logger;

    public DepartmentImportService(AppDbContext db, AuditService audit, ILogger<DepartmentImportService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public static string Normalize(string? s) =>
        string.Join(" ", (s ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>يحلل الملف ويعرض معاينة كاملة دون كتابة أي بيانات.</summary>
    public async Task<DepartmentImportAnalysis> AnalyzeAsync(byte[] bytes, string fileName, CancellationToken ct = default)
    {
        var format = AttendanceFileParser.DetectFormat(bytes);
        var sheet = AttendanceFileParser.Parse(bytes, format);
        var analysis = new DepartmentImportAnalysis { FileHash = EmployeeImportService.ComputeFileHash(bytes) };

        if (sheet.Rows.Count < 2) return analysis;

        var header = sheet.Rows[0].Select(x => (x ?? string.Empty).Trim()).ToArray();
        int NameIdx() => Array.FindIndex(header, h => h.Contains("القسم") || h.Contains("الاسم") || h.Equals("Name", StringComparison.OrdinalIgnoreCase));
        int CodeIdx() => Array.FindIndex(header, h => h.Contains("الكود") || h.Contains("الكود") || h.Equals("Code", StringComparison.OrdinalIgnoreCase));
        int MgrIdx() => Array.FindIndex(header, h => h.Contains("المدير") || h.Equals("Manager", StringComparison.OrdinalIgnoreCase));

        var ni = NameIdx(); var ci = CodeIdx(); var mi = MgrIdx();
        if (ni < 0)
        {
            _logger.LogWarning("تعذّر التعرف على عمود اسم القسم في {File}", fileName);
            return analysis;
        }

        var existingNames = (await _db.Departments.AsNoTracking().Select(d => d.Name).ToListAsync(ct))
            .Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            string? Cell(int idx) => idx >= 0 && idx < row.Length ? row[idx]?.Trim() : null;

            var name = Normalize(Cell(ni));
            var code = Cell(ci);
            if (!string.IsNullOrWhiteSpace(code)) code = code.Trim();
            var mgr = Cell(mi);

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(mgr))
                continue; // صف فارغ

            var preview = new DepartmentImportPreviewRow
            {
                RowNumber = r + 1, Name = name, Code = code, ManagerName = mgr
            };

            if (string.IsNullOrWhiteSpace(name))
            {
                preview.IsInvalid = true;
                preview.Message = "اسم القسم مطلوب.";
            }
            else if (!seen.Add(name))
            {
                preview.IsDuplicateInFile = true;
                preview.Message = "مكرر داخل الملف — سيُستورد أول ظهور فقط.";
            }
            else if (existingNames.Contains(name))
            {
                preview.Message = "موجود مسبقًا — سيُتخطَّى دون تعديل.";
            }
            else
            {
                preview.IsNew = true;
                preview.Message = "جديد — سيُضاف.";
            }
            analysis.Rows.Add(preview);
        }
        return analysis;
    }

    /// <summary>يحفظ الأقسام الجديدة فقط داخل معاملة واحدة.</summary>
    public async Task<(int Created, int Skipped)> CommitAsync(DepartmentImportAnalysis analysis,
        string fileName, string? userId, string? userName, CancellationToken ct = default)
    {
        var toAdd = analysis.Rows.Where(r => r.IsNew && !r.IsInvalid).ToList();
        if (toAdd.Count == 0) return (0, analysis.Rows.Count);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var existingNames = (await _db.Departments.Select(d => d.Name).ToListAsync(ct))
            .Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var batch = new ImportBatch
        {
            FileName = fileName,
            FileHash = analysis.FileHash,
            DetectedFormat = DepartmentImportMarker,
            ImportedAt = DateTime.UtcNow,
            ImportedByUserId = userId,
            ImportedByName = userName,
            SuccessRows = 0,
            InvalidRows = 0
        };
        _db.ImportBatches.Add(batch);

        foreach (var row in toAdd)
        {
            if (existingNames.Contains(row.Name)) continue; // سباق آمن: تحقق مزدوج
            _db.Departments.Add(new Department
            {
                Name = row.Name, Code = row.Code, ManagerName = row.ManagerName,
                IsActive = true, CreatedAt = DateTime.UtcNow, CreatedBy = userName
            });
            existingNames.Add(row.Name);
            created++;
        }

        batch.SuccessRows = created;
        batch.InvalidRows = analysis.Rows.Count - created;
        batch.Notes = $"استيراد أقسام: {created} جديد، {analysis.Rows.Count - created} متجاهَل.";
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("استيراد أقسام من Excel", nameof(Department), null, null,
            $"{fileName}: {created} جديد", userName, userId, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (created, analysis.Rows.Count - created);
    }
}
