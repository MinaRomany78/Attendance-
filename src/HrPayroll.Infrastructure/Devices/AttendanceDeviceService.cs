using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Infrastructure.Devices;

public sealed partial class AttendanceDeviceService
{
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IEnumerable<IZkDeviceClient> _clients;
    private readonly AuditService _audit;

    public AttendanceDeviceService(AppDbContext db, IDataProtectionProvider protection,
        IEnumerable<IZkDeviceClient> clients, AuditService audit)
    {
        _db = db;
        _protector = protection.CreateProtector("HrPayroll.AttendanceDevices.CommunicationKey.v1");
        _clients = clients;
        _audit = audit;
    }

    public bool HasCompatibleDriver(AttendanceDevice device) => _clients.Where(c => c.Supports(device)).Take(2).Count() == 1;

    public async Task SaveAsync(AttendanceDevice input, string? communicationKey, string? user, CancellationToken ct)
    {
        var device = input.Id == 0 ? new AttendanceDevice() : await _db.AttendanceDevices.SingleAsync(d => d.Id == input.Id, ct);
        if (!Enum.IsDefined(input.Role)) throw new InvalidOperationException("دور الجهاز غير صالح.");
        device.Role = input.Role;
        device.Name = input.Name.Trim();
        device.IpAddress = input.IpAddress.Trim();
        device.TcpPort = input.TcpPort;
        device.Model = input.Model.Trim();
        device.Identifier = input.Identifier?.Trim();
        device.TimeZoneId = input.TimeZoneId.Trim();
        if (!string.IsNullOrWhiteSpace(communicationKey))
            device.ProtectedCommunicationKey = _protector.Protect(communicationKey);
        if (string.IsNullOrWhiteSpace(device.Identifier))
            device.Identifier = "device-" + Guid.NewGuid().ToString("N");
        if (device.Identifier.Length > 50) throw new InvalidOperationException("معرف الجهاز يجب ألا يتجاوز 50 حرفاً.");
        if (await _db.AttendanceDevices.AnyAsync(d => d.Id != device.Id && d.Identifier == device.Identifier, ct))
            throw new InvalidOperationException("معرف الجهاز مستخدم لجهاز آخر.");
        if (device.Id == 0)
        {
            device.CreatedBy = user;
            _db.AttendanceDevices.Add(device);
        }
        else
        {
            device.UpdatedAt = DateTime.UtcNow;
            device.UpdatedBy = user;
        }
        _audit.Log("حفظ إعداد جهاز بصمة", nameof(AttendanceDevice), device.Id.ToString(), user: user);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ZkConnectionResult> TestAsync(int id, CancellationToken ct)
    {
        var device = await _db.AttendanceDevices.SingleAsync(d => d.Id == id, ct);
        var client = FindDriver(device);
        if (client is null)
        {
            device.ConnectionStatus = "برنامج التشغيل غير متاح";
            await _db.SaveChangesAsync(ct);
            return new(false, "لا يوجد برنامج تشغيل متوافق مع هذا الطراز؛ لم تتم محاولة الاتصال.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var result = await client.TestConnectionAsync(Connection(device), timeout.Token);
            device.ConnectionStatus = result.Connected ? "متصل (آخر اختبار)" : "فشل اختبار الاتصال";
            await _db.SaveChangesAsync(ct);
            return result;
        }
        catch when (!ct.IsCancellationRequested)
        {
            device.ConnectionStatus = "فشل اختبار الاتصال";
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    public async Task<ImportAnalysisResult> DownloadForPreviewAsync(int id, AttendanceImportService import, CancellationToken ct)
    {
        var device = await _db.AttendanceDevices.AsNoTracking().SingleAsync(d => d.Id == id, ct);
        var client = FindDriver(device) ?? throw new InvalidOperationException("يجب تثبيت برنامج تشغيل موثّق ومتوافق مع طراز الجهاز أولاً.");
        var companyZoneId = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.CompanyTimeZone).Select(s => s.Value).SingleOrDefaultAsync(ct) ?? "Africa/Cairo";
        if (!TimeZoneInfo.FindSystemTimeZoneById(device.TimeZoneId).HasSameRules(TimeZoneInfo.FindSystemTimeZoneById(companyZoneId)))
            throw new InvalidOperationException("توقيت الجهاز مختلف عن توقيت الشركة. يجب التحقق من تحويل التوقيت في برنامج التشغيل قبل تمكين المزامنة.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var records = await client.DownloadAttendanceAsync(Connection(device), timeout.Token);
        // Egyptian wall-clock device times are never converted to UTC. A driver must normalize
        // its documented timestamp representation before supplying this boundary.
        var rows = records.Select((r, index) => new ParsedPunchRow
        {
            RowNumber = index + 1,
            DeviceEmployeeCode = r.BiometricCode,
            DeviceId = device.Identifier,
            Name = r.EmployeeName,
            AttType = r.Direction,
            NormalizedAttType = NormalizeDirection(device.Role, r.Direction),
            PunchTime = r.LocalTime.Kind == DateTimeKind.Unspecified ? r.LocalTime : null,
            RawPunchTime = r.LocalTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            IsTimeValid = r.LocalTime.Kind == DateTimeKind.Unspecified,
            OriginalFields = new[] { r.BiometricCode, r.Direction ?? "", r.LocalTime.ToString("O"), r.DeviceIdentifier ?? "" }
                .Concat(r.OriginalFields ?? Array.Empty<string>()).ToArray(),
            ValidationMessage = r.LocalTime.Kind == DateTimeKind.Unspecified ? null : "وقت الجهاز يجب أن يكون وقتاً محلياً بدون تحويل UTC."
        });
                var analysis = await import.AnalyzeRecordsAsync(rows, "Ethernet: " + device.Name, ct,
            device.Role == AttendanceDeviceRole.Entry ? HrPayroll.Domain.Enums.PunchDirection.In :
            device.Role == AttendanceDeviceRole.Exit ? HrPayroll.Domain.Enums.PunchDirection.Out : HrPayroll.Domain.Enums.PunchDirection.Unknown);
        return analysis;
    }

    private IZkDeviceClient? FindDriver(AttendanceDevice device)
    {
        var compatible = _clients.Where(c => c.Supports(device)).Take(2).ToList();
        if (compatible.Count > 1) throw new InvalidOperationException("يوجد أكثر من برنامج تشغيل متوافق؛ يجب تحديد برنامج التشغيل قبل الاتصال.");
        return compatible.SingleOrDefault();
    }

    private ZkDeviceConnection Connection(AttendanceDevice d) => new(d.Id, d.IpAddress, d.TcpPort, d.Model,
        d.Identifier, d.TimeZoneId, d.ProtectedCommunicationKey is null ? null : _protector.Unprotect(d.ProtectedCommunicationKey));
}
