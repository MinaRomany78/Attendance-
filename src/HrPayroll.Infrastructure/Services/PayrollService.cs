using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HrPayroll.Infrastructure.Services;

public sealed class PayrollGenerationResult
{
    public int PayrollRunId { get; set; }
    public int ItemCount { get; set; }
    public decimal TotalNet { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalDeductions { get; set; }
    public int Version { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>تعديلات يدوية محفوظة على سطر كشف سابق لنفس الفترة (تُنقل للكشف الجديد عند إعادة الاحتساب).</summary>
internal sealed class ManualCarry
{
    public decimal AbsenceDays { get; init; }
    public decimal AnnualLeaveDays { get; init; }
    public decimal AnnualLeaveBalanceApplied { get; init; }
    public decimal UnpaidLeaveDays { get; init; }
    public decimal Bonuses { get; init; }
    public decimal Allowances { get; init; }
    public decimal OtherDeductions { get; init; }
    public decimal LoansTotal { get; init; }
    public decimal? SystemFinancialDeductions { get; init; }
    public decimal? EmployeeExpenseRecoveries { get; init; }
    public decimal AdditionalDaysCount { get; init; }
    public decimal? AdditionalDaysRate { get; init; }
    public decimal? ManualOvertimeHours { get; init; }
    public string? LastEditedBy { get; init; }
    public DateTime? LastEditedAt { get; init; }
}

/// <summary>جاهزية الفترة للاحتساب: عدد الأيام الناقصة وحالات المراجعة.</summary>
public sealed class PayrollReadiness
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int ActiveEmployees { get; set; }
    public int DaysWithoutAttendance { get; set; }
    public int NeedsReviewCount { get; set; }
    public int IncompletePunchCount { get; set; }
    public int PendingOvertimeDays { get; set; }
    public int PendingLeaves { get; set; }
    public int PendingAdjustments { get; set; }
    public bool IsReady => DaysWithoutAttendance == 0 && PendingLeaves == 0 && PendingAdjustments == 0;
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// توليد وإدارة كشوف الرواتب الشهرية.
/// يعتمد على الحضور النهائي + الإضافي المعتمد + الإجازات + الفرائض والعطلات + التسويات.
/// كل مكوّن يُحسب مرة واحدة فقط ويُربط بالكشف لمنع الازدواج بين الفترات.
/// </summary>
public class PayrollService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly ILogger<PayrollService> _logger;
    private readonly TimeProvider _clock;

    public PayrollService(AppDbContext db, SettingsService settings, AuditService audit, ILogger<PayrollService> logger, TimeProvider? clock = null)
    {
        _db = db;
        _settings = settings;
        _audit = audit;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private static (DateTime First, DateTime Last) Period(int year, int month)
    {
        var period = PayrollPeriod.ForMonth(year,month);
        return (period.Start,period.End);
    }

    /// <summary>الراتب الشهري المستخدم للموظف في الفترة (حسب تاريخ التعيين/إنهاء الخدمة).</summary>
    private static bool EligibleForPeriod(Employee e, DateTime first, DateTime last)
    {
        if (e.HireDate.Date > last) return false;
        if (e.TerminationDate.HasValue && e.TerminationDate.Value.Date < first) return false;
        return e.EmploymentStatus != EmploymentStatus.Terminated || e.TerminationDate.HasValue;
    }

    /// <summary>يفحص جاهزية الفترة قبل الاحتساب.</summary>
    public async Task<PayrollReadiness> GetReadinessAsync(int year, int month, CancellationToken ct = default)
    {
        var (first, last) = Period(year, month);
        var readiness = new PayrollReadiness { Year = year, Month = month };

        var employees = await _db.Employees.AsNoTracking().Where(e => e.IsActive).ToListAsync(ct);
        readiness.ActiveEmployees = employees.Count;

        var rows = await _db.DailyAttendances.AsNoTracking()
            .Where(d => d.WorkDate >= first && d.WorkDate <= last)
            .Select(d => new { d.Status, d.PunchReviewStatus, d.HasIncompletePunches, d.EmployeeId })
            .ToListAsync(ct);

        readiness.NeedsReviewCount = rows.Count(r => r.PunchReviewStatus == ClassificationStatus.NeedsReview
                                                     || r.Status == AttendanceStatus.NeedsReview);
        readiness.IncompletePunchCount = rows.Count(r => r.HasIncompletePunches);
        readiness.PendingOvertimeDays = rows.Count(r => r.Status == AttendanceStatus.NeedsReview);

        var attended = rows.Where(r => r.Status is AttendanceStatus.Present or AttendanceStatus.Late
                                        or AttendanceStatus.EarlyDeparture or AttendanceStatus.NeedsReview
                                        or AttendanceStatus.MissingCheckIn or AttendanceStatus.MissingCheckOut)
            .Select(r => r.EmployeeId).Distinct().Count();

        readiness.DaysWithoutAttendance = attended == 0 ? 1 : 0;
        if (rows.Count == 0)
            readiness.Warnings.Add("لا توجد أي بيانات حضور لهذه الفترة. استورد ملفات البصمة واعالج الحضور أولًا.");
        if (readiness.IncompletePunchCount > 0)
            readiness.Warnings.Add($"يوجد {readiness.IncompletePunchCount} يوم ببصمة ناقصة (دخول أو خروج مفقود) — تحتاج مراجعة قبل الاحتساب.");
        if (readiness.NeedsReviewCount > 0)
            readiness.Warnings.Add($"يوجد {readiness.NeedsReviewCount} يوم بحالة \"يحتاج مراجعة\".");

        readiness.PendingLeaves = await _db.LeaveRequests.CountAsync(
            l => l.Status == ApprovalStatus.Pending && l.StartDate <= last && l.EndDate >= first, ct);
        readiness.PendingAdjustments = await _db.PayrollAdjustments.CountAsync(
            a => a.Status == ApprovalStatus.Pending && a.Year == year && a.Month == month, ct);

        if (readiness.PendingLeaves > 0)
            readiness.Warnings.Add($"يوجد {readiness.PendingLeaves} طلب إجازة قيد الاعتماد داخل الفترة.");
        if (readiness.PendingAdjustments > 0)
            readiness.Warnings.Add($"يوجد {readiness.PendingAdjustments} تسوية مالية قيد الاعتماد داخل الفترة.");

        return readiness;
    }

    /// <summary>يولد/يعيد احتساب كشف رواتب الشهر داخل معاملة واحدة.</summary>
    public async Task<PayrollGenerationResult> GenerateAsync(
        int year, int month, string? user = null, string? userId = null, CancellationToken ct = default)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));

        var policy = await _settings.GetPolicyAsync(ct);
        var companyToday = await CompanyPayrollClock.TodayAsync(_db, ct, _clock);
        var requireApproval = await _settings.GetBoolAsync(SettingKeys.RequireOvertimeApproval, true, ct);
        var (first, last) = Period(year, month);
        var result = new PayrollGenerationResult();

        await FinancialPeriodGuard.EnsureEditableAsync(_db, first, last, ct);
        var legacyRuns = await _db.PayrollRuns.AsNoTracking().Where(r=>r.PeriodStart==null && r.Status!=PayrollStatus.Cancelled).ToListAsync(ct);
        if (legacyRuns.Any(r=>PayrollPeriod.ForRun(r).Overlaps(first,last)))
            throw new InvalidOperationException("دورة 26–25 تتداخل مع كشف تاريخي. يلزم حل التعارض عبر إعادة الفتح/الإلغاء المعتمد دون تغيير التاريخ تلقائياً.");
        var existing = await _db.PayrollRuns.Where(r => r.Year == year && r.Month == month).ToListAsync(ct);

        if (existing.Any(r => r.Status != PayrollStatus.Cancelled && (r.PeriodStart != first || r.PeriodEnd != last)))
            throw new InvalidOperationException("يوجد كشف تاريخي بنطاق مختلف. لا يمكن استبدال نطاقه تلقائياً؛ يلزم تصحيح معتمد قبل توليد الدورة الجديدة.");
        if (await _db.SalaryAdvances.AnyAsync(a => a.Status == SalaryAdvanceStatus.Paid && a.PayrollItemId == null && a.PeriodStart == null
            && (a.Year == year && a.Month == month || a.CutoffDate >= first && a.CutoffDate <= last), ct))
            throw new InvalidOperationException("توجد سلف تاريخية مصروفة تحتاج تحديد دورة التسوية قبل احتساب رواتب 26–25.");
        var locked = existing.FirstOrDefault(r => r.Status is PayrollStatus.Approved or PayrollStatus.Locked);
        if (locked is not null)
            throw new InvalidOperationException(
                $"كشف رواتب {year}-{month:00} مقفل (الحالة: {locked.Status}). " +
                "لا يمكن إعادة الاحتساب تلقائيًا. استخدم \"إعادة الفتح\" مع تسجيل السبب أولًا.");

        // حماية: لا يُحتسب كشف لفترة لم تُعالَج فيها أي بيانات حضور، لأن ذلك كان
        // يُنتج رواتب كاملة بدون أي خصم رغم غياب البيانات (نتيجة صامتة غير صحيحة).
        // يمكن للأدمن السماح بذلك صراحةً من الإعدادات عند الحاجة.
        if (!await _settings.GetBoolAsync(SettingKeys.AllowPayrollWithoutAttendance, false, ct))
        {
            var hasAttendance = await _db.DailyAttendances.AnyAsync(d => d.WorkDate >= first && d.WorkDate <= last, ct);
            if (!hasAttendance)
                throw new InvalidOperationException(
                    $"لا توجد أي بيانات حضور معالجة للفترة {year}-{month:00}. " +
                    "استورد ملفات البصمة ثم عالج الحضور لهذه الفترة قبل احتساب الكشف.");
        }

        var previousVersion = existing.Count == 0 ? 0 : existing.Max(r => r.Version);

        // تعديلات يدوية من كشوف سابقة لنفس الفترة: تُنقل كما هي حتى لا يفقد المستخدم عمله عند إعادة الاحتساب،
        // ومعها مقدار الرصيد السنوي المخصوم سابقًا حتى لا يُخصم مرتين.
        var previousRunIds = existing.Where(r => r.Status != PayrollStatus.Cancelled).Select(r => r.Id).ToList();
        var previousManual = new Dictionary<int, ManualCarry>();
        if (previousRunIds.Count > 0)
        {
            var previousItems = await _db.PayrollItems.AsNoTracking()
                .Where(i => previousRunIds.Contains(i.PayrollRunId) && i.IsManuallyAdjusted)
                .ToListAsync(ct);
            foreach (var g in previousItems.GroupBy(i => i.EmployeeId))
            {
                var latest = g.OrderByDescending(i => i.Id).First();
                previousManual[g.Key] = new ManualCarry
                {
                    AbsenceDays = latest.AbsenceDays,
                    AnnualLeaveDays = latest.AnnualLeaveDays,
                    AnnualLeaveBalanceApplied = latest.AnnualLeaveBalanceApplied,
                    UnpaidLeaveDays = latest.UnpaidLeaveDays,
                    Bonuses = latest.Bonuses,
                    Allowances = latest.Allowances,
                    OtherDeductions = latest.OtherDeductions,
                    LoansTotal = latest.LoansTotal, SystemFinancialDeductions = latest.SystemFinancialDeductions,
                    EmployeeExpenseRecoveries = latest.EmployeeExpenseRecoveries,
                    AdditionalDaysCount = latest.AdditionalDaysCount,
                    AdditionalDaysRate = latest.AdditionalDaysRate,
                    ManualOvertimeHours = latest.ManualOvertimeHours,
                    LastEditedBy = latest.LastEditedBy,
                    LastEditedAt = latest.LastEditedAt
                };
            }
        }

        await using var tx = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await FinancialPeriodGuard.EnsureEditableAsync(_db, first, last, ct);

        // أرشفة الكشوف السابقة وفصل ارتباطاتها حتى لا تُخصم/تُضاف المكوّنات مرتين
        foreach (var oldRun in existing.Where(r => r.Status != PayrollStatus.Cancelled))
        {
            var oldItemIds = await _db.PayrollItems.Where(i => i.PayrollRunId == oldRun.Id).Select(i => i.Id).ToListAsync(ct);
            await UnlinkAsync(oldItemIds, ct);
            oldRun.Status = PayrollStatus.Cancelled;
            oldRun.UpdatedAt = DateTime.UtcNow;
            oldRun.UpdatedBy = user;
        }

        var run = new PayrollRun
        {
            Year = year,
            Month = month,
            PeriodStart = first, PeriodEnd = last,
            Status = PayrollStatus.Computed,
            Version = previousVersion + 1,
            ComputedAt = DateTime.UtcNow,
            ComputedBy = user,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = user
        };
        _db.PayrollRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var allEmployees = await _db.Employees.ToListAsync(ct);
        var employees = allEmployees.Where(e => EligibleForPeriod(e, first, last)).ToList();
        var employeeIds = employees.Select(e => e.Id).ToList();

        // ===== أيام الفترة =====
        var daily = await _db.DailyAttendances.AsNoTracking()
            .Where(d => employeeIds.Contains(d.EmployeeId) && d.WorkDate >= first && d.WorkDate <= last)
            .ToListAsync(ct);

        // ===== الإضافي المعتمد =====
        if (await _db.OvertimeApprovals.AnyAsync(o=>employeeIds.Contains(o.EmployeeId) && o.Status==ApprovalStatus.Approved
            && o.PayrollItemId==null && o.PeriodStart<=last && o.PeriodEnd>=first && (o.PeriodStart<first || o.PeriodEnd>last),ct))
            throw new InvalidOperationException("يوجد اعتماد إضافي إجمالي يتجاوز حدود الدورة. يلزم تفصيل الاعتماد حسب 26–25 قبل الاحتساب.");
        var approvedApprovals = await _db.OvertimeApprovals
            .Where(o => employeeIds.Contains(o.EmployeeId) && o.Status == ApprovalStatus.Approved
                        && o.PeriodStart >= first && o.PeriodEnd <= last && o.PeriodEnd <= companyToday && o.PayrollItemId == null)
            .ToListAsync(ct);
        var approvalHoursByEmp = approvedApprovals
            .GroupBy(o => o.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.ApprovedHours));

