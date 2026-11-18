using HrPayroll.Domain.Enums;

namespace HrPayroll.Domain.Models;

public sealed record AttendanceTimeCalculation(int WorkedMinutes, int ActualLateMinutes, int LateMinutes,
    int EarlyLeaveMinutes, int RawOvertimeMinutes, int ScheduledMinutes, int? MissingScheduledMinutes,
    bool HasIncompletePunches, bool NeedsReview, AttendanceStatus DerivedStatus);
