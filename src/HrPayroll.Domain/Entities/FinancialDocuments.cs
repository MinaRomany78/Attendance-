using HrPayroll.Domain.Enums;

namespace HrPayroll.Domain.Entities;

/// <summary>
/// مصروف موظف: معاملة مالية مستقلة قابلة للتتبع.
/// ليس سلفة افتراضيًا: طريقة احتسابه من الراتب تُحدد صراحة عبر <see cref="SettlementMode"/>
/// أو بربطه بسلفة/كشف عبر <see cref="SalaryAdvanceId"/>.
/// </summary>
public class EmployeeExpense : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public decimal Amount { get; set; }
    public DateTime ExpenseDate { get; set; }

    /// <summary>الفئة: مواصلات، علاج، ... (نص حر).</summary>
    public string Category { get; set; } = "عام";

    public string? Description { get; set; }

    /// <summary>مرجع الإيصال أو المرفق إن وُجد.</summary>
    public string? ReceiptReference { get; set; }

    public string Currency { get; set; } = "EGP";

    /// <summary>الشهر/السنة المحاسبية التي يتبعها المصروف (للتقارير والخصم).</summary>
    public int Month { get; set; }
    public int Year { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// كيف يُسوَّى المصروف:
    /// - قابل للخصم من الراتب (DeductFromPayroll) — الافتراضي للمصاريف المعتمدة.
    /// - مشمول ضمن سلفة (IncludedInAdvance) — لا يُخصم مرة أخرى، بل يُربط بسلفة عبر SalaryAdvanceId.
    /// - مدفوع نقدًا خارج الراتب (PaidCash) — للتوثيق فقط ولا يؤثر على صافي الراتب.
    /// </summary>
    public ExpenseSettlementMode SettlementMode { get; set; } = ExpenseSettlementMode.DeductFromPayroll;

    /// <summary>سلفة مرتبطة (عندما يكون المصروف مشمولًا ضمن سلفة).</summary>
    public int? SalaryAdvanceId { get; set; }
    public SalaryAdvance? SalaryAdvance { get; set; }

    /// <summary>كشف الرواتب الذي سُوّي فيه (لمنع الخصم المزدوج).</summary>
    public int? PayrollItemId { get; set; }
    public PayrollItem? PayrollItem { get; set; }

    public bool IsSettled => PayrollItemId.HasValue;

    public string? Notes { get; set; }
}

/// <summary>طريقة تسوية مصروف الموظف.</summary>
public enum ExpenseSettlementMode
{
    /// <summary>يُخصم من صافي الراتب في شهر المصروف.</summary>
    DeductFromPayroll = 0,
    /// <summary>مشمول ضمن سلفة mid-month — لا يُخصم مرة أخرى.</summary>
    IncludedInAdvance = 1,
    /// <summary>دُفع نقدًا خارج الراتب — توثيق فقط.</summary>
    PaidCash = 2
}

/// <summary>
/// سلفة منتصف الشهر (سلفة يوم 15): تُحسب من الاستحقاق الفعلي
/// من بداية الشهر حتى تاريخ الاستحقاق، ثم تُراجع وتُعتمد وتُدفع،
/// وتُسوَّى لاحقًا مع كشف راتب نفس الشهر.
/// </summary>
public class SalaryAdvance : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public int Year { get; set; }
    public int Month { get; set; }

    /// <summary>تاريخ الاستحقاق (افتراضيًا يوم 15).</summary>
    public DateTime CutoffDate { get; set; }

    public decimal BasicSalary { get; set; }

    // ===== مدخلات الحساب (لقطة وقت الحساب) =====
    public decimal EarnedAmount { get; set; }
    public int AttendanceDays { get; set; }
    public decimal WorkingHours { get; set; }
    public int LateMinutes { get; set; }
    public decimal LateDeduction { get; set; }
    public decimal AbsenceDays { get; set; }
    public decimal AbsenceDeduction { get; set; }
    public decimal OvertimeAmount { get; set; }
    public decimal Bonuses { get; set; }

    /// <summary>مصاريف سابقة مشمولة ضمن هذه السلفة (لا تُخصم مرة أخرى).</summary>
    public decimal IncludedExpenses { get; set; }

    /// <summary>المبلغ المقترح آليًا من الحساب.</summary>
    public decimal ProposedAmount { get; set; }

    /// <summary>المبلغ المعتمد النهائي (قد يعدَّله المسؤول).</summary>
    public decimal ApprovedAmount { get; set; }

    public decimal PaidAmount { get; set; }

    public SalaryAdvanceStatus Status { get; set; } = SalaryAdvanceStatus.Calculated;

    /// <summary>سبب التعديل اليدوي (مطلوب عند اختلاف المعتمد عن المقترح).</summary>
    public string? AdjustmentReason { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? PaidBy { get; set; }
    public DateTime? PaidAt { get; set; }

    /// <summary>كشف الرواتب الذي سُوّيت فيه (لمنع التسوية المزدوجة).</summary>
    public int? PayrollItemId { get; set; }
    public PayrollItem? PayrollItem { get; set; }

    public bool IsSettled => PayrollItemId.HasValue;

    public string? Notes { get; set; }

    public ICollection<EmployeeExpense> LinkedExpenses { get; set; } = new List<EmployeeExpense>();
}

/// <summary>حالة سلفة منتصف الشهر.</summary>
public enum SalaryAdvanceStatus
{
    Calculated = 0,
    UnderReview = 1,
    Approved = 2,
    Paid = 3,
    Settled = 4,
    Cancelled = 5
}

/// <summary>
/// قسط سداد قرض: سجل واحد لكل (قرض × شهر استحقاق).
/// يمنع الخصم المزدوج ويوثّق التخطي والإيقاف والتعديلات.
/// </summary>
public class LoanRepayment : BaseEntity
{
    public int LoanId { get; set; }
    public Loan? Loan { get; set; }

    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public int Year { get; set; }
    public int Month { get; set; }

    public decimal DueAmount { get; set; }
    public decimal PaidAmount { get; set; }

    public LoanRepaymentStatus Status { get; set; } = LoanRepaymentStatus.Due;

    /// <summary>كشف الرواتب الذي خُصم فيه (لمنع الخصم المزدوج).</summary>
    public int? PayrollItemId { get; set; }
    public PayrollItem? PayrollItem { get; set; }

    public string? Notes { get; set; }
}

/// <summary>حالة قسط السداد.</summary>
public enum LoanRepaymentStatus
{
    /// <summary>مستحق ولم يُدفع بعد.</summary>
    Due = 0,
    /// <summary>مُحصَّل عبر كشف الراتب.</summary>
    Collected = 1,
    /// <summary>تُخطّي بقرار معتمد — يبقى في الرصيد ولا يُخصم.</summary>
    Skipped = 2,
    /// <summary>مُعدَّل/مُسوَّى يدويًا بقرار معتمد.</summary>
    Adjusted = 3
}