        // ===== السلف والخصومات والتسويات والإجازات المعتمدة =====
        var loans = await _db.Loans
            .Where(l => employeeIds.Contains(l.EmployeeId) && l.DeductionYear == year && l.DeductionMonth == month
                        && l.Status == ApprovalStatus.Approved && l.PayrollItemId == null && !l.IsPaused)
            .ToListAsync(ct);

        // سلف منتصف الشهر المدفوعة وغير المُسوَّاة تُسترد من كشف نفس الشهر.
        var midAdvances = await _db.SalaryAdvances
            .Where(a => employeeIds.Contains(a.EmployeeId) && a.Year == year && a.Month == month
                        && a.PeriodStart == first && a.PeriodEnd == last
                        && (a.Status == SalaryAdvanceStatus.Paid || a.Status == SalaryAdvanceStatus.Registered) && a.PayrollItemId == null)
            .ToListAsync(ct);

        // مصاريف الموظفين المعتمدة القابلة للخصم وغير المُسوَّاة.
        // (المشمولة ضمن سلفة أو المدفوعة نقدًا لا تُخصم هنا — تُسوَّى عبر السلفة أو للتوثيق فقط.)
        var expenses = await _db.EmployeeExpenses
            .Where(e => employeeIds.Contains(e.EmployeeId) && e.Year == year && e.Month == month
                        && e.Status == ApprovalStatus.Approved
                        && e.SettlementMode == ExpenseSettlementMode.DeductFromPayroll
                        && e.PayrollItemId == null)
            .ToListAsync(ct);

