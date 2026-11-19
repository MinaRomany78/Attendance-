using System.Security.Cryptography;
using System.Text.Json;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Infrastructure.Services;

/// <summary>Detects changed source evidence without rewriting financial snapshots or settlement links.</summary>
public static class PayrollFinancialEvidence
{
    public static async Task EnsureCurrentAsync(AppDbContext db, PayrollRun run, CancellationToken ct = default)
    {
        // Null identifies historical snapshots that predate this protection; never fabricate their evidence.
        if (run.FinancialEvidenceHash == null) return;
        if (run.FinancialEvidenceHash != await CalculateAsync(db, run, ct))
            throw new InvalidOperationException("تغيرت بيانات الحضور أو المعاملات المالية أو بيانات التعاقد بعد احتساب الكشف. أعد احتساب نفس الدورة قبل التحرير أو الاعتماد.");
    }

    public static async Task<string> CalculateAsync(AppDbContext db, PayrollRun run, CancellationToken ct = default)
    {
        var p = PayrollPeriod.ForRun(run);
        var employeeIds = await db.PayrollItems.Where(i => i.PayrollRunId == run.Id).Select(i => i.EmployeeId).ToListAsync(ct);
        var employees = await db.Employees.AsNoTracking()
            .Where(e => employeeIds.Contains(e.Id) || e.HireDate <= p.End && (!e.TerminationDate.HasValue || e.TerminationDate >= p.Start))
            .OrderBy(e => e.Id).Select(e => new { e.Id, e.BasicSalary, e.DailyRate, e.StandardWorkHours, e.HireDate, e.TerminationDate, e.EmploymentStatus }).ToListAsync(ct);
        employeeIds = employees.Select(e => e.Id).ToList();
        var attendance = await db.DailyAttendances.AsNoTracking()
            .Where(d => employeeIds.Contains(d.EmployeeId) && d.WorkDate >= p.Start && d.WorkDate <= p.End).OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.EmployeeId, d.WorkDate, d.CheckInTime, d.CheckOutTime, d.ShiftStart, d.ShiftEnd, d.Status,
                d.PunchReviewStatus, d.HasIncompletePunches, d.LateMinutes, d.EarlyLeaveMinutes, d.RawOvertimeMinutes,
                d.ApprovedOvertimeMinutes, d.PaidOvertimeMinutes, d.LeaveRequestId }).ToListAsync(ct);
        var advances = await db.SalaryAdvances.AsNoTracking()
            .Where(a => employeeIds.Contains(a.EmployeeId) && a.PeriodStart == p.Start && a.PeriodEnd == p.End
                && (a.Status == SalaryAdvanceStatus.Registered && a.RegisteredAmount > 0
                    || a.PaidAmount > 0 && (a.Status == SalaryAdvanceStatus.Paid || a.Status == SalaryAdvanceStatus.Settled))).OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.EmployeeId, Amount = a.Status == SalaryAdvanceStatus.Registered ? a.RegisteredAmount ?? 0 : a.PaidAmount, a.PeriodStart, a.PeriodEnd }).ToListAsync(ct);
        var deductions = await db.Deductions.AsNoTracking().Where(d => employeeIds.Contains(d.EmployeeId)
            && d.Year == p.Year && d.Month == p.Month && d.Status == ApprovalStatus.Approved).OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.EmployeeId, d.Amount }).ToListAsync(ct);
        var expenses = await db.EmployeeExpenses.AsNoTracking().Where(e => employeeIds.Contains(e.EmployeeId)
            && e.Year == p.Year && e.Month == p.Month && e.Status == ApprovalStatus.Approved && e.SettlementMode == ExpenseSettlementMode.DeductFromPayroll).OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.EmployeeId, e.Amount }).ToListAsync(ct);
        var adjustments = await db.PayrollAdjustments.AsNoTracking().Where(a => employeeIds.Contains(a.EmployeeId)
            && a.Year == p.Year && a.Month == p.Month && a.Status == ApprovalStatus.Approved).OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.EmployeeId, a.Type, a.Amount, a.Days, a.DailyRateOverride }).ToListAsync(ct);
        var overtime = await db.OvertimeApprovals.AsNoTracking().Where(o => employeeIds.Contains(o.EmployeeId)
            && o.PeriodStart <= p.End && o.PeriodEnd >= p.Start && o.Status == ApprovalStatus.Approved).OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.EmployeeId, o.PeriodStart, o.PeriodEnd, o.ApprovedHours }).ToListAsync(ct);
        var leaves = await db.LeaveRequests.AsNoTracking().Where(l => employeeIds.Contains(l.EmployeeId)
            && l.StartDate <= p.End && l.EndDate >= p.Start && l.Status == ApprovalStatus.Approved).OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.EmployeeId, l.StartDate, l.EndDate, l.LeaveType, l.IsPaid, l.WorkingDays }).ToListAsync(ct);
        var loans = await db.Loans.AsNoTracking().Where(l => employeeIds.Contains(l.EmployeeId) && l.Status == ApprovalStatus.Approved
            && (l.DeductionYear == p.Year && l.DeductionMonth == p.Month || l.Installments > 1 || l.InstallmentAmount > 0)).OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.EmployeeId, l.Amount, l.Installments, l.InstallmentAmount, l.OutstandingBalance,
                l.DeductionYear, l.DeductionMonth, l.RepaymentStartYear, l.RepaymentStartMonth, l.IsPaused }).ToListAsync(ct);
        var repayments = await db.LoanRepayments.AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)
            && r.Year == p.Year && r.Month == p.Month).OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.EmployeeId, r.LoanId, r.DueAmount, Skipped = r.Status == LoanRepaymentStatus.Skipped,
                Adjusted = r.Status == LoanRepaymentStatus.Adjusted }).ToListAsync(ct);
        var policySettings = await db.AppSettings.AsNoTracking().OrderBy(s => s.Key)
            .Select(s => new { s.Key, s.Value }).ToListAsync(ct);
        // Paid→Settled and Due→Collected are bookkeeping changes, not new economic evidence.
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { employees, attendance, advances, deductions, expenses, adjustments, overtime, leaves, loans, repayments, policySettings })));
    }
}
