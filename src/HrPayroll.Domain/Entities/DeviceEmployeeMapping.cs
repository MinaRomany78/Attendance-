namespace HrPayroll.Domain.Entities;
public enum AttendanceDeviceRole { Unspecified = 0, Entry = 1, Exit = 2 }
public sealed class DeviceEmployeeMapping : BaseEntity
{
    public int AttendanceDeviceId { get; set; }
    public AttendanceDevice? AttendanceDevice { get; set; }
    public string BiometricCode { get; set; } = string.Empty;
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
}
