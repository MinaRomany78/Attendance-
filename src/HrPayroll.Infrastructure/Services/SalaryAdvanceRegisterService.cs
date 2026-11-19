using System.Data;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Entities;
using Microsoft.EntityFrameworkCore;
namespace HrPayroll.Infrastructure.Services;

public sealed record PreparedAdvanceEdit(int EmployeeId, decimal Amount, long Revision);
public sealed record PreparedAdvanceConfirmation(int Id, long Revision, decimal PaidAmount, Guid RequestId);
public sealed record AdvanceWindow(int StartDay, int EndDay);

public partial class SalaryAdvanceService
{
    public const string MonthlyCycle = "mid-month";
    public const string PaymentWindowStartKey = "Payroll.Advances.PaymentWindowStartDay";
    public const string PaymentWindowEndKey = "Payroll.Advances.PaymentWindowEndDay";

    public async Task<DateTime> CompanyTodayAsync(CancellationToken ct = default)
    {
        return await CompanyPayrollClock.TodayAsync(_db,ct,_clock);
    }
    public async Task<AdvanceWindow> GetPaymentWindowAsync(CancellationToken ct = default)
    {
        var start = await _settings.GetIntAsync(PaymentWindowStartKey, 15, ct);
        var end = await _settings.GetIntAsync(PaymentWindowEndKey, 15, ct);
        if (start < 1 || end > 31 || start > end) throw new InvalidOperationException("إعداد فترة صرف السلف غير صالح.");
        return new(start, end);
    }
    private async Task EnsurePaymentWindowAsync(int year, int month, CancellationToken ct)
    {
        var today = await CompanyTodayAsync(ct); var window = await GetPaymentWindowAsync(ct);
        if (!PayrollPeriod.ForMonth(year,month).Contains(today) || today.Day < window.StartDay || today.Day > window.EndDay)
            throw new InvalidOperationException($"الصرف متاح من يوم {window.StartDay} إلى {window.EndDay} داخل نفس شهر كشف السلف. التاريخ الفعلي {today:yyyy-MM-dd}.");
    }
    public async Task SetPaymentWindowAsync(int start, int end, string? user, string? userId, CancellationToken ct = default)
    {
        if (start < 1 || end > 31 || start > end) throw new InvalidOperationException("أدخل فترة صحيحة بين اليوم 1 و31.");
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var old = await GetPaymentWindowAsync(ct);
        await _settings.SetAsync(PaymentWindowStartKey, start.ToString(), user: user, ct: ct);
        await _settings.SetAsync(PaymentWindowEndKey, end.ToString(), user: user, ct: ct);
        await _audit.LogAndSaveAsync("تعديل فترة صرف السلف", "SalaryAdvanceWindow", null, $"{old.StartDay}-{old.EndDay}", $"{start}-{end}", user, userId, ct);
        await tx.CommitAsync(ct);
    }
    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0 || amount > 9999999999999999.99m || amount != Math.Round(amount, 2))
            throw new InvalidOperationException("أدخل مبلغاً موجباً صالحاً بمنزلتين عشريتين كحد أقصى.");
    }

    /// <summary>Save is the final registration action; never records a physical cash payment.</summary>
    public async Task<IReadOnlyList<SalaryAdvance>> RegisterAsync(int year, int month,
        IReadOnlyList<PreparedAdvanceEdit> edits, string? user, string? userId, CancellationToken ct = default)
    {
        var period = PayrollPeriod.ForMonth(year, month);
        if (edits.Count is 0 or > 1000 || edits.Select(e => e.EmployeeId).Distinct().Count() != edits.Count)
            throw new InvalidOperationException("حدد من 1 إلى 1000 موظف لحفظ السلف.");
        foreach (var edit in edits)
            if (edit.Amount < 0 || edit.Amount > 9999999999999999.99m || edit.Amount != Math.Round(edit.Amount, 2))
                throw new InvalidOperationException("أدخل مبلغاً صالحاً غير سالب بمنزلتين عشريتين كحد أقصى.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await FinancialPeriodGuard.EnsureEditableAsync(_db, period.Start, period.End, ct);
        var today = await CompanyTodayAsync(ct);
        if (PayrollPeriod.FromDate(today).Start != period.Start)
            throw new InvalidOperationException("تسجيل السلف متاح للدورة الحالية؛ الدورات الأخرى متاحة للاطلاع التاريخي.");
        var ids = edits.Select(e => e.EmployeeId).ToList();
        var records = await _db.SalaryAdvances.Where(a => ids.Contains(a.EmployeeId) && a.Year == year && a.Month == month).ToListAsync(ct);
        var balances = await GetBalancesAsync(ids, today, ct, period);
        var saved = new List<SalaryAdvance>();
        foreach (var edit in edits.OrderBy(e => e.EmployeeId))
        {
            var balance = balances.Single(b => b.Employee.Id == edit.EmployeeId);
            if (!balance.Employee.IsActive) throw new InvalidOperationException("الموظف غير نشط.");
            var row = records.SingleOrDefault(a => a.EmployeeId == edit.EmployeeId && a.CycleKey == MonthlyCycle);
            if (row is null)
            {
                if (edit.Revision != 0) throw new InvalidOperationException("تغير سجل السلفة. حدّث الصفحة قبل الحفظ.");
                // An older paid transaction is preserved as its own accounting evidence. Unmapped
                // historical records need explicit cycle reconciliation before another deduction.
                if (records.Any(a => a.EmployeeId == edit.EmployeeId && a.Status != SalaryAdvanceStatus.Cancelled
                    && (a.PeriodStart != period.Start || a.PeriodEnd != period.End)))
                    throw new InvalidOperationException("توجد سلفة تاريخية غير مرتبطة بهذه الدورة؛ راجع ربطها قبل إضافة خصم جديد.");
                row = Snapshot(balance, today, user);
                row.Year = year; row.Month = month; row.CycleKey = MonthlyCycle;
                row.PeriodStart = period.Start; row.PeriodEnd = period.End;
                row.PreparedAt = _clock.GetUtcNow().UtcDateTime; row.PreparedBy = user;
                _db.SalaryAdvances.Add(row);
            }
            else
            {
                if (row.PeriodStart != period.Start || row.PeriodEnd != period.End)
                    throw new InvalidOperationException("السلفة مسجلة بدورة تاريخية مختلفة؛ يلزم ربط معتمد قبل تعديلها.");
                if (row.PaidAmount > 0 || row.Status is SalaryAdvanceStatus.Paid or SalaryAdvanceStatus.Settled)
                    throw new InvalidOperationException("هذا السجل يوثق صرفاً نقدياً سابقاً؛ لا يمكن استبداله أو محوه بالحفظ المباشر.");
                if (row.Revision != edit.Revision)
                {
                    // Idempotent retry after a successful response was lost is safe.
                    if ((row.Status == SalaryAdvanceStatus.Registered && row.RegisteredAmount == edit.Amount)
                        || (row.Status == SalaryAdvanceStatus.Cancelled && edit.Amount == 0))
                    { saved.Add(row); continue; }
                    throw new InvalidOperationException("تم تعديل السلفة من مستخدم آخر. حدّث الصفحة قبل الحفظ.");
                }
            }

            var before = row.RegisteredAmount;
            if ((row.Status == SalaryAdvanceStatus.Registered && before == edit.Amount)
                || (row.Status == SalaryAdvanceStatus.Cancelled && edit.Amount == 0))
            { saved.Add(row); continue; }
            row.RegisteredAmount = edit.Amount;
            row.PreparedAmount = edit.Amount;
            row.ApprovedAmount = edit.Amount; // Preserved legacy export field, not a separate approval.
            row.ProposedAmount = balance.RegistrationCapacity;
            row.EarnedAmount = balance.Earned; row.AttendanceDays = balance.WorkedDays;
            row.CutoffDate = today;
            row.Status = edit.Amount == 0 ? SalaryAdvanceStatus.Cancelled : SalaryAdvanceStatus.Registered;
            row.Revision++; row.UpdatedAt = _clock.GetUtcNow().UtcDateTime; row.UpdatedBy = user;
            // PaidAmount, PaidAt, PaidBy and PaymentRequestId deliberately remain untouched.
            await _db.SaveChangesAsync(ct);
            await _audit.LogAndSaveAsync(edit.Amount == 0 ? "إلغاء خصم سلفة" : "تسجيل خصم سلفة", nameof(SalaryAdvance), row.Id.ToString(),
                before?.ToString(), $"خصم مسجل {edit.Amount}; الدورة {period.Start:yyyy-MM-dd} — {period.End:yyyy-MM-dd}; {balance.Explanation}"
                + (edit.Amount > balance.RegistrationCapacity ? " قيمة السلفة أكبر من المستحق الحالي للموظف." : ""), user, userId, ct);
            saved.Add(row);
        }
        await tx.CommitAsync(ct);
        return saved;
    }

    /// <summary>Explicit audited assignment of an unlinked historical cash advance; never invents payment evidence.</summary>
    public async Task MapLegacyPeriodAsync(int id, int year, int month, string reason,
        string? user, string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10 || reason.Length > 1000)
            throw new InvalidOperationException("اكتب سبب ربط السلفة التاريخية ومصدر التحقق (من 10 إلى 1000 حرف).");
        var target = PayrollPeriod.ForMonth(year, month);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var row = await _db.SalaryAdvances.SingleOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new InvalidOperationException("السلفة غير موجودة.");
        if (row.PayrollItemId.HasValue || row.Status != SalaryAdvanceStatus.Paid || row.PaidAmount <= 0)
            throw new InvalidOperationException("الربط متاح لسلفة نقدية مصروفة وغير مرتبطة بكشف فقط. عالج الكشف التاريخي عبر الإلغاء أو إعادة الفتح المصرح بها أولاً.");
        if (row.PeriodStart == target.Start && row.PeriodEnd == target.End && row.Year == year && row.Month == month)
            return;
        if (row.PeriodStart.HasValue || row.PeriodEnd.HasValue)
            throw new InvalidOperationException("هذه السلفة مرتبطة بالفعل بدورة محددة. لا يمكن إعادة تعيين تاريخها تلقائياً.");
        var original = PayrollPeriod.StoredOrLegacy(row.PeriodStart, row.PeriodEnd, row.Year, row.Month);
        await FinancialPeriodGuard.EnsureEditableAsync(_db, original.Start, original.End, ct);
        await FinancialPeriodGuard.EnsureEditableAsync(_db, target.Start, target.End, ct);
        if (await _db.SalaryAdvances.AnyAsync(a => a.Id != row.Id && a.EmployeeId == row.EmployeeId
            && a.Year == year && a.Month == month
            && (a.Status == SalaryAdvanceStatus.Registered && a.RegisteredAmount > 0
                || row.CycleKey != null && a.CycleKey == row.CycleKey), ct))
            throw new InvalidOperationException("توجد سلفة مسجلة أو خانة مستخدمة لنفس الموظف والدورة. سوّ المبالغ المتداخلة أولاً لمنع الخصم المزدوج.");
        var before = $"دورة قديمة {row.Year}-{row.Month:00}; نطاق {original.Start:yyyy-MM-dd}/{original.End:yyyy-MM-dd}; مصروف فعلياً {row.PaidAmount}";
        row.Year = year; row.Month = month; row.PeriodStart = target.Start; row.PeriodEnd = target.End;
        row.Revision++; row.UpdatedAt = _clock.GetUtcNow().UtcDateTime; row.UpdatedBy = user;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAndSaveAsync("ربط سلفة تاريخية بدورة الرواتب", nameof(SalaryAdvance), row.Id.ToString(), before,
            $"دورة {target.Display}; مصروف فعلياً {row.PaidAmount}; مصدر التحقق: {reason.Trim()}", user, userId, ct);
        await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<SalaryAdvance>> PrepareAsync(int year, int month,
        IReadOnlyList<PreparedAdvanceEdit> edits, string? user, string? userId, CancellationToken ct = default)
    {
        var period = PayrollPeriod.ForMonth(year,month);
        if (edits.Count is 0 or > 1000 || edits.Select(e => e.EmployeeId).Distinct().Count() != edits.Count)
            throw new InvalidOperationException("حدد من 1 إلى 1000 موظف بمبالغ صالحة لحفظ الكشف.");
        foreach (var edit in edits) ValidateAmount(edit.Amount);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await FinancialPeriodGuard.EnsureEditableAsync(_db, period.Start, period.End, ct);
        var ids = edits.Select(e => e.EmployeeId).ToList();
        var records = await _db.SalaryAdvances.Where(a => ids.Contains(a.EmployeeId) && a.Year == year && a.Month == month).ToListAsync(ct);
        var today = await CompanyTodayAsync(ct);
        var snapshotDate = today < period.Start ? period.Start : today < period.End ? today : period.End;
        var balances = await GetBalancesAsync(ids, snapshotDate, ct,period);
        var saved = new List<SalaryAdvance>();
        foreach (var edit in edits.OrderBy(e => e.EmployeeId))
        {
            var balance = balances.Single(b => b.Employee.Id == edit.EmployeeId);
            if (!balance.Employee.IsActive) throw new InvalidOperationException("الموظف غير نشط.");
            var row = records.SingleOrDefault(a => a.EmployeeId == edit.EmployeeId && a.CycleKey == MonthlyCycle);
            if (row == null)
            {
                if (edit.Revision != 0 || records.Any(a => a.EmployeeId == edit.EmployeeId && (a.PaidAmount > 0 || a.Status != SalaryAdvanceStatus.Cancelled)))
                    throw new InvalidOperationException("يوجد سجل سابق لنفس الموظف والدورة. راجع السجل قبل إنشاء سلفة أخرى.");
                row = Snapshot(balance, snapshotDate, user); row.Year = year; row.Month = month; row.CycleKey = MonthlyCycle;
                row.PeriodStart = period.Start; row.PeriodEnd = period.End;
                row.PreparedAt = _clock.GetUtcNow().UtcDateTime; row.PreparedBy = user; _db.SalaryAdvances.Add(row);
            }
            else if (row.PeriodStart != period.Start || row.PeriodEnd != period.End)
                throw new InvalidOperationException("السلفة مسجلة بدورة تاريخية مختلفة؛ يلزم ربط معتمد قبل تغيير نطاقها.");
            else if (row.PaidAmount > 0 || row.IsSettled || row.Status is SalaryAdvanceStatus.Paid or SalaryAdvanceStatus.Settled)
                throw new InvalidOperationException("السلفة مصروفة؛ لا يمكن تعديل المعاملة المالية المدفوعة.");
            else if (row.Revision != edit.Revision)
            {
                if (row.Status == SalaryAdvanceStatus.Prepared && row.PreparedAmount == edit.Amount) { saved.Add(row); continue; }
                throw new InvalidOperationException("تم تعديل السلفة من مستخدم آخر. حدّث الصفحة قبل الحفظ.");
            }
            var before = row.PreparedAmount;
            row.PreparedAmount = row.ApprovedAmount = edit.Amount; row.ProposedAmount = balance.Available;
            row.EarnedAmount = balance.Earned; row.AttendanceDays = balance.WorkedDays;
            row.Status = SalaryAdvanceStatus.Prepared; row.Revision++; row.UpdatedAt = _clock.GetUtcNow().UtcDateTime; row.UpdatedBy = user;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAndSaveAsync("إعداد كشف سلف", nameof(SalaryAdvance), row.Id.ToString(), before?.ToString(),
                $"المطلوب {edit.Amount}; {balance.Explanation}" + (edit.Amount > balance.Available ? " قيمة السلفة أكبر من المستحق الحالي للموظف." : ""), user, userId, ct);
            saved.Add(row);
        }
        await tx.CommitAsync(ct); return saved;
    }
    public async Task ConfirmPreparedAsync(IReadOnlyList<PreparedAdvanceConfirmation> confirmations,
        string? user, string? userId, CancellationToken ct = default)
    {
        if (confirmations.Count is 0 or > 1000 || confirmations.Select(c => c.Id).Distinct().Count() != confirmations.Count
            || confirmations.Select(c => c.RequestId).Distinct().Count() != confirmations.Count || confirmations.Any(c => c.RequestId == Guid.Empty))
            throw new InvalidOperationException("حدد من 1 إلى 1000 سلفة معدة تم صرفها فعلياً.");
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        foreach (var c in confirmations.OrderBy(c => c.Id))
        {
            ValidateAmount(c.PaidAmount);
            var row = await _db.SalaryAdvances.SingleOrDefaultAsync(a => a.Id == c.Id, ct) ?? throw new InvalidOperationException("السلفة غير موجودة.");
            if (row.PaymentRequestId == c.RequestId && row.PaidAmount == c.PaidAmount && row.Status is SalaryAdvanceStatus.Paid or SalaryAdvanceStatus.Settled) continue;
            if (row.Status != SalaryAdvanceStatus.Prepared || row.CycleKey != MonthlyCycle || row.PaidAmount > 0 || row.IsSettled)
                throw new InvalidOperationException("السلفة غير معدة للصرف أو تم صرفها سابقاً.");
            if (row.Revision != c.Revision || row.PreparedAmount != c.PaidAmount)
                throw new InvalidOperationException("تغير المبلغ المعد. حدّث الصفحة قبل تأكيد الصرف. يجب تأكيد كامل المبلغ المعد.");
            var period = PayrollPeriod.ForMonth(row.Year,row.Month);
            if (row.PeriodStart != period.Start || row.PeriodEnd != period.End) throw new InvalidOperationException("يجب مراجعة دورة السلفة التاريخية قبل الصرف.");
            await EnsurePaymentWindowAsync(row.Year, row.Month, ct);
            await FinancialPeriodGuard.EnsureEditableAsync(_db,period.Start,period.End,ct);
            row.PaidAmount = c.PaidAmount; row.PaidAt = _clock.GetUtcNow().UtcDateTime; row.PaidBy = user;
            row.PaymentRequestId = c.RequestId; row.Status = SalaryAdvanceStatus.Paid; row.Revision++;
            row.UpdatedAt = row.PaidAt; row.UpdatedBy = user;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAndSaveAsync("تأكيد صرف سلفة معدة", nameof(SalaryAdvance), row.Id.ToString(), "غير مصروفة", $"مدفوع فعلياً {row.PaidAmount}; شهر التسوية {row.Year}-{row.Month:00}", user, userId, ct);
        }
        await tx.CommitAsync(ct);
    }
}
