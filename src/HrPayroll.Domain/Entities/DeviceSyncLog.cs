namespace HrPayroll.Domain.Entities;
public sealed class DeviceSyncLog : BaseEntity
{
    public int AttendanceDeviceId { get; set; }
    public AttendanceDevice? AttendanceDevice { get; set; }
    public bool Successful { get; set; }
    public string? Error { get; set; }
    public string? ReviewRecordsJson { get; set; }
    public int Downloaded { get; set; }
    public int Inserted { get; set; }
    public int Duplicates { get; set; }
    public int Invalid { get; set; }
    public int Unmatched { get; set; }
    public int Review { get; set; }
}
