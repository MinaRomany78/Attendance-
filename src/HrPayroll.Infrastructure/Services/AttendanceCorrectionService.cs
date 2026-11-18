using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Services;
using HrPayroll.Domain.ValueObjects;
using System.Data;
using System.Globalization;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Infrastructure.Services;

/// <summary>
/// التصحيح اليدوي لسجلات الحضور.
/// كل تصحيح يسجّل القيمة الأصلية والمصححة والسبب والمستخدم والوقت،
/// وتبقى البصمات الخام محفوظة كما وردت من الجهاز.
/// </summary>
public class AttendanceCorrectionService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public AttendanceCorrectionService(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public sealed class CorrectionRequest
    {
        public int DailyAttendanceId { get; set; }
        public DateTime? CheckIn { get; set; }
        public DateTime? CheckOut { get; set; }
        public AttendanceStatus? Status { get; set; }
        public int? ApprovedOvertimeMinutes { get; set; }
        public string Reason { get; set; } = string.Empty;
        public bool Approve { get; set; } = true;
        public bool UseAutomaticStatus { get; set; }
    }

    public async Task<DailyAttendance> CorrectAsync(
        CorrectionRequest request, string? user, string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("سبب التصحيح مطلوب.");

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var daily = await _db.DailyAttendances.Include(d => d.Shift)
                        .FirstOrDefaultAsync(d => d.Id == request.DailyAttendanceId, ct)
                    ?? throw new InvalidOperationException("سجل الحضور غير موجود");

        await EnsurePeriodNotLockedAsync(daily.WorkDate, ct);
        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
            throw new InvalidOperationException("حالة الحضور غير صالحة.");
        if (request.ApprovedOvertimeMinutes < 0) throw new InvalidOperationException("ساعات الإضافي المعتمدة لا يمكن أن تكون سالبة.");
        var checkIn = request.CheckIn ?? daily.CheckInTime;
        var checkOut = request.CheckOut ?? daily.CheckOutTime;
        if (checkIn.HasValue && checkOut.HasValue && checkOut <= checkIn)
            throw new InvalidOperationException("وقت الخروج يجب أن يكون بعد الدخول، مع مراعاة تاريخ اليوم التالي للوردية الليلية.");
        if (request.CheckIn?.Kind == DateTimeKind.Utc || request.CheckOut?.Kind == DateTimeKind.Utc)
            throw new InvalidOperationException("استخدم وقت الشركة المحلي دون تحويل UTC.");
        var policy = await new SettingsService(_db).GetPolicyAsync(ct);
        var window = daily.ShiftEnd > daily.ShiftStart ? new ShiftWindow(daily.ShiftStart, daily.ShiftEnd)
            : daily.Shift is not null ? ShiftWindowResolver.Resolve(daily.Shift, daily.WorkDate)
            : new ShiftWindow(daily.WorkDate, daily.WorkDate);
        var proposed = AttendanceCalculator.CalculateDay(window, checkIn, checkOut,
            daily.Shift?.EntryGraceMinutes ?? policy.EntryGraceMinutes, daily.Shift?.ExitGraceMinutes ?? policy.ExitGraceMinutes,
            policy.MinimumOvertimeMinutes, policy.GracePeriodMode);
        if (request.ApprovedOvertimeMinutes.HasValue && request.ApprovedOvertimeMinutes != daily.ApprovedOvertimeMinutes
            && (request.ApprovedOvertimeMinutes > proposed.RawOvertimeMinutes || proposed.HasIncompletePunches || proposed.NeedsReview))
            throw new InvalidOperationException("الإضافي المعتمد لا يتجاوز الإضافي الفعلي، ولا يُعتمد مع بصمات ناقصة أو غير صالحة.");
        var history = await _db.AttendanceCorrections.AsNoTracking().Where(c => c.DailyAttendanceId == daily.Id).ToListAsync(ct);
        var hadStatusOverride = AttendanceDayRecalculator.HasIntentionalStatus(history);
        var before = Snapshot(daily);
        var corrections = new List<AttendanceCorrection>();

        void Record(string field, string? oldValue, string? newValue)
        {
            if (oldValue == newValue) return;
            corrections.Add(new AttendanceCorrection
            {
                DailyAttendanceId = daily.Id,
                EmployeeId = daily.EmployeeId,
                WorkDate = daily.WorkDate,
                FieldName = field,
                OldValue = oldValue,
                NewValue = newValue,
                Reason = request.Reason,
                Status = request.Approve ? ApprovalStatus.Approved : ApprovalStatus.Pending,
                ApprovedBy = request.Approve ? user : null,
                ApprovedAt = request.Approve ? DateTime.UtcNow : null,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = user
            });
        }

        if (!request.Approve)
        {
            if (request.CheckIn.HasValue) Record(nameof(DailyAttendance.CheckInTime), before[nameof(DailyAttendance.CheckInTime)], request.CheckIn.Value.ToString("O", CultureInfo.InvariantCulture));
            if (request.CheckOut.HasValue) Record(nameof(DailyAttendance.CheckOutTime), before[nameof(DailyAttendance.CheckOutTime)], request.CheckOut.Value.ToString("O", CultureInfo.InvariantCulture));
            if (request.Status.HasValue) Record(nameof(DailyAttendance.Status), daily.Status.ToString(), request.Status.Value.ToString());
            if (request.ApprovedOvertimeMinutes.HasValue) Record(nameof(DailyAttendance.ApprovedOvertimeMinutes), daily.ApprovedOvertimeMinutes.ToString(CultureInfo.InvariantCulture), request.ApprovedOvertimeMinutes.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            daily.CheckInTime = checkIn;
            daily.CheckOutTime = checkOut;
            if (request.Status.HasValue) daily.Status = request.Status.Value;
            if (request.ApprovedOvertimeMinutes.HasValue) daily.ApprovedOvertimeMinutes = request.ApprovedOvertimeMinutes.Value;
            AttendanceDayRecalculator.Apply(daily, daily.Shift, policy,
                request.Status.HasValue || (hadStatusOverride && !request.UseAutomaticStatus));
            var after = Snapshot(daily);
            foreach (var (field, value) in after)
                Record(field == nameof(DailyAttendance.Status) && !request.Status.HasValue ? "DerivedStatus" : field,
                    before[field], value);
            if (request.Status.HasValue || request.UseAutomaticStatus)
                Record("StatusOverride", hadStatusOverride ? "Manual" : "Automatic", request.Status.HasValue ? "Manual" : "Automatic");
        }
        if (corrections.Count == 0) throw new InvalidOperationException("لا توجد قيمة مختلفة أو حساب مشتق يحتاج إلى التصحيح.");
        if (request.Approve)
        {
            daily.IsManualOverride = true;
            daily.AdminNotes = request.Reason;
            daily.UpdatedAt = DateTime.UtcNow;
            daily.UpdatedBy = user;
        }
        _db.AttendanceCorrections.AddRange(corrections);
        await _audit.LogAsync("تصحيح حضور", nameof(DailyAttendance), daily.Id.ToString(),
            string.Join("; ", corrections.Select(c => $"{c.FieldName}:{c.OldValue}")),
            string.Join("; ", corrections.Select(c => $"{c.FieldName}:{c.NewValue}")),
            user, userId, ct);
        await PayrollEvidenceGuard.MarkAttendanceChangesAsync(_db,user,ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return daily;
    }

    private static Dictionary<string, string?> Snapshot(DailyAttendance daily) => new()
    {
        [nameof(DailyAttendance.CheckInTime)] = daily.CheckInTime?.ToString("O", CultureInfo.InvariantCulture),
        [nameof(DailyAttendance.CheckOutTime)] = daily.CheckOutTime?.ToString("O", CultureInfo.InvariantCulture),
        [nameof(DailyAttendance.Status)] = daily.Status.ToString(),
        [nameof(DailyAttendance.WorkedMinutes)] = daily.WorkedMinutes.ToString(CultureInfo.InvariantCulture),
        [nameof(DailyAttendance.LateMinutes)] = daily.LateMinutes.ToString(CultureInfo.InvariantCulture),
        [nameof(DailyAttendance.EarlyLeaveMinutes)] = daily.EarlyLeaveMinutes.ToString(CultureInfo.InvariantCulture),
        [nameof(DailyAttendance.RawOvertimeMinutes)] = daily.RawOvertimeMinutes.ToString(CultureInfo.InvariantCulture),
        [nameof(DailyAttendance.ApprovedOvertimeMinutes)] = daily.ApprovedOvertimeMinutes.ToString(CultureInfo.InvariantCulture),
        [nameof(DailyAttendance.PaidOvertimeMinutes)] = daily.PaidOvertimeMinutes.ToString(CultureInfo.InvariantCulture),
        [nameof(DailyAttendance.HasIncompletePunches)] = daily.HasIncompletePunches.ToString(),
        [nameof(DailyAttendance.PunchReviewStatus)] = daily.PunchReviewStatus.ToString(),
        [nameof(DailyAttendance.NeedsReviewReason)] = daily.NeedsReviewReason
    };

    /// <summary>يمنع تعديل حضور فترة رواتب مقفلة.</summary>
    public async Task EnsurePeriodNotLockedAsync(DateTime workDate, CancellationToken ct = default)
    {
        await FinancialPeriodGuard.EnsureEditableAsync(_db,workDate.Date,workDate.Date,ct);
    }

    /// <summary>سجل التصحيحات لسجل حضور معين.</summary>
    public async Task<List<AttendanceCorrection>> GetHistoryAsync(int dailyAttendanceId, CancellationToken ct = default)
        => await _db.AttendanceCorrections.AsNoTracking()
            .Where(c => c.DailyAttendanceId == dailyAttendanceId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
}