        // أقساط القروض المجدولة: تُنشأ صفوف الاستحقاق للشهر عند الحاجة (قسط واحد لكل قرض×شهر).
        if (await _db.Loans.AnyAsync(l => employeeIds.Contains(l.EmployeeId) && l.Status == ApprovalStatus.Approved
            && !l.IsPaused && (l.Installments > 1 || l.InstallmentAmount > 0) && (l.RepaymentStartYear < 1 || l.RepaymentStartMonth < 1 || l.RepaymentStartMonth > 12), ct))
            throw new InvalidOperationException("يوجد قرض بأقساط دون تاريخ بداية سداد صحيح. اضبط سنة وشهر بداية السداد قبل احتساب الرواتب؛ لن يُخصم كامل القرض تلقائياً.");
        var scheduledLoans = await _db.Loans
            .Where(l => employeeIds.Contains(l.EmployeeId) && l.Status == ApprovalStatus.Approved
                        && !l.IsPaused && (l.Installments > 1 || l.InstallmentAmount > 0)
                        && l.RepaymentStartYear > 0 && l.OutstandingBalance > 0)
            .ToListAsync(ct);
        var repayments = await EnsureRepaymentRowsAsync(scheduledLoans, year, month, user, ct);

        var deductions = await _db.Deductions
            .Where(d => employeeIds.Contains(d.EmployeeId) && d.Year == year && d.Month == month
                        && d.Status == ApprovalStatus.Approved && d.PayrollItemId == null)
            .ToListAsync(ct);

        var adjustments = await _db.PayrollAdjustments
            .Where(a => employeeIds.Contains(a.EmployeeId) && a.Year == year && a.Month == month
                        && a.Status == ApprovalStatus.Approved && a.PayrollItemId == null)
            .ToListAsync(ct);

        // الإجازات المعتمدة التي تغطي أيامًا داخل الفترة ولم تُستهلك في كشف سابق
        var leaves = await _db.LeaveRequests
            .Where(l => employeeIds.Contains(l.EmployeeId) && l.Status == ApprovalStatus.Approved
                        && l.StartDate <= last && l.EndDate >= first)
            .ToListAsync(ct);

        var restDays = await _settings.GetWeeklyRestDaysAsync(ct);
        var holidayDates = (await _db.PublicHolidays.AsNoTracking().Where(h=>h.IsActive && h.HolidayDate>=first && h.HolidayDate<=last)
            .Select(h=>h.HolidayDate).ToListAsync(ct)).Select(d=>d.Date).ToHashSet();
        var lockedAt = DateTime.UtcNow;

