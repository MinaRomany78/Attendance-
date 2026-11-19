using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HrPayroll.Infrastructure.Import;

public sealed class ImportPreviewRow
{
    public int RowNumber { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceEmployeeCode { get; set; }
    public string? Name { get; set; }
    public string? AttType { get; set; }
    public string? NormalizedAttType { get; set; }
    public string? RawPunchTime { get; set; }
    public DateTime? PunchTime { get; set; }
    public ImportRowStatus Status { get; set; }
    public string? Message { get; set; }
    public int? EmployeeId { get; set; }
    public string? MatchedEmployeeName { get; set; }
    public string DedupHash { get; set; } = string.Empty;
    public bool AlreadyImported { get; set; }
    public string[] OriginalFields { get; set; } = Array.Empty<string>();
    public string? Source { get; set; }
}

public sealed class ImportAnalysisResult
{
    public AttendanceFileFormat Format { get; set; }
    public ColumnMapping? Mapping { get; set; }
    public bool UsedFallback { get; set; }
    public string? EncodingName { get; set; }
    public PunchDirection ExpectedDirection { get; set; }
    public List<string> Headers { get; set; } = new();
    public List<ImportPreviewRow> Rows { get; set; } = new();
    public List<ImportPreviewRow> Preview { get; set; } = new();
    public string FileHash { get; set; } = string.Empty;
    public int Total => Rows.Count;
    public int SuccessCount => Rows.Count(r => r.Status == ImportRowStatus.Success);
    public int DuplicateCount => Rows.Count(r => r.Status == ImportRowStatus.Duplicate);
    public int InvalidCount => Rows.Count(r => r.Status is ImportRowStatus.InvalidDateTime or ImportRowStatus.InvalidRow);
    public int UnknownEmployeeCount => Rows.Count(r => r.Status == ImportRowStatus.UnknownEmployee);
    public int ReviewCount => Rows.Count(r => r.Status == ImportRowStatus.NeedsReview);
    public int CheckInCount => Rows.Count(r => PunchClassifier.ParseExplicitDirection(r.AttType) == PunchDirection.In);
    public int CheckOutCount => Rows.Count(r => PunchClassifier.ParseExplicitDirection(r.AttType) == PunchDirection.Out);
    public int AlreadyImportedCount => Rows.Count(r => r.AlreadyImported);
    public bool HasMapping => Mapping is { IsUsable: true };
}

public sealed class ImportCommitResult
{
    public int ImportBatchId { get; set; }
    public int Inserted { get; set; }
    public int SkippedDuplicate { get; set; }
    public int Failed { get; set; }
}

/// <summary>One normalization, mapping, preview and commit pipeline for files and future device drivers.</summary>
public class AttendanceImportService
{
    private List<DeviceEmployeeMapping> _deviceMappings = new();
    private readonly AppDbContext _db;
    private readonly ILogger<AttendanceImportService> _logger;
    public AttendanceImportService(AppDbContext db, ILogger<AttendanceImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Keep the historical hash for whole-second records. Historical rows are never rehashed.
    private static string TimeKey(DateTime time) => time.ToString(
        time.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fffffff",
        CultureInfo.InvariantCulture);

    public static string ComputeHash(string? deviceId, string code, DateTime time, string? attType)
    {
        var raw = $"{deviceId?.Trim().ToUpperInvariant()}|{code.Trim().ToUpperInvariant()}|{TimeKey(time)}|{attType?.Trim().ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static string ComputeFileHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string DirectionKey(string? type) => PunchClassifier.ParseExplicitDirection(type) switch
    {
        PunchDirection.In => "I",
        PunchDirection.Out => "O",
        _ => type?.Trim().ToUpperInvariant() ?? string.Empty
    };
    public static string NaturalKey(string? deviceEmployeeCode, DateTime punchTime, string? attType)
        => $"{EmployeeAttendanceMatcher.NormalizeCode(deviceEmployeeCode).ToUpperInvariant()}|{TimeKey(punchTime)}|{DirectionKey(attType)}";
    private static string IdentityKey(int? employeeId, string? code, DateTime time, string? type)
        => NaturalKey(employeeId.HasValue ? $"employee:{employeeId.Value}" : $"code:{code}", time, type);

    public async Task<ImportAnalysisResult> AnalyzeAsync(byte[] bytes, string fileName,
        CancellationToken ct = default, PunchDirection expectedDirection = PunchDirection.Unknown)
    {
        var format = AttendanceFileParser.DetectFormat(bytes);
        var sheet = AttendanceFileParser.Parse(bytes, format);
        var mapping = ColumnMapper.Detect(sheet.Rows);
        var fallback = mapping is null;
        mapping ??= ColumnMapper.Fallback(sheet.Rows);
        var result = new ImportAnalysisResult
        {
            Format = format, EncodingName = sheet.EncodingName, Mapping = mapping,
            UsedFallback = fallback, FileHash = ComputeFileHash(bytes), ExpectedDirection = expectedDirection
        };
        if (!result.HasMapping) return result;
        if (mapping.HeaderRowIndex >= 0) result.Headers = sheet.Rows[mapping.HeaderRowIndex].ToList();
        for (var r = mapping.HeaderRowIndex + 1; r < sheet.Rows.Count; r++)
        {
            var fields = sheet.Rows[r];
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            string? Cell(int? i) => i is >= 0 && i < fields.Length ? fields[i.Value]?.Trim() : null;
            var item = new ImportPreviewRow
            {
                RowNumber = r + 1, DeviceEmployeeCode = Cell(mapping.EmployeeCode), DeviceId = Cell(mapping.DeviceId),
                Name = Cell(mapping.Name), AttType = Cell(mapping.AttType), RawPunchTime = Cell(mapping.AttTime),
                OriginalFields = fields.ToArray(), Source = fileName
            };
            if (mapping.ExpectedColumnCount.HasValue && fields.Length != mapping.ExpectedColumnCount.Value)
            {
                item.Status = ImportRowStatus.InvalidRow;
                item.Message = $"عدد الأعمدة غير صحيح: المتوقع {mapping.ExpectedColumnCount} والموجود {fields.Length}.";
            }
            result.Rows.Add(item);
        }
        await ValidateAsync(result, ct);
        _logger.LogInformation("Attendance analysis: {Format}, {Rows} records, {Valid} eligible", format, result.Total, result.SuccessCount);
        return result;
    }

    /// <summary>Drivers supply device-local wall times (Unspecified), after any verified timezone normalization.</summary>
    public async Task<ImportAnalysisResult> AnalyzeRecordsAsync(IEnumerable<ParsedPunchRow> records,
        string source, CancellationToken ct = default, PunchDirection expectedDirection = PunchDirection.Unknown)
    {
        var result = new ImportAnalysisResult
        {
            ExpectedDirection = expectedDirection,
            Mapping = new ColumnMapping { EmployeeCode = 0, AttTime = 1, HeaderRowIndex = -1 },
            Rows = records.Select(r => new ImportPreviewRow
            {
                RowNumber = r.RowNumber, DeviceId = r.DeviceId, DeviceEmployeeCode = r.DeviceEmployeeCode,
                Name = r.Name, AttType = r.AttType, NormalizedAttType = r.NormalizedAttType, PunchTime = r.PunchTime, RawPunchTime = r.RawPunchTime,
                OriginalFields = r.OriginalFields, Source = source,
                Status = r.ValidationMessage is null ? ImportRowStatus.Success : ImportRowStatus.InvalidRow,
                Message = r.ValidationMessage
            }).ToList()
        };
        result.FileHash = ComputeFileHash(JsonSerializer.SerializeToUtf8Bytes(result.Rows));
        await ValidateAsync(result, ct);
        return result;
    }

    private async Task ValidateAsync(ImportAnalysisResult result, CancellationToken ct)
    {
        _deviceMappings = await _db.Set<DeviceEmployeeMapping>().AsNoTracking().Include(m => m.AttendanceDevice).ToListAsync(ct);
        var employees = await EmployeeIdentitiesAsync(ct);
        var matcher = new EmployeeAttendanceMatcher(employees);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Status == ImportRowStatus.InvalidRow) continue;
            var diagnosticFields = item.OriginalFields.Length > 0 ? item.OriginalFields :
                new[] { item.DeviceEmployeeCode ?? "", item.Name ?? "", item.AttType ?? "", item.RawPunchTime ?? "", item.DeviceId ?? "" };
            if (diagnosticFields.Any(field => field.Any(c => char.IsControl(c) && c is not '\t')))
            {
                item.Status = ImportRowStatus.InvalidRow;
                item.Message = "يحتوي الصف على محارف تحكم غير صالحة. صحح الصف ثم أعد التحليل."; continue;
            }
            item.DeviceEmployeeCode = EmployeeAttendanceMatcher.NormalizeCode(item.DeviceEmployeeCode);
            if (string.IsNullOrWhiteSpace(item.DeviceEmployeeCode))
            {
                item.Status = ImportRowStatus.InvalidRow; item.Message = "كود البصمة مفقود."; continue;
            }
            if (item.DeviceEmployeeCode.Length > 50 || item.DeviceId?.Length > 50 || item.Name?.Length > 200
                || item.AttType?.Length > 50 || item.RawPunchTime?.Length > 100)
            {
                item.Status = ImportRowStatus.InvalidRow; item.Message = "أحد الحقول يتجاوز الطول المسموح. راجع بنية الصف."; continue;
            }
            DateTime time;
            if (item.PunchTime is { } supplied)
            {
                if (supplied.Kind != DateTimeKind.Unspecified)
                {
                    item.Status = ImportRowStatus.InvalidDateTime;
                    item.Message = "يجب أن يرسل موصل الجهاز الوقت المحلي دون تحويل UTC."; continue;
                }
                time = supplied;
            }
            else if (!PunchTimeParser.TryParse(item.RawPunchTime, out time, out var error))
            {
                item.Status = ImportRowStatus.InvalidDateTime; item.Message = error; continue;
            }
            item.PunchTime = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
            var match = await MatchAsync(matcher, employees, item.DeviceId, item.DeviceEmployeeCode, ct);
            if (match.Ambiguous)
            {
                item.Status = ImportRowStatus.NeedsReview;
                item.Message = "كود البصمة مرتبط بأكثر من موظف. صحح الربط قبل الاستيراد."; continue;
            }
            if (match.Employee is null)
            {
                item.Status = ImportRowStatus.UnknownEmployee;
                item.Message = $"كود بصمة غير مرتبط: {item.DeviceEmployeeCode}. اربطه بموظف موجود ثم أعد التحليل."; continue;
            }
            item.EmployeeId = match.Employee.Id;
            item.MatchedEmployeeName = match.Employee.FullName;
            var direction = PunchClassifier.ParseExplicitDirection(item.NormalizedAttType ?? item.AttType);
            if (result.ExpectedDirection != PunchDirection.Unknown && direction != PunchDirection.Unknown && direction != result.ExpectedDirection)
            {
                item.Status = ImportRowStatus.NeedsReview;
                item.Message = "اتجاه الحركة الصريح يخالف منطقة الرفع. استخدم الملف الموحد أو منطقة الرفع الصحيحة."; continue;
            }
            // FP is the established neutral legacy format, classified by the existing shift engine.
            var legacyNeutral = result.Mapping?.IsZkExport != true && string.Equals(item.AttType, "FP", StringComparison.OrdinalIgnoreCase);
            if (direction == PunchDirection.Unknown && !legacyNeutral)
            {
                item.Status = ImportRowStatus.NeedsReview;
                item.Message = "اتجاه البصمة غير معروف؛ لا يمكن افتراض الدخول أو الخروج."; continue;
            }
            item.DedupHash = ComputeHash(item.DeviceId, item.DeviceEmployeeCode, time, item.NormalizedAttType ?? item.AttType);
            if (!seen.Add(IdentityKey(item.EmployeeId, item.DeviceEmployeeCode, time, item.NormalizedAttType ?? item.AttType)))
            {
                item.Status = ImportRowStatus.Duplicate; item.Message = "مكرر داخل نفس المصدر."; continue;
            }
            item.Status = ImportRowStatus.Success;
            item.Message = match.UsedFallback ? "مطابقة فريدة بالكود الوظيفي (لا يوجد كود بصمة مطابق)." : "سجل صالح للاستيراد.";
        }
        var candidates = result.Rows.Where(r => r.Status == ImportRowStatus.Success).ToList();
        var (hashes, keys) = await GetExistingPunchIndexAsync(candidates, employees, ct);
        foreach (var item in candidates)
        {
            if (!hashes.Contains(item.DedupHash) && !keys.Contains(IdentityKey(item.EmployeeId, item.DeviceEmployeeCode, item.PunchTime!.Value, item.NormalizedAttType ?? item.AttType))) continue;
            item.Status = ImportRowStatus.Duplicate; item.AlreadyImported = true;
            item.Message = "مستورد مسبقًا في قاعدة البيانات.";
        }
        result.Preview = result.Rows.Take(100).ToList();
    }

