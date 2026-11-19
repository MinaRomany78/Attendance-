using System.Data;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HrPayroll.Infrastructure.Services;

/// <summary>Earned-to-date salary advances, payment audit and preserved legacy lifecycle operations.</summary>
public partial class SalaryAdvanceService
{
    private readonly TimeProvider _clock;
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly ILogger<SalaryAdvanceService> _logger;

    public SalaryAdvanceService(AppDbContext db, SettingsService settings, AuditService audit,
        ILogger<SalaryAdvanceService> logger, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _db = db;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<SalaryAdvance> CalculateAsync(int employeeId, int year, int month, DateTime cutoff,
        string? user = null, string? userId = null, CancellationToken ct = default)
    {
        ValidatePeriod(year, month, cutoff);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await FinancialPeriodGuard.EnsureEditableAsync(_db, PayrollPeriod.ForMonth(year,month).Start, PayrollPeriod.ForMonth(year,month).End, ct);
        var balance = await GetBalanceAsync(employeeId, cutoff, ct);
        var advance = Snapshot(balance, cutoff, user);
        _db.SalaryAdvances.Add(advance);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("حساب سلفة موظف", nameof(SalaryAdvance), advance.Id.ToString(),
            null, balance.Explanation, user, userId, ct);
        await tx.CommitAsync(ct);
        return advance;
    }
    /// <summary>مراجعة/تعديل المبلغ المقترح من مستخدم مخوَّل مع توثيق السبب.</summary>
    public async Task ReviewAsync(int id, decimal approvedAmount, string? reason,
        string? user = null, string? userId = null, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        await EnsureMutableAsync(advance, ct);
        if (advance.CycleKey != null) throw new InvalidOperationException("استخدم كشف السلف لإعداد أو تأكيد هذه السلفة.");
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
        await _audit.LogAndSaveAsync("مراجعة سلفة موظف", nameof(SalaryAdvance), advance.Id.ToString(),
            old, $"{advance.ApprovedAmount:N2}/{advance.Status}: {reason}", user, userId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ApproveAsync(int id, string? user = null, string? userId = null, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        await EnsureMutableAsync(advance, ct);
        if (advance.Status == SalaryAdvanceStatus.Approved) return;
        var old = advance.Status.ToString();
        advance.Status = SalaryAdvanceStatus.Approved;
        advance.ApprovedBy = user;
        advance.ApprovedAt = DateTime.UtcNow;
        advance.UpdatedAt = DateTime.UtcNow;
        advance.UpdatedBy = user;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("اعتماد سلفة موظف", nameof(SalaryAdvance), advance.Id.ToString(),
            old, advance.Status.ToString(), user, userId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task MarkPaidAsync(int id, decimal paidAmount, string? user = null, string? userId = null,
        CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        if (advance.Status != SalaryAdvanceStatus.Approved)
            throw new InvalidOperationException("الدفع متاح فقط للسلف المعتمدة.");
        await EnsureMutableAsync(advance, ct);
        var roundedPaidAmount = PayrollCalculator.Round2(paidAmount);
        if (roundedPaidAmount <= 0 || roundedPaidAmount > advance.ApprovedAmount)
            throw new ArgumentOutOfRangeException(nameof(paidAmount), "المبلغ المدفوع يجب أن يكون بين 0 والمعتمد.");

        await EnsurePaymentWindowAsync(advance.Year, advance.Month, ct);
        ValidateAmount(paidAmount);
        advance.PaidAmount = roundedPaidAmount;
        advance.Status = SalaryAdvanceStatus.Paid;
        advance.PaidBy = user;
        advance.PaidAt = _clock.GetUtcNow().UtcDateTime;
        advance.UpdatedAt = DateTime.UtcNow;
        advance.UpdatedBy = user;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("دفع سلفة موظف", nameof(SalaryAdvance), advance.Id.ToString(),
            advance.ApprovedAmount.ToString("N2"), advance.PaidAmount.ToString("N2"), user, userId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task CancelAsync(int id, string reason, string? user = null, string? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("سبب الإلغاء مطلوب.");
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        if (advance.Status == SalaryAdvanceStatus.Paid || advance.PaidAmount > 0m)
            throw new InvalidOperationException(
                "لا يمكن إلغاء سلفة مدفوعة. سجّل استردادًا أو تسوية معتمدة بدل إلغاء عملية صُرف فيها مبلغ فعلي.");
        if (advance.IsSettled)
            throw new InvalidOperationException("لا يمكن إلغاء سلفة مُسوَّاة في كشف رواتب. استخدم دورة التصحيح المعتمدة.");
        await FinancialPeriodGuard.EnsureEditableAsync(_db, PayrollPeriod.StoredOrLegacy(advance.PeriodStart,advance.PeriodEnd,advance.Year,advance.Month).Start, PayrollPeriod.StoredOrLegacy(advance.PeriodStart,advance.PeriodEnd,advance.Year,advance.Month).End, ct);
        if (advance.Status == SalaryAdvanceStatus.Cancelled) return;
        var old = advance.Status.ToString();
        advance.Status = SalaryAdvanceStatus.Cancelled;
        advance.Revision++;
        advance.Notes = (advance.Notes ?? string.Empty) + $" [إلغاء: {reason}]";
        advance.UpdatedAt = DateTime.UtcNow;
        advance.UpdatedBy = user;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("إلغاء سلفة موظف", nameof(SalaryAdvance), advance.Id.ToString(),
            old, reason, user, userId, ct);
        await tx.CommitAsync(ct);
    }

    private async Task EnsureMutableAsync(SalaryAdvance advance, CancellationToken ct)
    {
        if (advance.Status is SalaryAdvanceStatus.Paid or SalaryAdvanceStatus.Settled or SalaryAdvanceStatus.Cancelled
            || advance.PaidAmount > 0m || advance.IsSettled)
            throw new InvalidOperationException("لا يمكن تعديل سلفة مدفوعة أو مسوّاة أو ملغاة. استخدم دورة التصحيح المعتمدة.");
        await FinancialPeriodGuard.EnsureEditableAsync(_db, PayrollPeriod.StoredOrLegacy(advance.PeriodStart,advance.PeriodEnd,advance.Year,advance.Month).Start, PayrollPeriod.StoredOrLegacy(advance.PeriodStart,advance.PeriodEnd,advance.Year,advance.Month).End, ct);
    }
}