        foreach (var emp in employees)
        {
            var empDays = daily.Where(d => d.EmployeeId == emp.Id && d.WorkDate <= companyToday && d.WorkDate >= emp.HireDate.Date && (!emp.TerminationDate.HasValue || d.WorkDate <= emp.TerminationDate.Value.Date)).ToList();
            var empLeaves = leaves.Where(l => l.EmployeeId == emp.Id).ToList();
            var coverage = PayrollCoverage.Calculate(emp, new PayrollPeriod(first, last), companyToday, empDays, empLeaves, restDays, holidayDates);

            // أيام الحضور الفعلي
            var attendanceDays = coverage.WorkedDays;

            // أيام الراحة والعطلات والغياب
            var paidRestDays = coverage.PaidRestDays;
            var paidHolidayDays = empDays.Count(d => d.Status == AttendanceStatus.Holiday && !d.CheckInTime.HasValue);

            // تصنيف الإجازات: نعتمد على أيام الحضور المُصنَّفة، مع الرجوع لطلبات الإجازة عند عدم وجود أيام
            var paidLeaveDays = coverage.PaidLeaveDays;
            var unpaidLeaveDays = empDays.Count(d => d.Status == AttendanceStatus.UnpaidLeave) / 1m;

            if (empDays.Count == 0 && empLeaves.Count > 0)
            {
                // لم تتم معالجة الحضور بعد: نستخدم أيام العمل من طلبات الإجازة
                foreach (var l in empLeaves)
                {
                    if (l.WorkingDays != decimal.Truncate(l.WorkingDays)) throw new InvalidOperationException("الإجازة الجزئية تحتاج تفصيلاً يومياً معتمداً قبل توزيعها على الدورة.");
                    var start = new[] { first,emp.HireDate.Date,l.StartDate.Date }.Max();
                    var end = new[] { last, companyToday, emp.TerminationDate?.Date ?? last,l.EndDate.Date }.Min();
                    var days = WorkingDayCalculator.Count(start,end,restDays,holidayDates);
                    if (!l.IsPaid || l.LeaveType == LeaveType.Unpaid) unpaidLeaveDays += days;
                }
            }

            var absenceDays = empDays.Count(d => d.Status is AttendanceStatus.Absent or AttendanceStatus.UnexcusedAbsence) / 1m;

            var lateMinutes = empDays
                .Where(d => d.Status is not (AttendanceStatus.RestDay or AttendanceStatus.Holiday
                            or AttendanceStatus.PaidLeave or AttendanceStatus.UnpaidLeave or AttendanceStatus.Leave))
                .Sum(d => d.LateMinutes);

            var rawOvertimeMinutes = empDays.Sum(d => d.RawOvertimeMinutes);
            var dayApprovedMinutes = empDays.Sum(d => d.ApprovedOvertimeMinutes);

            int approvedOvertimeMinutes;
            if (dayApprovedMinutes > 0)
            {
                approvedOvertimeMinutes = dayApprovedMinutes;
            }
            else if (requireApproval && approvalHoursByEmp.TryGetValue(emp.Id, out var hours))
            {
                approvedOvertimeMinutes = (int)Math.Round(hours * 60m, MidpointRounding.AwayFromZero);
            }
            else if (!requireApproval)
            {
                approvedOvertimeMinutes = rawOvertimeMinutes;
            }
            else
            {
                approvedOvertimeMinutes = 0;
            }

            // التقريب يُطبَّق مرة واحدة على إجمالي الشهر (وليس على كل بصمة)
            var paidOvertimeMinutes = policy.RoundedOvertimeEnabled
                ? approvedOvertimeMinutes
                : policy.RoundOvertimeMinutes(approvedOvertimeMinutes);

            var empLoans = loans.Where(l => l.EmployeeId == emp.Id).ToList();
            var empDeductions = deductions.Where(d => d.EmployeeId == emp.Id).ToList();
            var empAdjustments = adjustments.Where(a => a.EmployeeId == emp.Id).ToList();
            var empMidAdvances = midAdvances.Where(a => a.EmployeeId == emp.Id).ToList();
            var empExpenses = expenses.Where(e => e.EmployeeId == emp.Id).ToList();
            // القرض المجدول يُخصم عبر قسطه الشهري فقط (لا كامل المبلغ) — يُستبعد من empLoans
            // ويُحتسب عبر empRepayments. الاستبعاد يعتمد على قائمة القروض المجدولة نفسها
            // (وليس على صفوف الأقساط المُنشأة هذا الشهر) حتى لا يتحول القرض المجدول
            // إلى خصم دفعة واحدة عندما يكون قسط الشهر متخطى بقرار معتمد.
            var scheduledIds = scheduledLoans.Where(l => l.EmployeeId == emp.Id).Select(l => l.Id).ToHashSet();
            var empOneShotLoans = empLoans.Where(l => !scheduledIds.Contains(l.Id)).ToList();
            var empRepayments = repayments.Where(r => r.EmployeeId == emp.Id).ToList();
            var loanInstallments = empRepayments.Sum(r => r.DueAmount);
            var advanceRecoveries = empMidAdvances.Sum(a => a.Status == SalaryAdvanceStatus.Registered ? a.RegisteredAmount ?? 0m : a.PaidAmount);
            var expenseRecoveries = empExpenses.Sum(e => e.Amount);

            var bonuses = empAdjustments.Where(a => a.Type == AdjustmentType.Bonus).Sum(a => a.Amount);
            var allowances = empAdjustments.Where(a => a.Type == AdjustmentType.Allowance).Sum(a => a.Amount);
            var otherDeductions = empAdjustments.Where(a => a.Type == AdjustmentType.OtherDeduction).Sum(a => a.Amount);
            var additionalDays = empAdjustments.Where(a => a.Type == AdjustmentType.AdditionalDays).Sum(a => a.Days ?? 0m);
            var additionalRate = empAdjustments
                .Where(a => a.Type == AdjustmentType.AdditionalDays && a.DailyRateOverride is > 0m)
                .Select(a => a.DailyRateOverride)
                .FirstOrDefault();

            // معامل الإضافي: سياسة الشركة = 1 (بدون مضاعفة). الأفضلية لمعامل التسويات إن وُجد.
            var multiplier = policy.DefaultOvertimeMultiplier <= 0m ? 1m : policy.DefaultOvertimeMultiplier;

            var entitlement = BasicSalaryEntitlement.Calculate(emp.BasicSalary,emp.HireDate,emp.TerminationDate,new PayrollPeriod(first,last));
            var inputs = new PayrollInputs
            {
                BasicSalary = entitlement.BasicSalary,
                AllowNegativeNetSalary = true,
                DailyRateOverride = policy.DailyRateFor(emp.BasicSalary,emp.DailyRate),
                StandardWorkHours = emp.StandardWorkHours,
                LateMinutes = lateMinutes,
                EarlyLeaveMinutes = empDays.Where(d => d.IsPresent && !d.NeedsReview).Sum(d => d.EarlyLeaveMinutes),
                RawOvertimeMinutes = rawOvertimeMinutes,
                ApprovedOvertimeMinutes = approvedOvertimeMinutes,
                PaidOvertimeMinutes = paidOvertimeMinutes,
                HasExplicitPaidOvertimeMinutes = true,
                AttendanceDurations = PayrollDurationCalculator.FromDaily(empDays),
                ApprovedOvertimeDurations = dayApprovedMinutes > 0
                    ? empDays.Where(d => d.ApprovedOvertimeMinutes > 0).Select(d => new PayrollOvertimeDuration(d.WorkDate, d.ApprovedOvertimeMinutes)).ToList()
                    : !requireApproval ? empDays.Where(d => d.RawOvertimeMinutes > 0).Select(d => new PayrollOvertimeDuration(d.WorkDate, d.RawOvertimeMinutes)).ToList()
                    : Array.Empty<PayrollOvertimeDuration>(),
                AttendanceDays = attendanceDays,
                PaidRestDays = paidRestDays,
                PaidLeaveDays = paidLeaveDays,
                UnpaidLeaveDays = unpaidLeaveDays,
                AbsenceDays = absenceDays,
                // الصيغة المعتمدة: صافي = المستحق + الإضافات − استرداد السلف − استرداد المصاريف − أقساط القروض − ...
                // LoansTotal يجمع استرداد السلف (لمرة واحدة + منتصف الشهر) وأقساط القروض المجدولة؛
                // OtherDeductions يجمع التسويات الأخرى + استرداد المصاريف القابلة للخصم.
                Advances = PayrollCalculator.Round2(empOneShotLoans.Sum(l => l.Amount) + advanceRecoveries + loanInstallments),
                DeductionsTotal = empDeductions.Sum(d => d.Amount),
                OtherDeductions = PayrollCalculator.Round2(otherDeductions + expenseRecoveries),
                Bonuses = bonuses,
                Allowances = allowances,
                AdditionalDays = additionalDays,
                AdditionalDaysRate = additionalRate,
                OvertimeMultiplier = multiplier
            };

            // إعادة تطبيق التعديلات اليدوية السابقة (إن وُجدت) على مدخلات الحساب
            previousManual.TryGetValue(emp.Id, out var carried);
            if (carried is not null)
            {
                var carriedOvertimeMinutes = carried.ManualOvertimeHours.HasValue
                    ? (int)Math.Round(carried.ManualOvertimeHours.Value * 60m, MidpointRounding.AwayFromZero)
                    : inputs.ApprovedOvertimeMinutes;

                inputs = new PayrollInputs
                {
                    BasicSalary = inputs.BasicSalary,
                    AllowNegativeNetSalary = inputs.AllowNegativeNetSalary,
                    DailyRateOverride = inputs.DailyRateOverride,
                    StandardWorkHours = inputs.StandardWorkHours,
                    LateMinutes = inputs.LateMinutes,
                    EarlyLeaveMinutes = inputs.EarlyLeaveMinutes,
                    RawOvertimeMinutes = inputs.RawOvertimeMinutes,
                    ApprovedOvertimeMinutes = carriedOvertimeMinutes,
                    PaidOvertimeMinutes = carried.ManualOvertimeHours.HasValue
                        ? carriedOvertimeMinutes
                        : inputs.PaidOvertimeMinutes,
                    HasExplicitPaidOvertimeMinutes = true,
                    AttendanceDurations = inputs.AttendanceDurations,
                    ApprovedOvertimeDurations = carried.ManualOvertimeHours.HasValue ? Array.Empty<PayrollOvertimeDuration>() : inputs.ApprovedOvertimeDurations,
                    AttendanceDays = inputs.AttendanceDays,
                    PaidRestDays = inputs.PaidRestDays,
                    PaidLeaveDays = inputs.PaidLeaveDays,
                    UnpaidLeaveDays = carried.UnpaidLeaveDays,
                    AbsenceDays = carried.AbsenceDays,
                    Advances = carried.SystemFinancialDeductions.HasValue ? Math.Max(0, carried.LoansTotal - carried.SystemFinancialDeductions.Value) + inputs.Advances : Math.Max(carried.LoansTotal, inputs.Advances),
                    DeductionsTotal = inputs.DeductionsTotal,
                    OtherDeductions = carried.EmployeeExpenseRecoveries.HasValue
                        ? Math.Max(0, carried.OtherDeductions - carried.EmployeeExpenseRecoveries.Value) + expenseRecoveries
                        : Math.Max(carried.OtherDeductions, inputs.OtherDeductions),
                    Bonuses = carried.Bonuses,
                    Allowances = carried.Allowances,
                    AdditionalDays = carried.AdditionalDaysCount,
                    AdditionalDaysRate = carried.AdditionalDaysRate,
                    OvertimeMultiplier = inputs.OvertimeMultiplier
                };
            }

            var computation = PayrollCalculator.Compute(inputs, policy);
            var earnedBasic = EarnedSalaryCalculator.Calculate(emp.BasicSalary, computation.DailyRate,
                coverage.WorkedDays, policy.MonthlySalaryIncludesPaidRestDays ? coverage.PaidRestDays : 0, coverage.PaidLeaveDays);
            // Absence/unpaid days are already excluded from earned basic: never subtract them twice.
            var earnedGross = PayrollCalculator.Round2(earnedBasic + computation.OvertimeAmount + computation.Bonuses
                + computation.Allowances + computation.AdditionalDaysAmount);
            var earnedDeductions = PayrollCalculator.Round2(computation.TotalDeductions - computation.AbsenceDeduction - computation.UnpaidLeaveDeduction
                - empDeductions.Where(d => d.DeductionDate.Date > coverage.AsOf).Sum(d => d.Amount)
                - empExpenses.Where(e => e.ExpenseDate.Date > coverage.AsOf).Sum(e => e.Amount));

            var item = new PayrollItem
            {
                PayrollRunId = run.Id,
                EmployeeId = emp.Id,
                EmployeeCode = emp.EmployeeCode,
                EmployeeName = emp.FullName,
                DepartmentName = emp.Department,
                JobTitleName = emp.JobTitle,
                BasicSalary = computation.BasicSalary,
                DailyRate = computation.DailyRate,
                HourlyRate = computation.HourlyRate,
                MinuteRate = computation.MinuteRate,
                DailyRateBasisDays = computation.DailyRateBasisDays,
                WorkHoursPerDay = computation.WorkHoursPerDay,
                AttendanceDays = computation.AttendanceDays,
                PaidRestDays = computation.PaidRestDays,
                PaidLeaveDays = computation.PaidLeaveDays,
                UnpaidLeaveDays = computation.UnpaidLeaveDays,
                AbsenceDays = computation.AbsenceDays,
                LateMinutes = computation.LateMinutes,
                LateDeduction = computation.LateDeduction,
                EarlyLeaveMinutes = computation.EarlyLeaveMinutes,
                EarlyLeaveDeduction = computation.EarlyLeaveDeduction,
                ShortageDeduction = computation.ShortageDeduction,
                RawOvertimeMinutes = computation.RawOvertimeMinutes,
                RawOvertimeHours = computation.RawOvertimeHours,
                ApprovedOvertimeHours = computation.ApprovedOvertimeHours,
                PaidOvertimeHours = computation.PaidOvertimeHours,
                OvertimeMultiplier = computation.OvertimeMultiplier,
                OvertimeAmount = computation.OvertimeAmount,
                AbsenceDeduction = computation.AbsenceDeduction,
                UnpaidLeaveDeduction = computation.UnpaidLeaveDeduction,
                LoansTotal = computation.Advances,
                SalaryAdvancesPaid = advanceRecoveries, LoanInstallments = loanInstallments,
                LegacyLoanDeductions = empOneShotLoans.Sum(l => l.Amount), EmployeeExpenseRecoveries = expenseRecoveries,
                DeductionsTotal = computation.DeductionsTotal,
                OtherDeductions = computation.OtherDeductions,
                Bonuses = computation.Bonuses,
                Allowances = computation.Allowances,
                AdditionalDaysCount = computation.AdditionalDaysCount,
                AdditionalDaysAmount = computation.AdditionalDaysAmount,
                GrossEarnings = computation.GrossEarnings,
                TotalDeductions = computation.TotalDeductions,
                GrossSalary = computation.GrossSalary,
                NetSalary = computation.NetSalary,
                UncoveredDeductions = Math.Max(0,computation.TotalDeductions-computation.GrossEarnings),
                ContractBasicSalary = emp.BasicSalary, PayableBasicDays = entitlement.PayableDays,
                EarningsAsOf = coverage.AsOf, EarnedBasicSalary = earnedBasic, EarnedGrossSalary = earnedGross,
                EarnedDeductions = earnedDeductions, EarnedNetSalary = PayrollCalculator.Round2(earnedGross - earnedDeductions),
                AttendanceMissingDays = coverage.MissingDays,
                FinancialReviewReason = null,
                OriginalAbsenceDays = absenceDays,
                AnnualLeaveDays = carried?.AnnualLeaveDays ?? 0m,
                AnnualLeaveBalanceApplied = carried?.AnnualLeaveBalanceApplied ?? 0m,
                ManualOvertimeHours = carried?.ManualOvertimeHours,
                IsManuallyAdjusted = carried is not null,
                LastEditedBy = carried?.LastEditedBy,
                LastEditedAt = carried?.LastEditedAt,
                Status = PayrollStatus.Computed,
                CalculationDetails = computation.CalculationDetails,
                Notes = $"أيام راحة مدفوعة: {paidRestDays} | عطلات رسمية مدفوعة: {paidHolidayDays} (مشمولة في الراتب الشهري)" +
                    $" | استرداد سلف: {advanceRecoveries:N2} | استرداد مصاريف: {expenseRecoveries:N2} | أقساط قروض: {loanInstallments:N2} (EGP)",
                CreatedAt = lockedAt,
                CreatedBy = user
            };

            _db.PayrollItems.Add(item);
            await _db.SaveChangesAsync(ct);

            // ربط المكوّنات لمنع إعادة استخدامها في كشف آخر
            foreach (var l in empOneShotLoans) l.PayrollItemId = item.Id;
            foreach (var d in empDeductions) d.PayrollItemId = item.Id;
            foreach (var a in empAdjustments) a.PayrollItemId = item.Id;
            // Period approvals are either an alternative to daily approval or a reference
            // to the same approved time. Linking both prevents reuse without adding them twice.
            foreach (var approval in approvedApprovals.Where(o => o.EmployeeId == emp.Id)) approval.PayrollItemId = item.Id;
            foreach (var sa in empMidAdvances)
            {
                sa.PayrollItemId = item.Id;
                if (sa.Status != SalaryAdvanceStatus.Registered) sa.Status = SalaryAdvanceStatus.Settled;
            }
            foreach (var e in empExpenses) e.PayrollItemId = item.Id;
            // الأقساط المحصلة: تحديث حالة القسط ورصيد القرض (لا تتجاوز الرصيد المتبقي أبدًا).
            foreach (var rp in empRepayments)
            {
                var due = Math.Min(rp.DueAmount, rp.Loan?.OutstandingBalance ?? rp.DueAmount);
                rp.DueAmount = PayrollCalculator.Round2(due);
                rp.PaidAmount = rp.DueAmount;
                rp.Status = LoanRepaymentStatus.Collected;
                rp.PayrollItemId = item.Id;
                if (rp.Loan is not null)
                {
                    rp.Loan.PaidAmount = PayrollCalculator.Round2(rp.Loan.PaidAmount + rp.PaidAmount);
                    rp.Loan.OutstandingBalance = PayrollCalculator.Round2(
                        Math.Max(0m, rp.Loan.Amount - rp.Loan.PaidAmount));
                }
            }

            // ربط الإجازات التي دخلت في الاحتساب
            if (empDays.Count > 0)
            {
                var usedLeaves = empDays.Where(d => d.LeaveRequestId.HasValue).Select(d => d.LeaveRequestId!.Value).ToHashSet();
                foreach (var l in empLeaves.Where(l => usedLeaves.Contains(l.Id) && l.EndDate.Date <= last && l.PayrollItemId == null)) l.PayrollItemId = item.Id;
            }
            else
            {
                foreach (var l in empLeaves.Where(l=>l.EndDate.Date<=last && l.PayrollItemId==null)) l.PayrollItemId = item.Id;
            }

            if (item.NetSalary < 0) result.Warnings.Add($"{emp.EmployeeCode}: صافي سالب {item.NetSalary:N2}؛ فرق الخصومات {item.UncoveredDeductions:N2} محفوظ دون ترحيل تلقائي.");
            if (coverage.MissingDays > 0 && result.Warnings.Count < 10) result.Warnings.Add($"{emp.EmployeeCode}: بيانات {coverage.MissingDays} يوم غير مكتملة حتى {coverage.AsOf:yyyy-MM-dd}. الصافي المعروض توقع للدورة؛ المستحق الفعلي منفصل ولا يمكن الإقفال قبل استكمال البيانات.");
            result.ItemCount++;
        }