    private async Task<(HashSet<string> Hashes, HashSet<string> Keys)> GetExistingPunchIndexAsync(
        IReadOnlyList<ImportPreviewRow> rows, IReadOnlyList<Employee> employees, CancellationToken ct)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rows.Count == 0) return (hashes, keys);
        var min = rows.Min(r => r.PunchTime!.Value);
        var max = rows.Max(r => r.PunchTime!.Value);
        var matcher = new EmployeeAttendanceMatcher(employees);
        var ids = rows.Where(r => r.EmployeeId.HasValue).Select(r => r.EmployeeId!.Value).ToHashSet();
        // Bounded by the import period. Recognize historical orphan punches and aliases without rewriting them.
        var existing = await _db.RawPunches.AsNoTracking()
            .Where(p => p.PunchTime >= min && p.PunchTime <= max)
            .Select(p => new { p.EmployeeId, p.DeviceEmployeeCode, p.PunchTime, p.AttType, p.NormalizedAttType, p.DedupHash }).ToListAsync(ct);
        foreach (var punch in existing)
        {
            var id = punch.EmployeeId ?? matcher.Match(punch.DeviceEmployeeCode).Employee?.Id;
            if (id is null || !ids.Contains(id.Value)) continue;
            hashes.Add(punch.DedupHash);
            keys.Add(IdentityKey(id, punch.DeviceEmployeeCode, punch.PunchTime, punch.NormalizedAttType ?? punch.AttType));
        }
        return (hashes, keys);
    }

    public async Task<ImportCommitResult> CommitAsync(ImportAnalysisResult analysis, string fileName,
        string? userId, string? userName, CancellationToken ct = default)
    {
        if (!analysis.HasMapping) throw new InvalidOperationException("لا توجد خريطة أعمدة صالحة للاستيراد.");
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        _deviceMappings = await _db.Set<DeviceEmployeeMapping>().AsNoTracking().Include(m => m.AttendanceDevice).ToListAsync(ct);
        var employees = await EmployeeIdentitiesAsync(ct);
        var matcher = new EmployeeAttendanceMatcher(employees);
        var rows = analysis.Rows.Where(r => r.Status == ImportRowStatus.Success && r.PunchTime.HasValue).ToList();
        foreach (var row in rows)
        {
            var current = await MatchAsync(matcher, employees, row.DeviceId, row.DeviceEmployeeCode, ct);
            if (current.Ambiguous || current.Employee?.Id != row.EmployeeId)
            {
                row.Status = ImportRowStatus.NeedsReview;
                row.Message = "تغير ربط الموظف بعد المعاينة؛ أعد التحليل قبل الاستيراد.";
            }
        }
        rows = rows.Where(r => r.Status == ImportRowStatus.Success).ToList();
        var (hashes, keys) = await GetExistingPunchIndexAsync(rows, employees, ct);
        var commit = new ImportCommitResult { SkippedDuplicate = analysis.DuplicateCount };
        var batch = new ImportBatch
        {
            FileName = Path.GetFileName(fileName), FileHash = analysis.FileHash,
            ImportedAt = DateTime.UtcNow, ImportedByUserId = userId, ImportedByName = userName,
            DetectedFormat = analysis.Format.ToString(), TotalRows = analysis.Total,
            InvalidRows = analysis.InvalidCount, UnknownEmployeeRows = analysis.UnknownEmployeeCount,
            ReviewRows = analysis.ReviewCount,
            Notes = $"دخول {analysis.CheckInCount}؛ خروج {analysis.CheckOutCount}؛ ترميز {analysis.EncodingName ?? "device"}؛ " +
                    (analysis.UsedFallback ? "ربط حسب بنية المحتوى." : "ربط الأعمدة المعروفة."),
            CreatedBy = userName, CreatedAt = DateTime.UtcNow
        };
        var createdPunches = new List<RawPunch>();
        try
        {
            _db.ImportBatches.Add(batch);
            foreach (var row in rows)
            {
                var key = IdentityKey(row.EmployeeId, row.DeviceEmployeeCode, row.PunchTime!.Value, row.NormalizedAttType ?? row.AttType);
                if (hashes.Contains(row.DedupHash) || !keys.Add(key))
                {
                    row.Status = ImportRowStatus.Duplicate; row.AlreadyImported = true;
                    row.Message = "تم استيراد السجل بعد المعاينة بواسطة عملية أخرى.";
                    commit.SkippedDuplicate++; continue;
                }
                var punch = new RawPunch
                {
                    ImportBatch = batch, DeviceId = row.DeviceId, DeviceEmployeeCode = row.DeviceEmployeeCode!,
                    RawEmployeeName = row.Name, AttType = row.AttType, NormalizedAttType = row.NormalizedAttType, PunchTime = row.PunchTime.Value,
                    RawPunchTimeText = row.RawPunchTime, RowNumber = row.RowNumber,
                    OriginalFieldsJson = row.OriginalFields.Length > 0 ? JsonSerializer.Serialize(row.OriginalFields) : null,
                    DedupHash = row.DedupHash, EmployeeId = row.EmployeeId, IsValid = true,
                    CreatedBy = userName, CreatedAt = DateTime.UtcNow
                };
                createdPunches.Add(punch);
                _db.RawPunches.Add(punch);
                hashes.Add(row.DedupHash);
                commit.Inserted++;
            }
            batch.SuccessRows = commit.Inserted;
            batch.DuplicateRows = commit.SkippedDuplicate;
            commit.Failed = batch.InvalidRows + batch.UnknownEmployeeRows + batch.ReviewRows;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            // A failed file must never be flushed by a later file on this scoped context.
            // Detach only this attempt's entities, including entities accepted by SaveChanges
            // when the surrounding transaction subsequently failed or was cancelled.
            foreach (var punch in createdPunches) _db.Entry(punch).State = EntityState.Detached;
            _db.Entry(batch).State = EntityState.Detached;
            throw;
        }
        commit.ImportBatchId = batch.Id;
        _logger.LogInformation("Attendance import: batch {Batch}, inserted {Inserted}, duplicate {Duplicates}, withheld {Withheld}",
            batch.Id, commit.Inserted, commit.SkippedDuplicate, commit.Failed);
        return commit;
    }

    private Task<(Employee? Employee, bool Ambiguous, bool UsedFallback)> MatchAsync(
        EmployeeAttendanceMatcher matcher, List<Employee> employees, string? deviceId, string? code, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mapping = _deviceMappings.Where(m => deviceId != null && m.AttendanceDevice?.Identifier == deviceId
            && string.Equals(m.BiometricCode, code, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.EmployeeId).Distinct().Take(2).ToList();
        if (mapping.Count > 1) return Task.FromResult<(Employee?, bool, bool)>((null, true, false));
        if (mapping.Count == 1) return Task.FromResult<(Employee?, bool, bool)>((employees.SingleOrDefault(e => e.Id == mapping[0]), false, false));
        return Task.FromResult(matcher.Match(code));
    }
    private Task<List<Employee>> EmployeeIdentitiesAsync(CancellationToken ct)
        => _db.Employees.AsNoTracking().Select(e => new Employee
        {
            Id = e.Id, EmployeeCode = e.EmployeeCode, DeviceEmployeeCode = e.DeviceEmployeeCode, FullName = e.FullName
        }).ToListAsync(ct);
}
