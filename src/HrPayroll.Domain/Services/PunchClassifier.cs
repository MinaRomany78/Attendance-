using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.ValueObjects;

namespace HrPayroll.Domain.Services;

public static class PunchClassifier
{
    private static readonly string[] ExplicitInValues =
        { "i", "in", "0", "checkin", "check-in", "entry", "xÏÎæá", "ÍÖæÑ" };

    private static readonly string[] ExplicitOutValues =
        { "o", "out", "1", "checkout", "check-out", "exit", "ÎÑæÌ", "ÇäÕÑÇÝ" };

    public static PunchDirection ParseExplicitDirection(string? attType)
    {
        if (string.IsNullOrWhiteSpace(attType)) return PunchDirection.Unknown;
        var v = attType.Trim().ToLowerInvariant();
        if (v is "fp" or "fingerprint" or "ÈÕãÉ" or "normal" or "attend") return PunchDirection.Unknown;
        if (ExplicitInValues.Contains(v)) return PunchDirection.In;
        if (ExplicitOutValues.Contains(v)) return PunchDirection.Out;
        return PunchDirection.Unknown;
    }

    public static DayClassificationResult Classify(
        IReadOnlyList<RawPunchInput> punches,
        Shift shift,
        DateTime workDate,
        AttendancePolicy policy)
    {
        var window = ShiftWindowResolver.Resolve(shift, workDate);
        var entryGrace = Math.Max(0, shift.EntryGraceMinutes);
        var exitGrace = Math.Max(0, shift.ExitGraceMinutes);
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
                Reason = "áÇ ÊæÌÏ ÈÕãÇÊ áåÐÇ Çáíæã"
            };
        }
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
        return BuildFromRules(relevant, window, workDate, policy, entryGrace, exitGrace);
    }

    private static DayClassificationResult BuildFromExplicit(
        List<(RawPunchInput Punch, PunchDirection Dir)> items,
        ShiftWindow window, DateTime workDate, AttendancePolicy policy, int entryGrace, int exitGrace)
    {
        var events = new List<ClassifiedEvent>();
        DateTime? entry = null, exit = null;
        int? entryId = null, exitId = null;
        var review = false;
        foreach (var (punch, dir) in items.OrderBy(x => x.Punch.PunchTime))
        {
            var type = dir == PunchDirection.In ? AttendanceEventType.Entry : AttendanceEventType.Exit;
            var inWindow = punch.PunchTime >= window.Start.AddMinutes(-policy.EarlyArrivalToleranceMinutes)
                           && punch.PunchTime <= window.End.AddMinutes(policy.OvertimeToleranceMinutes);
            var status = inWindow ? ClassificationStatus.Classified : ClassificationStatus.NeedsReview;
            if (!inWindow) review = true;
            events.Add(new ClassifiedEvent(punch, type, status, ClassificationSource.ExplicitDeviceType,
                inWindow ? 1.0m : 0.5m, inWindow ? null : "ÇáÍÑßÉ ÎÇÑÌ äØÇÞ ÇáæÑÏíÉ"));
            if (type == AttendanceEventType.Entry && (entry == null || punch.PunchTime < entry.Value))
            {
                entry = punch.PunchTime; entryId = punch.Id;
            }
            if (type == AttendanceEventType.Exit && (exit == null || punch.PunchTime > exit.Value))
            {
                exit = punch.PunchTime; exitId = punch.Id;
            }
        }
        return Finalize(workDate, window, policy, events, entry, exit, entryId, exitId, review,
            "ÊÕäíÝ ÍÓÈ äæÚ ÇáÍÑßÉ ÇáÕÑíÍ ãä ÇáÌåÇÒ", entryGrace, exitGrace);
    }
}