        await _db.SaveChangesAsync(ct);

        run.FinancialEvidenceHash = await PayrollFinancialEvidence.CalculateAsync(_db, run, ct);
        run.TotalNet = _db.PayrollItems.Local.Where(i => i.PayrollRunId == run.Id).Sum(i => i.NetSalary);
        run.TotalGross = _db.PayrollItems.Local.Where(i => i.PayrollRunId == run.Id).Sum(i => i.GrossEarnings);
        run.TotalOvertime = _db.PayrollItems.Local.Where(i => i.PayrollRunId == run.Id).Sum(i => i.OvertimeAmount);
        run.TotalDeductions = _db.PayrollItems.Local.Where(i => i.PayrollRunId == run.Id).Sum(i => i.TotalDeductions);
        run.TotalBonuses = _db.PayrollItems.Local.Where(i => i.PayrollRunId == run.Id).Sum(i => i.Bonuses + i.Allowances);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("توليد كشف رواتب", nameof(PayrollRun), run.Id.ToString(),
            null, $"{year}-{month:00} إصدار {run.Version}: {result.ItemCount} موظف، صافي {run.TotalNet}",
            user, userId, ct);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        result.PayrollRunId = run.Id;
        result.TotalNet = run.TotalNet;
        result.TotalGross = run.TotalGross;
        result.TotalDeductions = run.TotalDeductions;
        result.Version = run.Version;

