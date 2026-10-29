using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HrPayroll.Infrastructure.Services;

/// <summary>
/// سلفة منتصف الشهر (سلفة يوم 15):
/// حساب الاستحقاق الفعلي من بداية الشهر حتى تاريخ الاستحقاق باستخدام
/// محرك حساب الرواتب نفسه (<see cref="PayrollCalculator"/>)، ثم مراجعة واعتماد ودفع وتسوية.
/// </summary>
public class SalaryAdvanceService
{
    /// <summary>
    /// آخر يوم مسموح لاستحقاق سلفة منتصف الشهر. يوم 15 نفسه مسموح،
    /// وأي تاريخ استحقاق بعد 15 مرفوض (سياسة الشركة المعتمدة).
    /// </summary>
    public const int CutoffDay = 15;

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly ILogger<SalaryAdvanceService> _logger;

    public SalaryAdvanceService(AppDbContext db, SettingsService settings, AuditService audit,
        ILogger<SalaryAdvanceService> logger)
    {
        _db = db;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// يحسب الاستحقاق الفعلي لموظف من أول الشهر حتى تاريخ الاستحقاق
    /// باستخدام نفس قواعد محرك الرواتب (يومي/ساعة/تأخير/غياب/إضافي).
    /// </summary>
    public async Task<SalaryAdvance> CalculateAsync(int employeeId, int year, int month, DateTime cutoff,
        string? user = null, string? userId = null, CancellationToken ct = default)
    {
        // سياسة الأهلية: السلفة تُحتسب من أول الشهر حتى يوم 15 (يوم 15 مسموح).
        // نرفض أي تاريخ خارج شهر السلفة أو بعد يوم 15 قبل أي قراءة/كتابة في قاعدة البيانات.
        if (cutoff.Year != year || cutoff.Month != month)
            throw new InvalidOperationException(
                $"تاريخ الاستحقاق ({cutoff:yyyy-MM-dd}) يجب أن يكون داخل شهر السلفة {year}-{month:00}.");
        if (cutoff.Day > CutoffDay)
            throw new InvalidOperationException(
                $"أهلية سلفة منتصف الشهر تنتهي يوم {CutoffDay} من الشهر؛ " +
                $"لا يمكن حساب سلفة بتاريخ استحقاق {cutoff:dd/MM/yyyy}.");

        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct)
            ?? throw new InvalidOperationException("الموظف غير موجود.");

        // منع التكرار: سلفة واحدة نشطة لكل (موظف × فترة).
        var existing = await _db.SalaryAdvances.FirstOrDefaultAsync(
            a => a.EmployeeId == employeeId && a.Year == year && a.Month == month
                 && a.Status != SalaryAdvanceStatus.Cancelled, ct);
        if (existing is not null)
            throw new InvalidOperationException("توجد سلفة مسجلة لهذا الموظف في نفس الشهر. راجعها أو ألغها أولًا.");

        var policy = await _settings.GetPolicyAsync(ct);
        var first = new DateTime(year, month, 1);
        var end = cutoff.Date;

        var days = await _db.DailyAttendances.AsNoTracking()
            .Where(d => d.EmployeeId == employeeId && d.WorkDate >= first && d.WorkDate <= end)
            .ToListAsync(ct);

        var attendanceDays = days.Count(d => d.CheckInTime.HasValue);
        var absenceDays = days.Count(d => d.Status is AttendanceStatus.Absent or AttendanceStatus.UnexcusedAbsence) / 1m;
        var unpaidLeaveDays = days.Count(d => d.Status == AttendanceStatus.UnpaidLeave) / 1m;
        var paidLeaveDays = days.Count(d => d.Status == AttendanceStatus.PaidLeave) / 1m;
        var lateMinutes = days
            .Where(d => d.Status is not (AttendanceStatus.RestDay or AttendanceStatus.Holiday
                or AttendanceStatus.PaidLeave or AttendanceStatus.UnpaidLeave or AttendanceStatus.Leave))
            .Sum(d => d.LateMinutes);
        var rawOvertimeMinutes = days.Sum(d => d.RawOvertimeMinutes);
        var approvedOvertimeMinutes = days.Sum(d => d.ApprovedOvertimeMinutes);

        var approvedAdjustments = await _db.PayrollAdjustments.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.Year == year && a.Month == month
                        && a.Status == ApprovalStatus.Approved && a.PayrollItemId == null)
            .ToListAsync(ct);
        var bonuses = approvedAdjustments.Where(a => a.Type == AdjustmentType.Bonus).Sum(a => a.Amount);
        var allowances = approvedAdjustments.Where(a => a.Type == AdjustmentType.Allowance).Sum(a => a.Amount);

