using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Infrastructure.Import;

public sealed record RosterReconciliationRow(int RowNumber, string Code, string? DeviceName,
    string? SystemName, string Status, string Message);

public sealed class RosterReconciliationResult
{
    public bool Analyzed { get; set; }
    public int SystemEmployees { get; set; }
    public int DeviceRows { get; set; }
    public int UniqueDeviceCodes { get; set; }
    public List<RosterReconciliationRow> Rows { get; set; } = new();
}

/// <summary>Read-only comparison. Names are diagnostics only; no HR or payroll updates are performed.</summary>
public sealed class ZkRosterReconciliationService(AppDbContext db)
{
    private static readonly string[] CodeHeaders = { "biometriccode", "deviceemployeecode", "devicecode", "userid", "enrollid", "acno", "pin", "id", "كودالبصمة", "كودالجهاز" };
    private static readonly string[] NameHeaders = { "name", "fullname", "employeename", "username", "الاسم", "اسمالموظف" };

    public async Task<RosterReconciliationResult> AnalyzeAsync(byte[] bytes, CancellationToken ct = default)
    {
        var sheet = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        if (ColumnMapper.Detect(sheet.Rows) is { IsUsable: true })
            throw new InvalidDataException("هذا ملف حركات حضور؛ ارفع قائمة موظفي الجهاز التي تحتوي على كود البصمة والاسم فقط للمقارنة.");
        var header = -1; var codeColumn = -1; var nameColumn = -1;
        for (var r = 0; r < Math.Min(15, sheet.Rows.Count); r++)
        {
            // Prefer the explicit biometric column in the existing employee template over its payroll ID.
            var normalized = sheet.Rows[r].Select(ColumnMapper.Normalize).ToArray();
            var bio = Array.FindIndex(normalized, x => CodeHeaders.Where(h => h != "id").Contains(x));
            var code = bio >= 0 ? bio : Array.FindIndex(normalized, x => x == "id");
            var name = Array.FindIndex(normalized, x => NameHeaders.Contains(x));
            if (code < 0 || name < 0) continue;
            header = r; codeColumn = code; nameColumn = name; break;
        }
        if (header < 0) throw new InvalidDataException("لم يتم التعرف على قائمة الموظفين. استخدم عناوين واضحة مثل كود البصمة والاسم أو User ID وName. لا تستخدم ملف حركات الحضور لهذه المقارنة.");
        var employees = await db.Employees.AsNoTracking().Select(e => new HrPayroll.Domain.Entities.Employee
        {
            Id = e.Id, EmployeeCode = e.EmployeeCode, DeviceEmployeeCode = e.DeviceEmployeeCode, FullName = e.FullName
        }).ToListAsync(ct);
        var matcher = new EmployeeAttendanceMatcher(employees);
        var source = sheet.Rows.Skip(header + 1).Select((row, index) => (Fields: row, Number: header + index + 2))
            .Where(x => x.Fields.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        string Code(string[] fields) => codeColumn < fields.Length ? EmployeeAttendanceMatcher.NormalizeCode(fields[codeColumn]) : string.Empty;
        var counts = source.Where(x => Code(x.Fields).Length > 0).GroupBy(x => Code(x.Fields), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new RosterReconciliationResult
        {
            Analyzed = true, SystemEmployees = employees.Count, DeviceRows = source.Count, UniqueDeviceCodes = counts.Count
        };
        var represented = new HashSet<int>();
        foreach (var row in source)
        {
            var code = Code(row.Fields);
            var name = nameColumn < row.Fields.Length ? row.Fields[nameColumn].Trim() : null;
            var match = matcher.Match(code);
            var status = "matched"; var message = "كود البصمة مطابق.";
            if (code.Length == 0) { status = "invalid"; message = "كود البصمة مفقود."; }
            else if (counts[code] > 1) { status = "review"; message = "كود البصمة مكرر داخل قائمة الجهاز."; }
            else if (match.Ambiguous) { status = "review"; message = "كود البصمة ملتبس بين موظفين بالنظام."; }
            else if (match.Employee is null) { status = "unknown"; message = "لا يوجد موظف مطابق للكود في النظام."; }
            else if (match.UsedFallback) { status = "review"; message = "مطابقة بالكود الوظيفي فقط؛ راجع ربط كود البصمة."; }
            else if (!string.Equals(NormalizeName(name), NormalizeName(match.Employee.FullName), StringComparison.Ordinal))
            { status = "review"; message = "الكود مطابق والاسم مختلف؛ راجع الاسم دون تحديث تلقائي."; }
            if (match.Employee is not null) represented.Add(match.Employee.Id);
            result.Rows.Add(new(row.Number, code, name, match.Employee?.FullName, status, message));
        }
        foreach (var employee in employees.Where(e => !represented.Contains(e.Id)))
            result.Rows.Add(new(0, employee.DeviceEmployeeCode ?? employee.EmployeeCode, null, employee.FullName,
                "missing", "موظف بالنظام غير موجود في قائمة الجهاز المرفوعة."));
        return result;
    }

    private static string NormalizeName(string? name)
        => string.Join(" ", (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
