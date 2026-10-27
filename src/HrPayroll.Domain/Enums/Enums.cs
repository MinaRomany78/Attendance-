namespace HrPayroll.Domain.Enums;

/// <summary>
/// حالة حضور اليوم للموظف.
/// ملاحظة: القيم الجديدة تُضاف في نهاية الترقيم حتى لا تتغير القيم المحفوظة مسبقًا في قاعدة البيانات.
/// </summary>
public enum AttendanceStatus
{
    Present = 0,          // حاضر
    Absent = 1,           // غائب (بدون تصنيف إجازة)
    Late = 2,             // متأخر
    MissingCheckIn = 3,   // بصمة دخول مفقودة
    MissingCheckOut = 4,  // بصمة خروج مفقودة
    Leave = 5,            // إجازة معتمدة (عام)
    RestDay = 6,          // يوم راحة أسبوعية
    Holiday = 7,          // عطلة رسمية
    NeedsReview = 8,      // يحتاج مراجعة بشرية

    EarlyDeparture = 9,      // انصراف مبكر
    PaidLeave = 10,          // إجازة مدفوعة
    UnpaidLeave = 11,        // إجازة بدون أجر
    UnexcusedAbsence = 12    // غياب بدون عذر
}
