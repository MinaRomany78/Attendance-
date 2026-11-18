using HrPayroll.Domain.Entities;

namespace HrPayroll.Infrastructure.Import;

/// <summary>Exact text codes only: leading zeroes and internal spaces are never removed.</summary>
public sealed class EmployeeAttendanceMatcher
{
    private readonly Dictionary<string, List<Employee>> _device;
    private readonly Dictionary<string, List<Employee>> _employee;

    public EmployeeAttendanceMatcher(IEnumerable<Employee> employees)
    {
        var list = employees.ToList();
        _device = Index(list, e => e.DeviceEmployeeCode);
        _employee = Index(list, e => e.EmployeeCode);
    }

    public static string NormalizeCode(string? code) => code?.Trim() ?? string.Empty;

    private static Dictionary<string, List<Employee>> Index(IEnumerable<Employee> employees, Func<Employee, string?> code)
        => employees.Where(e => !string.IsNullOrWhiteSpace(code(e)))
            .GroupBy(e => NormalizeCode(code(e)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    public (Employee? Employee, bool Ambiguous, bool UsedFallback) Match(string? code)
    {
        var key = NormalizeCode(code);
        if (_device.TryGetValue(key, out var deviceMatches))
            return deviceMatches.Count == 1 ? (deviceMatches[0], false, false) : (null, true, false);
        if (_employee.TryGetValue(key, out var codeMatches))
            return codeMatches.Count == 1 ? (codeMatches[0], false, true) : (null, true, true);
        return (null, false, false);
    }
}
