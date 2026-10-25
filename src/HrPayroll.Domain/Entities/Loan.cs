using HrPayroll.Domain.Enums;

namespace HrPayroll.Domain.Entities;

/// <summary>سلفة موظف.</summary>
public class Loan : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public decimal Amount { get; set; }
    public DateTime LoanDate { get; set; }

    /// <summary>الشهر الذي تُخصم فيه السلفة.</summary>
    public int DeductionMonth { get; set; }
    public int DeductionYear { get; set; }

    public int Installments { get; set; } = 1;
    public string? Notes { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    // ===== تتبع السداد (حقول مضافة — اختيارية للحفاظ على توافق البيانات القديمة) =====
    /// <summary>القسط الشهري المتفق عليه (0 = يُحسب تلقائيًا Amount / Installments).</summary>
    public decimal InstallmentAmount { get; set; }

    /// <summary>إجمالي ما سُدد فعليًا عبر الأقساط المحصلة.</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>الرصيد المتبقي = Amount − PaidAmount (يُحسب عند الحفظ ولا يُدخل يدويًا).</summary>
    public decimal OutstandingBalance { get; set; }

    /// <summary>شهر بدء السداد (افتراضيًا شهر القرض).</summary>
    public int RepaymentStartMonth { get; set; }
    public int RepaymentStartYear { get; set; }

    /// <summary>إيقاف مؤقت للسداد بقرار معتمد (لا تُحصَّل أقساط جديدة حتى الاستئناف).</summary>
    public bool IsPaused { get; set; }

    public ICollection<LoanRepayment> Repayments { get; set; } = new List<LoanRepayment>();

    /// <summary>كشف الرواتب الذي خُصمت فيه هذه السلفة (لمنع الخصم المزدوج).</summary>
    public int? PayrollItemId { get; set; }
    public PayrollItem? PayrollItem { get; set; }
}
