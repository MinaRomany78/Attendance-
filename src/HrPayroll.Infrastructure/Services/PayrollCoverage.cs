using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;

namespace HrPayroll.Infrastructure.Services;

/// <summary>Evidence of payable elapsed days. Unknown and future days are never invented absences.</summary>
public sealed record PayrollCoverage(DateTime AsOf, int WorkedDays, int PaidRestDays, decimal PaidLeaveDays, int MissingDays)
{
    public static PayrollCoverage Calculate(Employee employee, PayrollPeriod period, DateTime today,
        IEnumerable<DailyAttendance> attendance, IEnumerable<LeaveRequest> leaves,
        ISet<DayOfWeek> restDays, ISet<DateTime> holidays)
    {
        var end = new[] { period.End, today.Date, employee.TerminationDate?.Date ?? period.End }.Min();
        var start = new[] { period.Start, employee.HireDate.Date }.Max();
        var rows = attendance.GroupBy(d => d.WorkDate.Date).ToDictionary(g => g.Key, g => g.First());
        var approvedLeaves = leaves.Where(l => l.Status == ApprovalStatus.Approved).ToList();
        var worked = 0; var rest = 0; var paidLeave = 0m; var missing = 0;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            rows.TryGetValue(date, out var row);
            if (row is not null && row.IsPresent && row.CheckOutTime!.Value.Date <= today.Date && !row.NeedsReview && row.Status is not (AttendanceStatus.Absent or AttendanceStatus.UnexcusedAbsence or AttendanceStatus.UnpaidLeave or AttendanceStatus.PaidLeave))
            { worked++; continue; }
            if (row is not null && (row.NeedsReview || row.Status is AttendanceStatus.NeedsReview or AttendanceStatus.MissingCheckIn or AttendanceStatus.MissingCheckOut))
            { missing++; continue; }
            if (row?.Status is AttendanceStatus.RestDay or AttendanceStatus.Holiday || restDays.Contains(date.DayOfWeek) || holidays.Contains(date))
            { rest++; continue; }
            var leave = approvedLeaves.FirstOrDefault(l => l.StartDate.Date <= date && l.EndDate.Date >= date);
            if (leave is { IsPaid: true } && leave.LeaveType != LeaveType.Unpaid)
            { paidLeave++; continue; }
            if (row?.Status is AttendanceStatus.Absent or AttendanceStatus.UnexcusedAbsence or AttendanceStatus.UnpaidLeave || leave is not null)
                continue;
            missing++;
        }
        return new PayrollCoverage(end, worked, rest, paidLeave, missing);
    }
}
