using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using System.ComponentModel.DataAnnotations.Schema;

namespace HrPayroll.Domain.Entities;

/// <summary>
/// سطر راتب موظف داخل كشف شهري.
/// يحتوي لقطة كاملة من الأجر والسياسة المستخدمة حتى يبقى الكشف التاريخي قابلًا للتفسير
/// حتى لو تغيّر راتب الموظف أو قسمه لاحقًا.
/// </summary>
public class PayrollItem : BaseEntity
{
    public int PayrollRunId { get; set; }
    public PayrollRun? PayrollRun { get; set; }

    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    // ===== لقطة من بيانات الموظف وقت الحساب =====
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
    public string? JobTitleName { get; set; }

    // ===== لقطة سياسة الأجر المستخدمة =====
    public decimal BasicSalary { get; set; }
    public decimal DailyRate { get; set; }
    public decimal HourlyRate { get; set; }
    public decimal MinuteRate { get; set; }
    public int DailyRateBasisDays { get; set; } = 30;
    public int WorkHoursPerDay { get; set; } = 8;

    // ===== أيام الحضور =====
    public int AttendanceDays { get; set; }
    public int PaidRestDays { get; set; }
    public decimal PaidLeaveDays { get; set; }
    public decimal UnpaidLeaveDays { get; set; }

    /// <summary>الغياب المعتمد المستخدم في الخصم (قابل للتعديل يدويًا من كشف المرتبات).</summary>
    public decimal AbsenceDays { get; set; }

    /// <summary>
    /// الغياب الأصلي المستخرج من البصمة كما كان لحظة الاحتساب (لا يُستبدل بالتعديل اليدوي أبدًا).
    /// يُحفظ للرجوع إليه ومقارنته بالغياب المعتمد.
    /// </summary>
    public decimal OriginalAbsenceDays { get; set; }

    /// <summary>أيام الإجازة السنوية المستخدمة في هذا الكشف (تُخصم من رصيد الموظف ولا تُخصم كغياب).</summary>
    public decimal AnnualLeaveDays { get; set; }

    /// <summary>
    /// مقدار ما تم خصمه فعليًا من رصيد الإجازة السنوية لهذا السطر.
    /// يُستخدم لحساب الفرق (Delta) عند إعادة التعديل أو إعادة الاحتساب لمنع الخصم المزدوج.
    /// </summary>
    public decimal AnnualLeaveBalanceApplied { get; set; }

    public int LateMinutes { get; set; }
    public int? EarlyLeaveMinutes { get; set; }

    // ===== الإضافي =====
    public int RawOvertimeMinutes { get; set; }
    public decimal RawOvertimeHours { get; set; }
    public decimal ApprovedOvertimeHours { get; set; }
    public decimal PaidOvertimeHours { get; set; }
    public decimal OvertimeMultiplier { get; set; } = 1m;
    public decimal OvertimeAmount { get; set; }

    /// <summary>ساعات الإضافي المعتمدة يدويًا من كشف المرتبات (تتجاوز القيمة المستخرجة من البصمة).</summary>
    public decimal? ManualOvertimeHours { get; set; }

    // ===== الخصومات =====
    public decimal LateDeduction { get; set; }
    public decimal? EarlyLeaveDeduction { get; set; }
    public decimal? ShortageDeduction { get; set; }
    public decimal AbsenceDeduction { get; set; }
    public decimal UnpaidLeaveDeduction { get; set; }

    /// <summary>السلف (اسم تاريخي محفوظ للتوافق).</summary>
    public decimal LoansTotal { get; set; }
    public decimal? SalaryAdvancesPaid { get; set; }
    public decimal? LoanInstallments { get; set; }
    public decimal? LegacyLoanDeductions { get; set; }
    public decimal? EmployeeExpenseRecoveries { get; set; }
    public decimal? SystemFinancialDeductions => SalaryAdvancesPaid.HasValue
        ? SalaryAdvancesPaid + (LoanInstallments ?? 0) + (LegacyLoanDeductions ?? 0) : null;

    public decimal DeductionsTotal { get; set; }

    /// <summary>خصومات أخرى معتمدة من التسويات.</summary>
    public decimal OtherDeductions { get; set; }

    // ===== الإضافات =====
    public decimal Bonuses { get; set; }
    public decimal Allowances { get; set; }
    public decimal AdditionalDaysCount { get; set; }

    /// <summary>سعر اليوم المستخدم في الأيام الإضافية (فارغ = الأجر اليومي للموظف).</summary>
    public decimal? AdditionalDaysRate { get; set; }

    public decimal AdditionalDaysAmount { get; set; }

    // ===== الإجماليات =====
    public decimal GrossEarnings { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal NetSalary { get; set; }
    public decimal UncoveredDeductions { get; set; }
    public decimal? ContractBasicSalary { get; set; }
    public decimal? PayableBasicDays { get; set; }
    public string? FinancialReviewReason { get; set; }
    public DateTime? EarningsAsOf { get; set; }
    public decimal? EarnedBasicSalary { get; set; }
    public decimal? EarnedGrossSalary { get; set; }
    public decimal? EarnedDeductions { get; set; }
    public decimal? EarnedNetSalary { get; set; }
    public int? AttendanceMissingDays { get; set; }

    /// <summary>الإجمالي قبل الخصومات (يحفظ للتوافق: الراتب + الإضافي + الإضافات).</summary>
    public decimal GrossSalary { get; set; }

    public PayrollStatus Status { get; set; } = PayrollStatus.Computed;
    public string? Notes { get; set; }

    // ===== أثر التعديل اليدوي (مراجعة وتدقيق) =====
    /// <summary>هل عُدِّل هذا السطر يدويًا من كشف المرتبات؟</summary>
    public bool IsManuallyAdjusted { get; set; }

    public DateTime? LastEditedAt { get; set; }
    public string? LastEditedBy { get; set; }

    /// <summary>تفاصيل الحساب خطوة بخطوة (JSON) لأغراض المراجعة والتدقيق.</summary>
    public string? CalculationDetails { get; set; }

    [NotMapped] public decimal ActualLateHours => PayrollDurationSnapshot.Read(this).ActualLateMinutes / 60m;
    [NotMapped] public decimal? ActualEarlyDepartureHours => PayrollDurationSnapshot.Read(this).ActualEarlyDepartureMinutes / 60m;
    [NotMapped] public decimal? ActualMissingHours => PayrollDurationSnapshot.Read(this).ActualMissingMinutes / 60m;
    [NotMapped] public decimal? RoundedDurationHours => PayrollDurationSnapshot.Read(this).RoundedDurationHours;
    [NotMapped] public decimal ChargeableHours => PayrollDurationSnapshot.Read(this).ChargeableHours;
    [NotMapped] public string AttendanceDeductionBasisText => PayrollDurationSnapshot.Read(this).BasisText;
    [NotMapped] public string DurationPolicyDescription => PayrollDurationSnapshot.Read(this).Description;
    [NotMapped] public decimal AttendanceDeductions => LateDeduction + (EarlyLeaveDeduction ?? 0m) + (ShortageDeduction ?? 0m);
    [NotMapped] public bool IsProvisional => Status is not (PayrollStatus.Approved or PayrollStatus.Locked);

    public ICollection<Loan> Loans { get; set; } = new List<Loan>();
    public ICollection<Deduction> Deductions { get; set; } = new List<Deduction>();
    public ICollection<OvertimeApproval> OvertimeApprovals { get; set; } = new List<OvertimeApproval>();
    public ICollection<PayrollAdjustment> Adjustments { get; set; } = new List<PayrollAdjustment>();
    public ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();
}