        var inputs = new PayrollInputs
        {
            BasicSalary = emp.BasicSalary,
            DailyRateOverride = emp.DailyRate,
            StandardWorkHours = emp.StandardWorkHours,
            LateMinutes = lateMinutes,
            RawOvertimeMinutes = rawOvertimeMinutes,
            ApprovedOvertimeMinutes = approvedOvertimeMinutes,
            PaidOvertimeMinutes = approvedOvertimeMinutes,
            AttendanceDays = attendanceDays,
            PaidLeaveDays = paidLeaveDays,
            UnpaidLeaveDays = unpaidLeaveDays,
            AbsenceDays = absenceDays,
            Bonuses = bonuses,
            Allowances = allowances,
            OvertimeMultiplier = policy.DefaultOvertimeMultiplier <= 0m ? 1m : policy.DefaultOvertimeMultiplier
        };
        var computation = PayrollCalculator.Compute(inputs, policy);

        // المصاريف السابقة المشمولة ضمن السلفة (لا تُخصم مرة أخرى لاحقًا).
        var includedExpenses = await _db.EmployeeExpenses.AsNoTracking()
            .Where(e => e.EmployeeId == employeeId && e.Year == year && e.Month == month
                        && e.Status == ApprovalStatus.Approved
                        && e.SettlementMode == ExpenseSettlementMode.IncludedInAdvance)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        // المقترح = صافي الاستحقاق حتى الاستحقاق (بحد أقصى الراتب الأساسي)،
        // مع خصم المصاريف المشمولة حتى لا تُدفع مرتين.
        var earned = computation.NetSalary;
        var proposed = PayrollCalculator.Round2(Math.Max(0m, Math.Min(emp.BasicSalary, earned) - includedExpenses));

        var advance = new SalaryAdvance
        {
            EmployeeId = employeeId,
            Year = year,
            Month = month,
            CutoffDate = end,
            BasicSalary = emp.BasicSalary,
            EarnedAmount = earned,
            AttendanceDays = attendanceDays,
            WorkingHours = PayrollCalculator.Round2(attendanceDays * (emp.StandardWorkHours <= 0 ? 8m : emp.StandardWorkHours)),
            LateMinutes = lateMinutes,
            LateDeduction = computation.LateDeduction,
            AbsenceDays = absenceDays,
            AbsenceDeduction = computation.AbsenceDeduction,
            OvertimeAmount = computation.OvertimeAmount,
            Bonuses = computation.Bonuses + computation.Allowances,
            IncludedExpenses = includedExpenses,
            ProposedAmount = proposed,
            ApprovedAmount = proposed,
            Status = SalaryAdvanceStatus.Calculated,
            Notes = $"حُسب آليًا من {attendanceDays} يوم حضور حتى {end:yyyy-MM-dd} (صافي الاستحقاق {earned:N2} EGP).",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = user
        };

        _db.SalaryAdvances.Add(advance);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("حساب سلفة منتصف الشهر", nameof(SalaryAdvance), advance.Id.ToString(),
            null, $"{emp.EmployeeCode}: مقترح {proposed:N2}", user, userId, ct);

