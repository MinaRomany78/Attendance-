using System.Data;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Infrastructure.Services;

public sealed record AdvanceBalance(Employee Employee, int EligibleDays, decimal Earned,
    decimal KnownDeductions, decimal PreviousPaid, decimal Available, bool NeedsReview, string Explanation, decimal LateDeduction = 0, decimal WorkingHours = 0,
    decimal OvertimeAmount = 0, int WorkedDays = 0, decimal RegistrationCapacity = 0, decimal ExistingRegisteredAmount = 0);

public partial class SalaryAdvanceService
{
    private static void ValidatePeriod(int year, int month, DateTime cutoff)
    {
        if (year is < 2 or > 9998 || month is < 1 or > 12 || !PayrollPeriod.ForMonth(year,month).Contains(cutoff))
            throw new InvalidOperationException("تاريخ السلفة يجب أن يكون داخل الشهر المحدد.");
    }

    public async Task<AdvanceBalance> GetBalanceAsync(int employeeId, DateTime date, CancellationToken ct = default)
        => (await GetBalancesAsync(new[] { employeeId }, date, ct)).Single();

    public async Task<List<AdvanceBalance>> GetBalancesAsync(IReadOnlyList<int> employeeIds, DateTime date, CancellationToken ct = default, PayrollPeriod? payrollPeriod = null)
    {
        var employees = await _db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToListAsync(ct);
        if (employees.Count != employeeIds.Distinct().Count()) throw new InvalidOperationException("الموظف غير موجود.");
        var policy = await _settings.GetPolicyAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(await _settings.GetAsync(SettingKeys.CompanyTimeZone, ct) ?? "Africa/Cairo");
        var now = TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, zone);
        var period = payrollPeriod ?? PayrollPeriod.FromDate(date);
        var asOf = new[] { date.Date, period.End, now.Date }.Min();
        var first = period.Start; var end = asOf.AddDays(1);
        var requireOvertimeApproval = await _settings.GetBoolAsync(SettingKeys.RequireOvertimeApproval, true, ct);
        // A bounded set of queries per department, rather than one set per employee.
        var attendance = await _db.DailyAttendances.AsNoTracking().Where(d => employeeIds.Contains(d.EmployeeId)
            && d.WorkDate >= first && d.WorkDate < end && d.WorkDate <= now.Date).ToListAsync(ct);
        var leaves = await _db.LeaveRequests.AsNoTracking().Where(l => employeeIds.Contains(l.EmployeeId)
            && l.Status == ApprovalStatus.Approved && l.StartDate < end && l.EndDate >= first).ToListAsync(ct);
        var restDays = await _settings.GetWeeklyRestDaysAsync(ct);
        var holidays = (await _db.PublicHolidays.AsNoTracking().Where(h => h.IsActive && h.HolidayDate >= first && h.HolidayDate < end)
            .Select(h => h.HolidayDate).ToListAsync(ct)).Select(d => d.Date).ToHashSet();
        var deductions = await _db.Deductions.AsNoTracking().Where(d => employeeIds.Contains(d.EmployeeId)
            && d.Year == period.Year && d.Month == period.Month && d.Status == ApprovalStatus.Approved && d.DeductionDate < end).ToListAsync(ct);
        var expenses = await _db.EmployeeExpenses.AsNoTracking().Where(e => employeeIds.Contains(e.EmployeeId)
            && e.Year == period.Year && e.Month == period.Month && e.Status == ApprovalStatus.Approved
            && e.ExpenseDate < end && e.SettlementMode == ExpenseSettlementMode.DeductFromPayroll).ToListAsync(ct);
        var loans = await _db.Loans.AsNoTracking().Where(l => employeeIds.Contains(l.EmployeeId) && l.Status == ApprovalStatus.Approved
            && !l.IsPaused && l.LoanDate < end).ToListAsync(ct);
        var repayments = await _db.LoanRepayments.AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)
            && r.Year == period.Year && r.Month == period.Month).ToListAsync(ct);
        var adjustments = await _db.PayrollAdjustments.AsNoTracking().Where(a => employeeIds.Contains(a.EmployeeId) && a.Year == period.Year
            && a.Month == period.Month && a.Status == ApprovalStatus.Approved
            && (!a.ReferenceDate.HasValue || a.ReferenceDate < end)).ToListAsync(ct);
        var overtimeApprovals = await _db.OvertimeApprovals.AsNoTracking().Where(o => employeeIds.Contains(o.EmployeeId)
            && o.Status == ApprovalStatus.Approved && o.PeriodStart <= period.End && o.PeriodEnd >= period.Start).ToListAsync(ct);
        var advances = await _db.SalaryAdvances.AsNoTracking().Where(a => employeeIds.Contains(a.EmployeeId)
            && a.PeriodStart == period.Start && a.PeriodEnd == period.End
            && (a.Status == SalaryAdvanceStatus.Registered && a.RegisteredAmount > 0 || a.PaidAmount > 0
                && (a.Status == SalaryAdvanceStatus.Paid || a.Status == SalaryAdvanceStatus.Settled))).ToListAsync(ct);
        var lockedPeriod = await FinancialPeriodGuard.OverlapsProtectedAsync(_db,period.Start,period.End,ct);
        var results = new List<AdvanceBalance>();
        foreach (var emp in employees)
        {
            var days = attendance.Where(d => d.EmployeeId == emp.Id && d.WorkDate >= emp.HireDate.Date
                && (!emp.TerminationDate.HasValue || d.WorkDate.Date <= emp.TerminationDate.Value.Date)).ToList();
            var review = days.Count == 0 || lockedPeriod || date.Date > now.Date || !emp.IsActive || days.Any(d => d.NeedsReview || d.Status == AttendanceStatus.NeedsReview
                || d.CheckOutTime > now || d.CheckOutTime >= end
                || (d.Status is AttendanceStatus.Present or AttendanceStatus.Late && !d.IsPresent));
            var eligible = days.Where(d => d.IsPresent && !d.NeedsReview && d.CheckOutTime <= now && d.CheckOutTime < end
                && d.Status is AttendanceStatus.Present or AttendanceStatus.Late or AttendanceStatus.EarlyDeparture).ToList();
            var coverage = PayrollCoverage.Calculate(emp, period, asOf,
                days, leaves.Where(l => l.EmployeeId == emp.Id), restDays, holidays);
            var empLoans = loans.Where(l => l.EmployeeId == emp.Id).ToList();
            var empAdjustments = adjustments.Where(a => a.EmployeeId == emp.Id).ToList();
            var employeeApprovals = overtimeApprovals.Where(o => o.EmployeeId == emp.Id).ToList();
            var approvedMinutes = eligible.Sum(d => d.ApprovedOvertimeMinutes);
            if (approvedMinutes == 0)
                approvedMinutes = requireOvertimeApproval
                    ? (int)Math.Round(employeeApprovals.Where(o => o.PeriodStart >= first && o.PeriodEnd < end).Sum(o => o.ApprovedHours) * 60m, MidpointRounding.AwayFromZero)
                    : eligible.Sum(d => d.RawOvertimeMinutes);
            var loanDeductions = LoanDeductionsForBalance(empLoans, repayments.Where(r => r.EmployeeId == emp.Id).ToList(), period);
            review |= coverage.MissingDays > 0 || empLoans.Any(l => (l.Installments > 1 || l.InstallmentAmount > 0) && l.RepaymentStartYear <= 0)
                || employeeApprovals.Any(o => o.PeriodStart < first || o.PeriodEnd >= end);
            var additionalRate = empAdjustments.Where(a => a.Type == AdjustmentType.AdditionalDays && a.DailyRateOverride > 0)
                .Select(a => a.DailyRateOverride).FirstOrDefault();
            var computation = PayrollCalculator.Compute(new PayrollInputs
            {
                BasicSalary = emp.BasicSalary, DailyRateOverride = emp.DailyRate, StandardWorkHours = emp.StandardWorkHours,
                LateMinutes = eligible.Sum(d => d.LateMinutes), EarlyLeaveMinutes = eligible.Sum(d => d.EarlyLeaveMinutes),
                AttendanceDurations = PayrollDurationCalculator.FromDaily(eligible),
                RawOvertimeMinutes = eligible.Sum(d => d.RawOvertimeMinutes), ApprovedOvertimeMinutes = approvedMinutes,
                PaidOvertimeMinutes = policy.RoundedOvertimeEnabled ? approvedMinutes : policy.RoundOvertimeMinutes(approvedMinutes),
                ApprovedOvertimeDurations = eligible.Where(d => d.ApprovedOvertimeMinutes > 0)
                    .Select(d => new PayrollOvertimeDuration(d.WorkDate, d.ApprovedOvertimeMinutes)).ToList(),
                HasExplicitPaidOvertimeMinutes = true, OvertimeMultiplier = policy.DefaultOvertimeMultiplier,
                Bonuses = empAdjustments.Where(a => a.Type == AdjustmentType.Bonus).Sum(a => a.Amount),
                Allowances = empAdjustments.Where(a => a.Type == AdjustmentType.Allowance).Sum(a => a.Amount),
                AdditionalDays = empAdjustments.Where(a => a.Type == AdjustmentType.AdditionalDays).Sum(a => a.Days ?? 0), AdditionalDaysRate = additionalRate
            }, policy);
            var earned = PayrollCalculator.Round2(EarnedSalaryCalculator.Calculate(emp.BasicSalary, computation.DailyRate,
                coverage.WorkedDays, policy.MonthlySalaryIncludesPaidRestDays ? coverage.PaidRestDays : 0, coverage.PaidLeaveDays)
                + computation.OvertimeAmount + computation.Bonuses + computation.Allowances + computation.AdditionalDaysAmount);
            var previous = advances.Where(a => a.EmployeeId == emp.Id && a.Status != SalaryAdvanceStatus.Registered).Sum(a => a.PaidAmount);
            var registered = advances.Where(a => a.EmployeeId == emp.Id && a.Status == SalaryAdvanceStatus.Registered).Sum(a => a.RegisteredAmount ?? 0);
            var known = PayrollCalculator.Round2(deductions.Where(d => d.EmployeeId == emp.Id).Sum(d => d.Amount)
                + expenses.Where(e => e.EmployeeId == emp.Id).Sum(e => e.Amount) + computation.AttendanceDeductions
                + empAdjustments.Where(a => a.Type == AdjustmentType.OtherDeduction).Sum(a => a.Amount) + loanDeductions);
            var capacity = PayrollCalculator.Round2(Math.Max(0, earned - known - previous));
            var available = PayrollCalculator.Round2(Math.Max(0, earned - known - previous - registered));
            results.Add(new(emp, coverage.WorkedDays + coverage.PaidRestDays + (int)coverage.PaidLeaveDays, earned, known, previous, available, review,
                $"عمل فعلي {coverage.WorkedDays}؛ راحة مدفوعة {(policy.MonthlySalaryIncludesPaidRestDays ? coverage.PaidRestDays : 0)}؛ إجازة مدفوعة {coverage.PaidLeaveDays}؛ بيانات عمل ناقصة {coverage.MissingDays}؛ الأجر اليومي {computation.DailyRate}; إضافي معتمد {computation.OvertimeAmount}; خصومات {known} منها قروض {loanDeductions}; سلف مدفوعة سابقة {previous}.",
                computation.LateDeduction, eligible.Sum(d => d.WorkedMinutes) / 60m, computation.OvertimeAmount, coverage.WorkedDays, capacity, registered));
        }
        return results;
    }

    // Read-only projection of due installments; displaying balances must never create accounting rows.
    private static decimal LoanDeductionsForBalance(IReadOnlyList<Loan> loans, IReadOnlyList<LoanRepayment> repayments, PayrollPeriod period)
    {
        var amount = 0m;
        foreach (var loan in loans)
        {
            if (loan.Installments <= 1 && loan.InstallmentAmount <= 0)
            {
                if (loan.DeductionYear == period.Year && loan.DeductionMonth == period.Month) amount += loan.Amount;
                continue;
            }
            var existing = repayments.SingleOrDefault(r => r.LoanId == loan.Id);
            if (existing is not null)
            {
                if (existing.Status is LoanRepaymentStatus.Due or LoanRepaymentStatus.Collected) amount += existing.DueAmount;
                continue;
            }
            if (loan.RepaymentStartYear <= 0 || loan.RepaymentStartMonth is < 1 or > 12 || loan.OutstandingBalance <= 0) continue;
            var index = period.Year * 12 + period.Month;
            var firstIndex = loan.RepaymentStartYear * 12 + loan.RepaymentStartMonth;
            if (index < firstIndex || loan.Installments > 0 && index >= firstIndex + loan.Installments) continue;
            var baseDue = loan.InstallmentAmount > 0 ? loan.InstallmentAmount : loan.Amount / Math.Max(1, loan.Installments);
            var due = PayrollCalculator.Round2(Math.Min(baseDue, loan.OutstandingBalance));
            if (loan.InstallmentAmount <= 0 && loan.Installments > 0 && index == firstIndex + loan.Installments - 1
                && loan.OutstandingBalance <= due + 0.01m) due = PayrollCalculator.Round2(loan.OutstandingBalance);
            amount += due;
        }
        return PayrollCalculator.Round2(amount);
    }

    private static SalaryAdvance Snapshot(AdvanceBalance b, DateTime date, string? user) => new()
    {
        EmployeeId = b.Employee.Id, Year = PayrollPeriod.FromDate(date).Year, Month = PayrollPeriod.FromDate(date).Month, CutoffDate = date.Date,
        PeriodStart = PayrollPeriod.FromDate(date).Start, PeriodEnd = PayrollPeriod.FromDate(date).End,
        BasicSalary = b.Employee.BasicSalary, EarnedAmount = b.Earned, AttendanceDays = b.WorkedDays,
        LateDeduction = b.LateDeduction, WorkingHours = b.WorkingHours, OvertimeAmount = b.OvertimeAmount,
        ProposedAmount = b.RegistrationCapacity, ApprovedAmount = b.RegistrationCapacity,
        Notes = (b.NeedsReview ? "يحتاج مراجعة. " : "") + b.Explanation, CreatedBy = user
    };

}
