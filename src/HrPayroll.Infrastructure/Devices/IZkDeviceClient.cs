using HrPayroll.Domain.Entities;

namespace HrPayroll.Infrastructure.Devices;

// A driver must confirm model + protocol compatibility. No default SDK or network driver is registered.
public interface IZkDeviceClient
{
    bool Supports(AttendanceDevice device);
    Task<ZkConnectionResult> TestConnectionAsync(ZkDeviceConnection device, CancellationToken ct);
    Task<ZkDeviceInfo> GetDeviceInfoAsync(ZkDeviceConnection device, CancellationToken ct);
    Task<IReadOnlyList<ZkAttendanceRecord>> DownloadAttendanceAsync(ZkDeviceConnection device, CancellationToken ct);
    Task<IReadOnlyList<ZkEmployeeRecord>> DownloadEmployeesAsync(ZkDeviceConnection device, CancellationToken ct);
    Task<ZkSyncStatus> GetLastSyncStatusAsync(ZkDeviceConnection device, CancellationToken ct);
}

/// <summary>Credentials are supplied only to the selected compatible driver, never to Razor or logs.</summary>
public sealed record ZkDeviceConnection(int Id, string IpAddress, int TcpPort, string Model,
    string? Identifier, string TimeZoneId, string? CommunicationKey)
{
    public override string ToString() => $"Device {Id}: {IpAddress}:{TcpPort} ({Model})";
}
public sealed record ZkConnectionResult(bool Connected, string Message);
public sealed record ZkDeviceInfo(string Model, string? SerialNumber, string? FirmwareVersion);
public sealed record ZkSyncStatus(DateTime? LastSuccessfulSyncUtc, string State);
/// <summary>LocalTime MUST be an Unspecified wall-clock time in the configured device timezone.
/// Drivers must preserve explicit directions and must never erase device logs.</summary>
public sealed record ZkAttendanceRecord(string BiometricCode, DateTime LocalTime, string? Direction,
    string? EmployeeName = null, string? DeviceIdentifier = null, string[]? OriginalFields = null);
public sealed record ZkEmployeeRecord(string BiometricCode, string? EmployeeName);
