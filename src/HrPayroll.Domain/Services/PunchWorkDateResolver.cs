using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.ValueObjects;

namespace HrPayroll.Domain.Services;

/// <summary>
/// يُسند كل بصمة خام إلى يوم عمل واحد فقط.
///
/// المشكلة: البصمة الواحدة قد تقع داخل أكثر من نافذة وردية، وإسنادها ليومين يجعلها تُحتسب مرتين.
/// القاعدة المطبَّقة (بالأولوية): الجودة، ثم الوردية المُسندة، ثم تاريخ البصمة نفسه، ثم الأقرب للحافة.
/// </summary>
public static class PunchWorkDateResolver
{
    public sealed record PunchWorkDate(int PunchId, DateTime WorkDate, bool IsAmbiguous, int? ShiftId);

    private sealed record Candidate(DateTime Date, int ShiftId, int Quality, bool OwnDate, double EdgeDistance);

    public static Dictionary<int, PunchWorkDate> Assign(
        IReadOnlyList<RawPunchInput> punches,
        IReadOnlyList<Shift> shifts,
        AttendancePolicy policy,
        Func<DateTime, int?> assignedShiftIdFor)
    {
        var result = new Dictionary<int, PunchWorkDate>();
        var active = shifts.Where(s => s.IsActive).ToList();

        if (active.Count == 0 || punches.Count == 0)
        {
            foreach (var p in punches) result[p.Id] = new PunchWorkDate(p.Id, p.PunchTime.Date, false, null);
            return result;
        }

        var entryTolerance = Math.Max(0, policy.EarlyArrivalToleranceMinutes);
        var exitTolerance = Math.Max(0, policy.OvertimeToleranceMinutes);
        var entryGrace = Math.Max(0, policy.EntryGraceMinutes);
        var exitGrace = Math.Max(0, policy.ExitGraceMinutes);

        foreach (var punch in punches)
        {
            var candidates = new List<Candidate>();

            foreach (var shift in active)
            {
                var dates = new[] { punch.PunchTime.Date, punch.PunchTime.Date.AddDays(-1) };
                foreach (var date in dates)
                {
                    var window = ShiftWindowResolver.Resolve(shift, date);
                    if (punch.PunchTime < window.Start.AddMinutes(-entryTolerance)) continue;
                    if (punch.PunchTime > window.End.AddMinutes(exitTolerance)) continue;

                    var inside = punch.PunchTime >= window.Start && punch.PunchTime <= window.End;
                    var adjacent = !inside
                        && (punch.PunchTime < window.Start
                                ? punch.PunchTime >= window.Start.AddMinutes(-entryGrace)
                                : punch.PunchTime <= window.End.AddMinutes(exitGrace));

                    var quality = inside ? 0 : adjacent ? 1 : 2;
                    if (assignedShiftIdFor(date) == shift.Id) quality = Math.Max(0, quality - 1);

                    var edge = Math.Min(
                        Math.Abs((punch.PunchTime - window.Start).TotalMinutes),
                        Math.Abs((punch.PunchTime - window.End).TotalMinutes));

                    candidates.Add(new Candidate(date, shift.Id, quality, date == punch.PunchTime.Date, edge));
                }
            }

            if (candidates.Count == 0)
            {
                result[punch.Id] = new PunchWorkDate(punch.Id, punch.PunchTime.Date, false, null);
                continue;
            }

            var ordered = candidates
                .OrderBy(c => c.Quality)
                .ThenBy(c => c.OwnDate ? 0 : 1)
                .ThenBy(c => c.EdgeDistance)
                .ThenBy(c => c.Date)
                .ThenBy(c => c.ShiftId)
                .ToList();

            var best = ordered[0];
            var ambiguous = ordered.Any(c => c.Date != best.Date
                                             && c.Quality == best.Quality
                                             && c.OwnDate == best.OwnDate);

            result[punch.Id] = new PunchWorkDate(punch.Id, best.Date, ambiguous, best.ShiftId);
        }

        return result;
    }
}
