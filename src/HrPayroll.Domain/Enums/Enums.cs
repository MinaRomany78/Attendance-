namespace HrPayroll.Domain.Enums;

/// <summary>
/// حالة حضور اليوم للموظف.
/// ملاحظة: القيم الجديدة تُضاف في نهاية الترقيم حتى لا تتغير القيم المحفوظة مسبقًا في قاعدة البيانات.
/// </summary>
public enum AttendanceStatus
{
    Present = 0,
    Absent = 1,
    Late = 2,
    MissingCheckIn = 3,
    MissingCheckOut = 4,
    Leave = 5,
    RestDay = 6,
    Holiday = 7,
    NeedsReview = 8,
    EarlyDeparture = 9,
    PaidLeave = 10,
    UnpaidLeave = 11,
    UnexcusedAbsence = 12
}

/// <summary>اتجاه حركة البصمة.</summary>
public enum PunchDirection
{
    Unknown = 0,
    In = 1,
    Out = 2
}

/// <summary>نوع الحدث الحضوري المصنف.</summary>
public enum AttendanceEventType
{
    Unknown = 0,
    Entry = 1,
    Exit = 2
}

/// <summary>مصدر التصنيف.</summary>
public enum ClassificationSource
{
    None = 0,
    ExplicitDeviceType = 1,
    Rule = 2,
    Manual = 3
}

/// <summary>حالة تصنيف/مراجعة البصمات.</summary>
public enum ClassificationStatus
{
    Unclassified = 0,
    NeedsReview = 1,
    Classified = 2
}

/// <summary>مصدر تحديد وردية اليوم.</summary>
public enum ShiftDetectionSource
{
    None = 0,
    Assigned = 1,
    Detected = 2,
    Ambiguous = 3
}

/// <summary>حالة الاعتماد العامة.</summary>
public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

/// <summary>حالة كشف الرواتب. القيم الجديدة تُضاف في النهاية للحفاظ على القيم المحفوظة.</summary>
public enum PayrollStatus
{
    Draft = 0,
    Computed = 1,
    Approved = 2,
    Cancelled = 3,
    PendingReview = 4,
    Locked = 5,
    Reopened = 6
}

/// <summary>نوع الإجازة / تصنيف اليوم.</summary>
public enum LeaveType
{
    Annual = 0,
    Sick = 1,
    Unpaid = 2,
    Emergency = 3,
    Other = 4,
    PublicHoliday = 5,
    UnexcusedAbsence = 6,
    Paid = 7,
    WeeklyRest = 8
}

/// <summary>حالة الموظف الوظيفية.</summary>
public enum EmploymentStatus
{
    Active = 0,
    OnLeave = 1,
    Suspended = 2,
    Terminated = 3
}

/// <summary>نوع التسوية المالية (مكافأة/بدل/خصم آخر/أيام إضافية).</summary>
public enum AdjustmentType
{
    Bonus = 0,
    Allowance = 1,
    OtherDeduction = 2,
    AdditionalDays = 3
}

/// <summary>نتيجة صف الاستيراد.</summary>
public enum ImportRowStatus
{
    Success = 0,
    Duplicate = 1,
    InvalidDateTime = 2,
    UnknownEmployee = 3,
    InvalidRow = 4
}

/// <summary>الوردية المستخدمة لحساب الرواتب (سياسة).</summary>
public enum PayrollDayBasis
{
    Fixed30Days = 0,
    ActualWorkDays = 1
}

/// <summary>حالة صف استيراد الموظفين.</summary>
public enum EmployeeImportRowStatus
{
    New = 0,
    Update = 1,
    Unchanged = 2,
    Duplicate = 3,
    Invalid = 4
}

/// <summary>وضع استيراد الموظفين: إضافة الجديد فقط أو تحديث الموجود أيضًا.</summary>
public enum EmployeeImportMode
{
    CreateOnly = 0,
    CreateAndUpdate = 1
}
