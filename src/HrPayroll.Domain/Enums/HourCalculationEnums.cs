namespace HrPayroll.Domain.Enums;

public enum HourRoundingMode { Up = 0, Down = 1, Nearest = 2 }
public enum HourRoundingScope { Unspecified = 0, PerEvent = 1, PerDay = 2, Monthly = 3 }
public enum AttendanceDeductionBasis { Unspecified = 0, LateOnly = 1, LateAndEarly = 2, TotalMissing = 3 }
public enum GracePeriodMode { Threshold = 0, ExcludeGrace = 1 }
