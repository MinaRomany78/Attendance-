using System.Data;
using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Infrastructure.Import;

public sealed class AttendanceEmployeeMappingService(AppDbContext db, AuditService audit)
{
    /// <summary>Explicit operator action: only fill a missing biometric mapping; never replace employee data.</summary>
    public async Task ResolveAsync(string code, int employeeId, string? userId, string? userName, CancellationToken ct = default)
    {
        code = EmployeeAttendanceMatcher.NormalizeCode(code);
        if (code.Length is 0 or > 50) throw new InvalidOperationException("كود البصمة غير صالح.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var employees = await db.Employees.ToListAsync(ct);
        var target = employees.SingleOrDefault(e => e.Id == employeeId)
            ?? throw new InvalidOperationException("الموظف المحدد غير موجود.");
        var matching = employees.Where(e => string.Equals(EmployeeAttendanceMatcher.NormalizeCode(e.DeviceEmployeeCode), code,
            StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Any(e => e.Id != employeeId)) throw new InvalidOperationException("كود البصمة مرتبط بموظف آخر؛ لا يمكن إعادة ربطه من الاستيراد.");
        if (!string.IsNullOrWhiteSpace(target.DeviceEmployeeCode) && !string.Equals(target.DeviceEmployeeCode.Trim(), code, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("الموظف لديه كود بصمة مختلف بالفعل. راجع بيانات الموظف قبل تغيير الربط.");
        if (target.DeviceEmployeeCode == code) return;
        var oldCode = target.DeviceEmployeeCode;
        target.DeviceEmployeeCode = code;
        target.UpdatedAt = DateTime.UtcNow; target.UpdatedBy = userName;
        audit.Log("ربط كود بصمة من معاينة الاستيراد", nameof(Employee), target.Id.ToString(), oldCode, code, userName, userId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
