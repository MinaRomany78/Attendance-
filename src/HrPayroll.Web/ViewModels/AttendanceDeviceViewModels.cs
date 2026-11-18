using System.ComponentModel.DataAnnotations;
using HrPayroll.Domain.Entities;

namespace HrPayroll.Web.ViewModels;

public sealed class AttendanceDeviceViewModel
{
    public int Id { get; set; }
    [EnumDataType(typeof(AttendanceDeviceRole))] public AttendanceDeviceRole Role { get; set; }
    [Required(ErrorMessage = "أدخل اسم الجهاز."), StringLength(150)]
    public string Name { get; set; } = string.Empty;
    [Required(ErrorMessage = "أدخل عنوان IP."), StringLength(45)]
    public string IpAddress { get; set; } = string.Empty;
    [Range(1, 65535, ErrorMessage = "المنفذ يجب أن يكون بين 1 و65535.")]
    public int TcpPort { get; set; } = 4370;
    [Required(ErrorMessage = "أدخل طراز الجهاز للتحقق من التوافق."), StringLength(150)]
    public string Model { get; set; } = string.Empty;
    [StringLength(150)]
    public string? Identifier { get; set; }
    [Required, StringLength(100)]
    public string TimeZoneId { get; set; } = "Africa/Cairo";
    [StringLength(100), DataType(DataType.Password)]
    public string? CommunicationKey { get; set; }
    public List<AttendanceDevice> Devices { get; set; } = new();
    public HashSet<int> SupportedDeviceIds { get; set; } = new();
}
