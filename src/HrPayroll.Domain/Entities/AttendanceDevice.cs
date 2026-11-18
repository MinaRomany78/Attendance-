namespace HrPayroll.Domain.Entities;

/// <summary>LAN device configuration; saving it does not imply SDK/protocol compatibility.</summary>
public sealed class AttendanceDevice : BaseEntity
{
    public AttendanceDeviceRole Role { get; set; }
    public string ConnectionStatus { get; set; } = "لم يتم الاختبار";
    public string? LastSyncError { get; set; }
    public int LastDownloadedCount { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int TcpPort { get; set; } = 4370;
    public string Model { get; set; } = string.Empty;
    public string? Identifier { get; set; }
    public string TimeZoneId { get; set; } = "Africa/Cairo";
    public string? ProtectedCommunicationKey { get; set; }
    public DateTime? LastSuccessfulSyncUtc { get; set; }
}
