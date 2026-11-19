using System.Text.Json;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
namespace HrPayroll.Infrastructure.Devices;
public sealed partial class AttendanceDeviceService
{
    public static string? NormalizeDirection(AttendanceDeviceRole role, string? original)
    {
        if (PunchClassifier.ParseExplicitDirection(original) != PunchDirection.Unknown) return original;
        return role switch { AttendanceDeviceRole.Entry => "I", AttendanceDeviceRole.Exit => "O", _ => original };
    }

    public async Task MapEmployeeAsync(int deviceId, string code, int employeeId, string? user, CancellationToken ct)
    {
        code = EmployeeAttendanceMatcher.NormalizeCode(code);
        if (code.Length is 0 or > 50 || !await _db.Employees.AnyAsync(e => e.Id == employeeId, ct)
            || !await _db.AttendanceDevices.AnyAsync(d => d.Id == deviceId, ct))
            throw new InvalidOperationException("الجهاز أو الموظف أو كود البصمة غير صالح.");
        if (await _db.Set<DeviceEmployeeMapping>().AnyAsync(m => m.AttendanceDeviceId == deviceId && m.BiometricCode == code, ct))
            throw new InvalidOperationException("الكود مرتبط بالفعل. راجع الربط الحالي قبل تغييره.");
        _db.Set<DeviceEmployeeMapping>().Add(new() { AttendanceDeviceId = deviceId, EmployeeId = employeeId, BiometricCode = code, CreatedBy = user });
        _audit.Log("ربط كود موظف بجهاز", nameof(DeviceEmployeeMapping), deviceId.ToString(), newValue: $"{code}: {employeeId}", user: user);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DeviceSyncLog>> SynchronizeAsync(int? deviceId, AttendanceImportService import,
        AttendanceProcessingService processing, string? userId, string? user, CancellationToken ct)
    {
        var devices = await _db.AttendanceDevices.Where(d => deviceId.HasValue ? d.Id == deviceId :
            d.Role == AttendanceDeviceRole.Entry || d.Role == AttendanceDeviceRole.Exit).OrderBy(d => d.Role).ToListAsync(ct);
        var results = new List<DeviceSyncLog>();
        DateTime? from = null, to = null;
        foreach (var d in devices)
        {
            ct.ThrowIfCancellationRequested();
            var log = new DeviceSyncLog { AttendanceDeviceId = d.Id, CreatedBy = user };
            try
            {
                if (d.Role == AttendanceDeviceRole.Unspecified) throw new InvalidOperationException("حدد دور الجهاز: دخول أو خروج.");
                if (string.IsNullOrWhiteSpace(d.Identifier)) throw new InvalidOperationException("احفظ إعدادات الجهاز لتعيين معرف ثابت.");
                var analysis = await DownloadForPreviewAsync(d.Id, import, ct);
                log.Downloaded = analysis.Total;
                log.Duplicates = analysis.DuplicateCount; log.Invalid = analysis.InvalidCount;
                log.Unmatched = analysis.UnknownEmployeeCount; log.Review = analysis.ReviewCount;
                // Retain every withheld record with original fields, identity and timestamp for review.
                log.ReviewRecordsJson = JsonSerializer.Serialize(analysis.Rows.Where(r => r.Status is not (ImportRowStatus.Success or ImportRowStatus.Duplicate)));
                var committed = await import.CommitAsync(analysis, "Ethernet-" + d.Name, userId, user, ct);
                log.Inserted = committed.Inserted; log.Duplicates = committed.SkippedDuplicate;
                var times = analysis.Rows.Where(r => r.Status == ImportRowStatus.Success).Select(r => r.PunchTime!.Value).ToList();
                if (times.Count > 0) { from = from.HasValue ? (from < times.Min() ? from : times.Min()) : times.Min(); to = to.HasValue ? (to > times.Max() ? to : times.Max()) : times.Max(); }
                log.Successful = true; d.LastSuccessfulSyncUtc = DateTime.UtcNow;
                d.ConnectionStatus = "تم تنزيل السجلات"; d.LastSyncError = null; d.LastDownloadedCount = log.Downloaded;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.Error = ex is InvalidOperationException ? ex.Message : "تعذر الاتصال أو حفظ السجلات؛ راجع الشبكة وسجل الخادم.";
                d.ConnectionStatus = "فشلت المزامنة"; d.LastSyncError = log.Error;
            }
            _db.Set<DeviceSyncLog>().Add(log);
            await _db.SaveChangesAsync(ct);
            results.Add(log);
        }
        // Combine both devices before resolving overnight shifts. Original wall times remain intact.
        if (from.HasValue && to.HasValue)
            await processing.ProcessAsync(from.Value.Date.AddDays(-1), to.Value.Date, user: user, ct: ct);
        return results;
    }
}
