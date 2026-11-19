using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Services;
using HrPayroll.Domain.ValueObjects;
using System.ComponentModel.DataAnnotations.Schema;

namespace HrPayroll.Domain.Entities;

/// <summary>ملخص الحضور النهائي لموظف في يوم/وردية.</summary>
public class DailyAttendance : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>تاريخ العمل (تاريخ بداية الوردية).</summary>
    public DateTime WorkDate { get; set; }

    public int? ShiftId { get; set; }
    public Shift? Shift { get; set; }

    /// <summary>كيف حُدِّدت الوردية لهذا اليوم.</summary>
    public ShiftDetectionSource DetectionSource { get; set; } = ShiftDetectionSource.None;

    /// <summary>سبب الحاجة للمراجعة عند غموض الوردية أو البصمات.</summary>
    public string? NeedsReviewReason { get; set; }

    public DateTime ShiftStart { get; set; }
    public DateTime ShiftEnd { get; set; }

    public DateTime? CheckInTime { get; set; }
    public DateTime? CheckOutTime { get; set; }

    public int LateMinutes { get; set; }
    public int EarlyLeaveMinutes { get; set; }

    /// <summary>الإضافي الخام المحسوب آليًا بالدقائق (قبل أي اعتماد).</summary>
    public int RawOvertimeMinutes { get; set; }

    /// <summary>الإضافي المعتمد يدويًا بالدقائق (المسموح بدفعه).</summary>
    public int ApprovedOvertimeMinutes { get; set; }

    /// <summary>الإضافي المدفوع فعليًا بالدقائق (بعد التقريب وقواعد السياسة).</summary>
    public int PaidOvertimeMinutes { get; set; }

    public AttendanceStatus Status { get; set; }
    public ClassificationStatus PunchReviewStatus { get; set; }

    public bool HasIncompletePunches { get; set; }
    public bool IsManualOverride { get; set; }

    /// <summary>يوم راحة أسبوعية.</summary>
    public bool IsWeeklyRestDay { get; set; }

    /// <summary>عطلة رسمية.</summary>
    public bool IsHoliday { get; set; }

    public int? HolidayId { get; set; }
    public PublicHoliday? Holiday { get; set; }

    /// <summary>الإجازة المعتمدة التي غطّت هذا اليوم (إن وُجدت).</summary>
    public int? LeaveRequestId { get; set; }
    public LeaveRequest? LeaveRequest { get; set; }

    public int? EntryEventId { get; set; }
    public int? ExitEventId { get; set; }

    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }

    /// <summary>إجمالي دقائق العمل الفعلية (خروج − دخول) عند توفر البصمتين.</summary>
    public int WorkedMinutes =>
        CheckInTime.HasValue && CheckOutTime.HasValue
            ? AttendanceCalculator.MinutesBetween(CheckInTime.Value, CheckOutTime.Value)
            : 0;

    [NotMapped] public decimal ActualWorkedHours => HourDurationCalculator.ToHours(WorkedMinutes);
    [NotMapped] public int ActualLateMinutes => CheckInTime.HasValue && ShiftEnd > ShiftStart
        ? AttendanceCalculator.MinutesBetween(ShiftStart, CheckInTime.Value) : 0;
    [NotMapped] public decimal ActualLateHours => HourDurationCalculator.ToHours(ActualLateMinutes);
    [NotMapped] public decimal LateHours => HourDurationCalculator.ToHours(LateMinutes);
    [NotMapped] public decimal ActualEarlyLeaveHours => HourDurationCalculator.ToHours(EarlyLeaveMinutes);
    [NotMapped] public decimal RawOvertimeHours => HourDurationCalculator.ToHours(RawOvertimeMinutes);
    [NotMapped] public decimal ApprovedOvertimeHours => HourDurationCalculator.ToHours(ApprovedOvertimeMinutes);
    [NotMapped] public decimal PaidOvertimeHours => HourDurationCalculator.ToHours(PaidOvertimeMinutes);
    [NotMapped] public int? MissingScheduledMinutes => ShiftEnd > ShiftStart
        ? AttendanceCalculator.CalculateDay(new ShiftWindow(ShiftStart, ShiftEnd), CheckInTime, CheckOutTime, 0, 0).MissingScheduledMinutes : null;
    [NotMapped] public decimal? ActualMissingHours => MissingScheduledMinutes.HasValue
        ? HourDurationCalculator.ToHours(MissingScheduledMinutes.Value) : null;
    [NotMapped] public bool IsPresent => CheckInTime.HasValue && CheckOutTime > CheckInTime;
    [NotMapped] public bool IsLate => ActualLateMinutes > 0;
    [NotMapped] public bool HasEarlyDeparture => EarlyLeaveMinutes > 0;
    [NotMapped] public bool HasMissingPunch => CheckInTime is null || CheckOutTime is null;
    [NotMapped] public bool NeedsReview => PunchReviewStatus == ClassificationStatus.NeedsReview || HasIncompletePunches
        || (CheckInTime.HasValue && CheckOutTime.HasValue && CheckOutTime <= CheckInTime);

    public ICollection<AttendanceCorrection> Corrections { get; set; } = new List<AttendanceCorrection>();
}