        _logger.LogInformation("تم توليد كشف الرواتب {Year}-{Month} إصدار {Version}", year, month, run.Version);
        return result;
    }

    /// <summary>
    /// يضمن وجود صف استحقاق واحد لكل (قرض مجدول × شهر) قبل الاحتساب.
    /// القسط الشهري = InstallmentAmount إن ضُبط، وإلا Amount / Installments، وبحد أقصى الرصيد المتبقي.
    /// الصفوف المتخطاة بقرار معتمد لا تُعاد ولا تُخصم.
    /// </summary>
    private async Task<List<LoanRepayment>> EnsureRepaymentRowsAsync(List<Loan> scheduledLoans,
        int year, int month, string? user, CancellationToken ct)
    {
        var result = new List<LoanRepayment>();
        var loanIds = scheduledLoans.Select(l => l.Id).ToList();
        var existingRepayments = await _db.LoanRepayments
            .Where(r => loanIds.Contains(r.LoanId) && r.Year == year && r.Month == month)
            .ToDictionaryAsync(r => r.LoanId, ct);
        foreach (var loan in scheduledLoans)
        {
            // القرض يبدأ السداد من شهر بدايته ولا يتجاوزه بأكثر من عدد الأقساط.
            var startIndex = loan.RepaymentStartYear * 12 + loan.RepaymentStartMonth;
            var currentIndex = year * 12 + month;
            if (currentIndex < startIndex) continue;
            if (loan.Installments > 0 && currentIndex >= startIndex + loan.Installments) continue;
            if (loan.OutstandingBalance <= 0) continue;

            existingRepayments.TryGetValue(loan.Id, out var existing);
            if (existing is not null)
            {
                if (existing.Status == LoanRepaymentStatus.Due)
                    result.Add(existing);
                // المتخطى/المحصل سابقًا لا يُعاد احتسابه.
                continue;
            }

            var baseDue = loan.InstallmentAmount > 0m
                ? loan.InstallmentAmount
                : (loan.Installments > 0 ? loan.Amount / loan.Installments : loan.Amount);
            var due = PayrollCalculator.Round2(Math.Min(baseDue, loan.OutstandingBalance));
            // Settle only the final rounding cent, never collect skipped installments implicitly.
            if (loan.InstallmentAmount <= 0m && loan.Installments > 0
                && currentIndex == startIndex + loan.Installments - 1
                && loan.OutstandingBalance <= due + 0.01m)
                due = PayrollCalculator.Round2(loan.OutstandingBalance);
            if (due <= 0) continue;

            var row = new LoanRepayment
            {
                LoanId = loan.Id,
                EmployeeId = loan.EmployeeId,
                Year = year,
                Month = month,
                DueAmount = due,
                Status = LoanRepaymentStatus.Due,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = user
            };
            _db.LoanRepayments.Add(row);
            result.Add(row);
        }
        if (result.Any(r => r.Id == 0))
            await _db.SaveChangesAsync(ct);
        // حمّل علاقة القرض للأقساط الجديدة حتى يُحدَّث الرصيد عند الربط.
        foreach (var r in result.Where(r => r.Loan is null))
            r.Loan = scheduledLoans.FirstOrDefault(l => l.Id == r.LoanId);
        return result;
    }

    /// <summary>يفصل ارتباط المكوّنات بأسطر كشف (يُستخدم عند الأرشفة/الإلغاء/إعادة الفتح).</summary>
    private async Task UnlinkAsync(List<int> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0) return;
        foreach (var l in await _db.Loans.Where(x => x.PayrollItemId != null && itemIds.Contains(x.PayrollItemId.Value)).ToListAsync(ct))
            l.PayrollItemId = null;
        // فك ارتباط سلف منتصف الشهر وإعادتها لحالة مدفوعة (قابلة لإعادة الاحتساب).
        foreach (var sa in await _db.SalaryAdvances.Where(x => x.PayrollItemId != null && itemIds.Contains(x.PayrollItemId.Value)).ToListAsync(ct))
        {
            sa.PayrollItemId = null;
            if (sa.Status == SalaryAdvanceStatus.Settled) sa.Status = SalaryAdvanceStatus.Paid;
        }
        // فك ارتباط المصاريف المخصومة (تبقى معتمدة وقابلة لإعادة الاحتساب).
        foreach (var e in await _db.EmployeeExpenses.Where(x => x.PayrollItemId != null && itemIds.Contains(x.PayrollItemId.Value)).ToListAsync(ct))
            e.PayrollItemId = null;
        // فك ارتباط الأقساط المحصلة وإرجاع المبالغ لرصيد القرض.
        var collected = await _db.LoanRepayments.Include(r => r.Loan)
            .Where(x => x.PayrollItemId != null && itemIds.Contains(x.PayrollItemId.Value)
                        && x.Status == LoanRepaymentStatus.Collected)
            .ToListAsync(ct);
        foreach (var rp in collected)
        {
            if (rp.Loan is not null)
            {
                rp.Loan.PaidAmount = PayrollCalculator.Round2(Math.Max(0m, rp.Loan.PaidAmount - rp.PaidAmount));
                rp.Loan.OutstandingBalance = PayrollCalculator.Round2(Math.Max(0m, rp.Loan.Amount - rp.Loan.PaidAmount));
            }
            rp.PaidAmount = 0m;
            rp.Status = LoanRepaymentStatus.Due;
            rp.PayrollItemId = null;
        }
        foreach (var d in await _db.Deductions.Where(x => x.PayrollItemId != null && itemIds.Contains(x.PayrollItemId.Value)).ToListAsync(ct))
            d.PayrollItemId = null;
        foreach (var o in await _db.OvertimeApprovals.Where(x => x.PayrollItemId != null && itemIds.Contains(x.PayrollItemId.Value)).ToListAsync(ct))
            o.PayrollItemId = null;
        foreach (var a in await _db.PayrollAdjustments.Where(x => x.PayrollItemId != null && itemIds.Contains(x.PayrollItemId.Value)).ToListAsync(ct))
            a.PayrollItemId = null;
        foreach (var lv in await _db.LeaveRequests.Where(x => x.PayrollItemId != null && itemIds.Contains(x.PayrollItemId.Value)).ToListAsync(ct))
            lv.PayrollItemId = null;
    }

    private async Task<PayrollRun> GetRunAsync(int id, CancellationToken ct)
        => await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id, ct)
           ?? throw new InvalidOperationException("كشف الرواتب غير موجود");

    private async Task ValidateFinalizationAsync(PayrollRun run, CancellationToken ct)
    {
        var period = PayrollPeriod.ForRun(run);
        if (await CompanyPayrollClock.TodayAsync(_db, ct, _clock) < period.End)
            throw new InvalidOperationException($"الدورة لم تنته بعد ({period.End:yyyy-MM-dd}). القيم الحالية توقعات وليست راتباً نهائياً.");
        var items = await _db.PayrollItems.Where(i => i.PayrollRunId == run.Id).ToListAsync(ct);
        if (items.Count == 0) throw new InvalidOperationException("لا توجد أسطر رواتب؛ احسب الكشف أولاً.");
        var problems = items.Where(i => i.FinancialReviewReason != null || i.AttendanceMissingDays > 0
            || i.AttendanceMissingDays == null)
            .Select(i => $"{i.EmployeeCode}: {i.FinancialReviewReason ?? (i.AttendanceMissingDays.HasValue ? $"استكمل حضور {i.AttendanceMissingDays} يوم أو سجّل الغياب/الإجازة الصحيحة ثم أعد الحساب" : "أعد حساب الكشف لتوثيق اكتمال بيانات الحضور")}").Take(10).ToList();
        if (problems.Count > 0) throw new InvalidOperationException("تعذر إقفال الرواتب: " + string.Join(" | ", problems));
        if (await _db.LeaveRequests.AnyAsync(l => l.Status == ApprovalStatus.Pending && l.StartDate <= period.End && l.EndDate >= period.Start, ct))
            throw new InvalidOperationException("احسم طلبات الإجازة المعلقة داخل الدورة ثم أعد حساب الرواتب قبل الإقفال.");
        if (await _db.PayrollAdjustments.AnyAsync(a => a.Status == ApprovalStatus.Pending && a.Year == run.Year && a.Month == run.Month, ct))
            throw new InvalidOperationException("توجد تسويات مالية معلقة؛ احسمها ثم أعد حساب الرواتب قبل الإقفال.");
        await PayrollFinancialEvidence.EnsureCurrentAsync(_db, run, ct);
    }

    /// <summary>Single authorized close action, with technical validation and an audit trail.</summary>
    public async Task FinalizeAsync(int payrollRunId, string? user, string? userId, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var run = await GetRunAsync(payrollRunId, ct);
        if (run.Status == PayrollStatus.Locked) return;
        if (run.Status == PayrollStatus.Cancelled) throw new InvalidOperationException("الكشف ملغى؛ احسب كشفاً جديداً قبل الإقفال.");
        await ValidateFinalizationAsync(run, ct);
        var previous = run.Status.ToString();
        run.Status = PayrollStatus.Locked;
        run.ApprovedAt = run.LockedAt = DateTime.UtcNow;
        run.ApprovedBy = run.LockedBy = user;
        foreach (var item in await _db.PayrollItems.Where(i => i.PayrollRunId == run.Id).ToListAsync(ct)) item.Status = PayrollStatus.Locked;
        await _audit.LogAsync("إقفال كشف الرواتب", nameof(PayrollRun), run.Id.ToString(), previous, "Locked", user, userId, ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>نقل الكشف إلى حالة "قيد المراجعة".</summary>
    public async Task MarkPendingReviewAsync(int payrollRunId, string? user, string? userId, CancellationToken ct = default)
    {
        var run = await GetRunAsync(payrollRunId, ct);
        if (run.IsLocked) throw new InvalidOperationException("الكشف مقفل ولا يمكن تغيير حالته.");
        run.Status = PayrollStatus.PendingReview;
        await _audit.LogAsync("إرسال كشف الرواتب للمراجعة", nameof(PayrollRun), run.Id.ToString(), null, "PendingReview", user, userId, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>اعتماد الكشف (يبقى قابلًا للقفل).</summary>
    public async Task ApproveAsync(int payrollRunId, string? user, string? userId, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var run = await GetRunAsync(payrollRunId, ct);
        if (run.PeriodEnd.HasValue && await CompanyPayrollClock.TodayAsync(_db,ct,_clock) < run.PeriodEnd.Value.Date)
            throw new InvalidOperationException("لا يمكن إغلاق أو اعتماد دورة رواتب قبل تاريخ انتهائها.");
        if (run.Status == PayrollStatus.Approved) return;
        if (run.Status is PayrollStatus.Locked or PayrollStatus.Cancelled)
            throw new InvalidOperationException("لا يمكن اعتماد كشف مقفل أو ملغى. استخدم سير العمل المعتمد لإعادة الفتح أو الاحتساب.");

        if (await _db.PayrollItems.AnyAsync(i => i.PayrollRunId == payrollRunId && i.FinancialReviewReason != null,ct))
            throw new InvalidOperationException("لا يمكن اعتماد الكشف قبل حل مراجعة الأجر الجزئي أو فرق الخصومات الزائدة. لا يُرحّل الفرق تلقائياً.");
        await PayrollFinancialEvidence.EnsureCurrentAsync(_db,run,ct);
        await ValidateFinalizationAsync(run, ct);
        run.Status = PayrollStatus.Approved;
        run.ApprovedAt = DateTime.UtcNow;
        run.ApprovedBy = user;

        foreach (var i in await _db.PayrollItems.Where(i => i.PayrollRunId == payrollRunId).ToListAsync(ct))
            i.Status = PayrollStatus.Approved;

        await _audit.LogAsync("اعتماد كشف رواتب", nameof(PayrollRun), run.Id.ToString(), "Computed", "Approved", user, userId, ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>قفل الكشف نهائيًا (يمنع أي إعادة احتساب تلقائية).</summary>
    public async Task LockAsync(int payrollRunId, string? user, string? userId, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var run = await GetRunAsync(payrollRunId, ct);
        if (run.PeriodEnd.HasValue && await CompanyPayrollClock.TodayAsync(_db,ct,_clock) < run.PeriodEnd.Value.Date)
            throw new InvalidOperationException("لا يمكن إغلاق أو اعتماد دورة رواتب قبل تاريخ انتهائها.");
        if (run.Status == PayrollStatus.Locked) return;
        if (run.Status != PayrollStatus.Approved)
            throw new InvalidOperationException("لا يمكن قفل الكشف قبل اعتماده.");

        await ValidateFinalizationAsync(run, ct);

        if (await _db.PayrollItems.AnyAsync(i => i.PayrollRunId == payrollRunId && i.FinancialReviewReason != null,ct))
            throw new InvalidOperationException("الكشف يحتوي فروقاً مالية تحتاج حلاً معتمداً قبل القفل.");
        run.Status = PayrollStatus.Locked;
        run.LockedAt = DateTime.UtcNow;
        run.LockedBy = user;

        foreach (var i in await _db.PayrollItems.Where(i => i.PayrollRunId == payrollRunId).ToListAsync(ct))
            i.Status = PayrollStatus.Locked;

        await _audit.LogAsync("قفل كشف رواتب", nameof(PayrollRun), run.Id.ToString(), "Approved", "Locked", user, userId, ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>إعادة فتح كشف مقفل بسبب مُسجَّل (تصحيح معتمد فقط).</summary>
    public async Task ReopenAsync(int payrollRunId, string reason, string? user, string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("سبب إعادة الفتح مطلوب.");

        var run = await GetRunAsync(payrollRunId, ct);
        if (run.Status is not (PayrollStatus.Approved or PayrollStatus.Locked))
            throw new InvalidOperationException("إعادة الفتح متاحة فقط للكشوف المعتمدة أو المقفلة.");

        var previous = run.Status.ToString();
        run.Status = PayrollStatus.Reopened;
        run.ReopenedAt = DateTime.UtcNow;
        run.ReopenedBy = user;
        run.ReopenReason = reason;

        await _audit.LogAsync("إعادة فتح كشف رواتب", nameof(PayrollRun), run.Id.ToString(), previous,
            $"Reopened: {reason}", user, userId, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>إلغاء الكشف مع فصل المكوّنات لإتاحة إعادة الاحتساب.</summary>
    public async Task CancelAsync(int payrollRunId, string? user, string? userId, CancellationToken ct = default)
    {
        var run = await GetRunAsync(payrollRunId, ct);
        if (run.IsLocked)
            throw new InvalidOperationException("لا يمكن إلغاء كشف معتمد أو مقفل. أعد فتحه أولًا بسبب مُسجَّل.");

        var itemIds = await _db.PayrollItems.Where(i => i.PayrollRunId == payrollRunId).Select(i => i.Id).ToListAsync(ct);
        await UnlinkAsync(itemIds, ct);

        // إعادة رصيد الإجازة السنوية الذي خُصم من هذا الكشف حتى لا يبقى مخصومًا بلا مقابل
        var appliedItems = await _db.PayrollItems
            .Where(i => i.PayrollRunId == payrollRunId && i.AnnualLeaveBalanceApplied > 0)
            .ToListAsync(ct);
        foreach (var ai in appliedItems)
        {
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == ai.EmployeeId, ct);
            if (emp is not null)
            {
                emp.AnnualLeaveBalance = PayrollCalculator.Round2(emp.AnnualLeaveBalance + ai.AnnualLeaveBalanceApplied);
                emp.UpdatedAt = DateTime.UtcNow;
                emp.UpdatedBy = user;
            }
            ai.AnnualLeaveBalanceApplied = 0m;
            ai.AnnualLeaveDays = 0m;
        }

        var previous = run.Status.ToString();
        run.Status = PayrollStatus.Cancelled;
        foreach (var i in await _db.PayrollItems.Where(i => i.PayrollRunId == payrollRunId).ToListAsync(ct))
            i.Status = PayrollStatus.Cancelled;

        await _audit.LogAsync("إلغاء كشف رواتب", nameof(PayrollRun), run.Id.ToString(), previous, "Cancelled", user, userId, ct);
        await _db.SaveChangesAsync(ct);
    }
}
