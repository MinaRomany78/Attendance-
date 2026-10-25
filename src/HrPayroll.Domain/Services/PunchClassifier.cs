using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.ValueObjects;

namespace HrPayroll.Domain.Services;

/// <summary>
/// يحوّل البصمات الخام إلى حركات دخول/خروج مصنفة.
///
/// ملاحظات مهمة:
/// - إذا أرسل الجهاز نوع حركة صريحًا (I/O, IN/OUT, دخول/خروج) يُستخدم بعد التحقق من معناه.
/// - إذا أرسل الجهاز نوع بصمة محايدًا مثل FP فلا يُفترض أن نوع البصمة يحدد الاتجاه.
/// - في هذه الحالة يُعتمد على الوردية وتوقيت العمل وتسلسل البصمات، وتُعلَّم الحالات الغامضة للمراجعة.
/// - بصمة دخول أو خروج مفقودة لا تُعتبر غيابًا مؤكدًا ولا إضافيًا معتمدًا.
/// </summary>
public static class PunchClassifier
{
    private static readonly string[] ExplicitInValues =
        { "i", "in", "0", "checkin", "check-in", "entry", "دخول", "حضور" };

    private static readonly string[] ExplicitOutValues =
        { "o", "out", "1", "checkout", "check-out", "exit", "خروج", "انصراف" };

    /// <summary>يحاول تحويل نوع الحركة القادم من الجهاز إلى اتجاه صريح، أو Unknown إذا كان محايدًا.</summary>
    public static PunchDirection ParseExplicitDirection(string? attType)
    {
        if (string.IsNullOrWhiteSpace(attType)) return PunchDirection.Unknown;
        var v = attType.Trim().ToLowerInvariant();

        // أنواع محايدة صريحة لا تحدد الاتجاه
        if (v is "fp" or "fingerprint" or "بصمة" or "normal" or "attend") return PunchDirection.Unknown;

        if (ExplicitInValues.Contains(v)) return PunchDirection.In;
        if (ExplicitOutValues.Contains(v)) return PunchDirection.Out;
        return PunchDirection.Unknown;
    }

    /// <summary>يصنّف بصمات موظف في يوم عمل محدد حسب ورديته.</summary>
    public static DayClassificationResult Classify(
        IReadOnlyList<RawPunchInput> punches,
        Shift shift,
        DateTime workDate,
        AttendancePolicy policy)
    {
        var window = ShiftWindowResolver.Resolve(shift, workDate);
        var entryGrace = Math.Max(0, shift.EntryGraceMinutes);
        var exitGrace = Math.Max(0, shift.ExitGraceMinutes);

        // إزالة البصمات المكررة (نفس الوقت) وترتيبها زمنيًا
        var relevant = punches
            .Where(p => p.PunchTime >= window.Start.AddMinutes(-policy.EarlyArrivalToleranceMinutes)
                        && p.PunchTime <= window.End.AddMinutes(policy.OvertimeToleranceMinutes))
            .GroupBy(p => p.PunchTime)
            .Select(g => g.OrderBy(x => x.Id).First())
            .OrderBy(p => p.PunchTime)
            .ThenBy(p => p.Id)
            .ToList();

        if (relevant.Count == 0)
        {
            return new DayClassificationResult
            {
                WorkDate = workDate,
                Status = ClassificationStatus.Classified,
                SuggestedStatus = AttendanceStatus.Absent,
                Reason = "لا توجد بصمات لهذا اليوم"
            };
        }

        // 1) محاولة الاعتماد على نوع الحركة الصريح القادم من الجهاز
        var withExplicit = relevant
            .Select(p => new { Punch = p, Dir = ParseExplicitDirection(p.AttType) })
            .Where(x => x.Dir != PunchDirection.Unknown)
            .ToList();

        if (withExplicit.Any(x => x.Dir == PunchDirection.In) && withExplicit.Any(x => x.Dir == PunchDirection.Out))
        {
            return BuildFromExplicit(
                withExplicit.Select(x => (x.Punch, x.Dir)).ToList(),
                window, workDate, policy, entryGrace, exitGrace);
        }

        // 2) التصنيف بالقواعد اعتمادًا على الوردية وتسلسل البصمات
        return BuildFromRules(relevant, window, workDate, policy, entryGrace, exitGrace);
    }