        _logger.LogInformation("سلفة يوم 15 محسوبة للموظف {EmployeeId} عن {Year}-{Month}", employeeId, year, month);
        return advance;
    }

    /// <summary>مراجعة/تعديل المبلغ المقترح من مستخدم مخوَّل مع توثيق السبب.</summary>
    public async Task ReviewAsync(int id, decimal approvedAmount, string? reason,
        string? user = null, string? userId = null, CancellationToken ct = default)
    {
        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        if (advance.Status is SalaryAdvanceStatus.Paid or SalaryAdvanceStatus.Settled)
            throw new InvalidOperationException("لا يمكن تعديل سلفة مدفوعة أو مُسوَّاة.");
        if (approvedAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(approvedAmount), "المبلغ المعتمد يجب أن يكون ≥ صفر.");
        if (approvedAmount != advance.ProposedAmount && string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("سبب التعديل مطلوب عند اختلاف المبلغ المعتمد عن المقترح.");

        var old = $"{advance.ApprovedAmount:N2}/{advance.Status}";
        advance.ApprovedAmount = PayrollCalculator.Round2(approvedAmount);
        advance.AdjustmentReason = reason;
        advance.Status = SalaryAdvanceStatus.UnderReview;
        advance.UpdatedAt = DateTime.UtcNow;
        advance.UpdatedBy = user;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("مراجعة سلفة منتصف الشهر", nameof(SalaryAdvance), advance.Id.ToString(),
            old, $"{advance.ApprovedAmount:N2}/{advance.Status}: {reason}", user, userId, ct);
    }

    public async Task ApproveAsync(int id, string? user = null, string? userId = null, CancellationToken ct = default)
    {
        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        if (advance.Status is SalaryAdvanceStatus.Paid or SalaryAdvanceStatus.Settled)
            throw new InvalidOperationException("لا يمكن اعتماد سلفة مدفوعة أو مُسوَّاة.");
        var old = advance.Status.ToString();
        advance.Status = SalaryAdvanceStatus.Approved;
        advance.ApprovedBy = user;
        advance.ApprovedAt = DateTime.UtcNow;
        advance.UpdatedAt = DateTime.UtcNow;
        advance.UpdatedBy = user;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("اعتماد سلفة منتصف الشهر", nameof(SalaryAdvance), advance.Id.ToString(),
            old, advance.Status.ToString(), user, userId, ct);
    }

    public async Task MarkPaidAsync(int id, decimal paidAmount, string? user = null, string? userId = null,
        CancellationToken ct = default)
    {
        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        if (advance.Status != SalaryAdvanceStatus.Approved)
            throw new InvalidOperationException("الدفع متاح فقط للسلف المعتمدة.");
        if (paidAmount <= 0 || paidAmount > advance.ApprovedAmount)
            throw new ArgumentOutOfRangeException(nameof(paidAmount), "المبلغ المدفوع يجب أن يكون بين 0 والمعتمد.");

        advance.PaidAmount = PayrollCalculator.Round2(paidAmount);
        advance.Status = SalaryAdvanceStatus.Paid;
        advance.PaidBy = user;
        advance.PaidAt = DateTime.UtcNow;
        advance.UpdatedAt = DateTime.UtcNow;
        advance.UpdatedBy = user;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("دفع سلفة منتصف الشهر", nameof(SalaryAdvance), advance.Id.ToString(),
            advance.ApprovedAmount.ToString("N2"), advance.PaidAmount.ToString("N2"), user, userId, ct);
    }

    public async Task CancelAsync(int id, string reason, string? user = null, string? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("سبب الإلغاء مطلوب.");
        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        if (advance.Status == SalaryAdvanceStatus.Paid)
            throw new InvalidOperationException(
                "لا يمكن إلغاء سلفة مدفوعة. سجّل استردادًا أو تسوية معتمدة بدل إلغاء عملية صُرف فيها مبلغ فعلي.");
        if (advance.IsSettled)
            throw new InvalidOperationException("لا يمكن إلغاء سلفة مُسوَّاة في كشف رواتب. استخدم دورة التصحيح المعتمدة.");
        var old = advance.Status.ToString();
        advance.Status = SalaryAdvanceStatus.Cancelled;
        advance.Notes = (advance.Notes ?? string.Empty + " ") + $"[إلغاء: {reason}]";
        advance.UpdatedAt = DateTime.UtcNow;
        advance.UpdatedBy = user;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("إلغاء سلفة منتصف الشهر", nameof(SalaryAdvance), advance.Id.ToString(),
            old, reason, user, userId, ct);
    }
}
