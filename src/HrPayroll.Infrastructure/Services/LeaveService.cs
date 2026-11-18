using HrPayroll.Domain.Services;
using System.Data;
using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Infrastructure.Services;

public sealed class LeaveOverlap
{
    public int LeaveRequestId { get; set; }
    public LeaveType LeaveType { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public ApprovalStatus Status { get; set; }
}

/// <summary>
/// إدارة الإجازات: حساب أيام العمل، منع التداخل والتكرار، تحويل الغياب إلى إجازة معتمدة،
/// وتحديث رصيد الإجازة السنوية مرة واحدة فقط.
/// </summary>
public class LeaveService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;

    public LeaveService(AppDbContext db, SettingsService settings, AuditService audit)
    {
        _db = db;
        _settings = settings;
        _audit = audit;
    }

    /// <summary>عدد أيام العمل الفعلية بين تاريخين (بدون أيام الراحة الأسبوعية والعطلات الرسمية).</summary>
    public async Task<decimal> CountWorkingDaysAsync(
        DateTime start, DateTime end, bool excludeRestDays = true, bool excludeHolidays = true, CancellationToken ct = default)
    {
        start = start.Date;
        end = end.Date;
        if (end < start) (start, end) = (end, start);

        var restDays = excludeRestDays ? await _settings.GetWeeklyRestDaysAsync(ct) : new HashSet<DayOfWeek>();
        var holidays = excludeHolidays
            ? await _db.PublicHolidays.AsNoTracking()
                .Where(h => h.IsActive && h.HolidayDate >= start && h.HolidayDate <= end)
                .Select(h => h.HolidayDate.Date)
                .ToListAsync(ct)
            : new List<DateTime>();
        var holidaySet = holidays.ToHashSet();

        return WorkingDayCalculator.Count(start,end,restDays,holidaySet);
    }

    /// <summary>يتحقق من عدم تداخل الطلب الجديد مع طلب إجازة آخر لنفس الموظف.</summary>
    public async Task<List<LeaveOverlap>> FindOverlapsAsync(
        int employeeId, DateTime start, DateTime end, int? excludeLeaveId = null, CancellationToken ct = default)
    {
        start = start.Date;
        end = end.Date;
        var q = _db.LeaveRequests.AsNoTracking()
            .Where(l => l.EmployeeId == employeeId
                        && l.Status != ApprovalStatus.Rejected
                        && l.StartDate <= end && l.EndDate >= start);
        if (excludeLeaveId.HasValue) q = q.Where(l => l.Id != excludeLeaveId.Value);

        return await q.Select(l => new LeaveOverlap
        {
            LeaveRequestId = l.Id,
            LeaveType = l.LeaveType,
            StartDate = l.StartDate,
            EndDate = l.EndDate,
            Status = l.Status
        }).ToListAsync(ct);
    }

    /// <summary>ينشئ طلب إجازة بعد التحقق من التداخل وحساب أيام العمل.</summary>
    public async Task<LeaveRequest> CreateAsync(
        LeaveRequest leave, string? user, bool allowOverlap = false, CancellationToken ct = default)
    {
        if (leave.EndDate.Date < leave.StartDate.Date)
            throw new InvalidOperationException("تاريخ نهاية الإجازة قبل تاريخ البداية.");

        if (!await _db.Employees.AnyAsync(e => e.Id == leave.EmployeeId, ct))
            throw new InvalidOperationException("الموظف غير موجود.");
        if (!Enum.IsDefined(leave.LeaveType)) throw new InvalidOperationException("نوع الإجازة غير صالح.");
        await FinancialPeriodGuard.EnsureEditableAsync(_db, leave.StartDate, leave.EndDate, ct);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var overlaps = await FindOverlapsAsync(leave.EmployeeId, leave.StartDate, leave.EndDate, null, ct);
        if (overlaps.Count > 0 && !allowOverlap)
            throw new InvalidOperationException(
                "يوجد تداخل مع طلب إجازة آخر لنفس الموظف: " +
                string.Join(" / ", overlaps.Select(o => $"{o.StartDate:yyyy-MM-dd}..{o.EndDate:yyyy-MM-dd}")));

        leave.StartDate = leave.StartDate.Date;
        leave.EndDate = leave.EndDate.Date;
        leave.DaysCount = (leave.EndDate - leave.StartDate).Days + 1;
        leave.WorkingDays = await CountWorkingDaysAsync(leave.StartDate, leave.EndDate, ct: ct);
        leave.Status = ApprovalStatus.Pending;
        leave.ApprovedBy = null;
        leave.ApprovedAt = null;
        leave.PayrollItemId = null;
        leave.PayrollItem = null;
        leave.Employee = null;
        leave.CreatedBy = user;
        leave.CreatedAt = DateTime.UtcNow;

        _db.LeaveRequests.Add(leave);
        await _audit.LogAsync("إضافة طلب إجازة", nameof(LeaveRequest), null, null,
            $"{leave.LeaveType} {leave.StartDate:yyyy-MM-dd}..{leave.EndDate:yyyy-MM-dd} ({leave.WorkingDays} يوم عمل)", user, ct: ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return leave;
    }

    /// <summary>يعتمد الطلب ويخصم رصيد الإجازة السنوية مرة واحدة فقط.</summary>
    public async Task ApproveAsync(int leaveId, string? user, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(l => l.Id == leaveId, ct)
                    ?? throw new InvalidOperationException("طلب الإجازة غير موجود");

        if (leave.Status == ApprovalStatus.Approved) return;

        if (leave.PayrollItemId.HasValue) throw new InvalidOperationException("الإجازة مرتبطة بكشف رواتب؛ أعد فتح الكشف أولاً.");
        await FinancialPeriodGuard.EnsureEditableAsync(_db, leave.StartDate, leave.EndDate, ct);
        var overlaps = await FindOverlapsAsync(leave.EmployeeId, leave.StartDate, leave.EndDate, leave.Id, ct);
        if (overlaps.Any(o => o.Status == ApprovalStatus.Approved))
            throw new InvalidOperationException("يوجد طلب إجازة معتمد متداخل لنفس الموظف.");
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == leave.EmployeeId, ct)
            ?? throw new InvalidOperationException("الموظف غير موجود.");
        if (leave.LeaveType == LeaveType.Annual && employee.AnnualLeaveBalance < leave.WorkingDays)
            throw new InvalidOperationException("رصيد الإجازة السنوية غير كافٍ. لا توجد سياسة معتمدة تسمح برصيد سالب.");

        leave.Status = ApprovalStatus.Approved;
        leave.ApprovedBy = user;
        leave.ApprovedAt = DateTime.UtcNow;
        leave.IsPaid = leave.IsPaid && leave.LeaveType != LeaveType.Unpaid && leave.LeaveType != LeaveType.UnexcusedAbsence;

        // خصم الرصيد السنوي مرة واحدة فقط (منع الخصم المتكرر عند إعادة الاعتماد)
        if (leave.LeaveType == LeaveType.Annual)
        {
            employee.AnnualLeaveBalance -= leave.WorkingDays;
            employee.UpdatedAt = DateTime.UtcNow;
            employee.UpdatedBy = user;
        }

        await _audit.LogAsync("اعتماد إجازة", nameof(LeaveRequest), leave.Id.ToString(), "Pending", "Approved", user, ct: ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task RejectAsync(int leaveId, string? user, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(l => l.Id == leaveId, ct)
                    ?? throw new InvalidOperationException("طلب الإجازة غير موجود");
        if (leave.Status == ApprovalStatus.Rejected) return;
        if (leave.PayrollItemId.HasValue) throw new InvalidOperationException("الإجازة مرتبطة بكشف رواتب؛ أعد فتح الكشف أولاً.");
        await FinancialPeriodGuard.EnsureEditableAsync(_db, leave.StartDate, leave.EndDate, ct);

        var wasApproved = leave.Status == ApprovalStatus.Approved;
        leave.Status = ApprovalStatus.Rejected;
        leave.ApprovedBy = user;
        leave.ApprovedAt = DateTime.UtcNow;

        // إعادة الرصيد إذا كان الطلب معتمدًا سابقًا
        if (wasApproved && leave.LeaveType == LeaveType.Annual)
        {
            var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == leave.EmployeeId, ct);
            if (employee is not null)
            {
                employee.AnnualLeaveBalance += leave.WorkingDays;
                employee.UpdatedAt = DateTime.UtcNow;
                employee.UpdatedBy = user;
            }
        }

        await _audit.LogAsync("رفض إجازة", nameof(LeaveRequest), leave.Id.ToString(),
            wasApproved ? "Approved" : "Pending", "Rejected", user, ct: ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// يحوّل أيام غياب مختارة إلى إجازة معتمدة (سنوية أو مدفوعة أو بدون أجر).
    /// الأيام غير المشمولة تبقى غيابًا بدون أجر.
    /// </summary>
    public async Task<LeaveRequest> ConvertAbsenceAsync(
        int employeeId, IEnumerable<DateTime> absenceDates, LeaveType leaveType,
        string? reason, string? user, CancellationToken ct = default)
    {
        var dates = absenceDates.Select(d => d.Date).Distinct().OrderBy(d => d).ToList();
        if (dates.Count == 0) throw new InvalidOperationException("لم يتم تحديد أي يوم غياب.");
        if (!Enum.IsDefined(leaveType)) throw new InvalidOperationException("نوع الإجازة غير صالح.");
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await FinancialPeriodGuard.EnsureEditableAsync(_db, dates.First(), dates.Last(), ct);
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct)
                       ?? throw new InvalidOperationException("الموظف غير موجود");
        var restDays = await _settings.GetWeeklyRestDaysAsync(ct);
        var holidays = await _db.PublicHolidays.Where(h => h.IsActive && dates.Contains(h.HolidayDate))
            .Select(h => h.HolidayDate).ToListAsync(ct);
        var nonWorking = holidays.Select(d => d.Date).ToHashSet();
        dates = dates.Where(d => !restDays.Contains(d.DayOfWeek) && !nonWorking.Contains(d)).ToList();
        if (dates.Count == 0) throw new InvalidOperationException("كل الأيام المحددة أيام راحة أو عطلات رسمية.");
        var daily = await _db.DailyAttendances
            .Where(d => d.EmployeeId == employeeId && dates.Contains(d.WorkDate)).ToListAsync(ct);
        if (daily.Count != dates.Count || daily.Any(d => d.Status is not (AttendanceStatus.Absent or AttendanceStatus.UnexcusedAbsence)))
            throw new InvalidOperationException("يمكن تحويل أيام الغياب المؤكدة فقط؛ لا يمكن تحويل يوم حضور أو يوم لم تُستورد بياناته.");
        if (leaveType == LeaveType.Annual && employee.AnnualLeaveBalance < dates.Count)
            throw new InvalidOperationException("رصيد الإجازة السنوية غير كافٍ لتحويل الأيام المحددة.");
        foreach (var date in dates)
            if ((await FindOverlapsAsync(employeeId, date, date, ct: ct)).Count > 0)
                throw new InvalidOperationException($"يوجد طلب إجازة متداخل في {date:yyyy-MM-dd}.");

        // Separate intervals preserve selected gaps: reprocessing must not grant leave to unselected dates.
        var groups = new List<List<DateTime>>();
        foreach (var date in dates)
        {
            if (groups.Count == 0 || groups[^1][^1].AddDays(1) != date) groups.Add(new List<DateTime>());
            groups[^1].Add(date);
        }
        var leaves = groups.Select(group => new LeaveRequest
        {
            EmployeeId = employeeId, StartDate = group[0], EndDate = group[^1], LeaveType = leaveType,
            Status = ApprovalStatus.Approved, IsPaid = leaveType is not (LeaveType.Unpaid or LeaveType.UnexcusedAbsence),
            Reason = reason, ApprovedBy = user, ApprovedAt = DateTime.UtcNow, DaysCount = group.Count,
            WorkingDays = group.Count, CreatedFromAbsence = true, CreatedBy = user, CreatedAt = DateTime.UtcNow
        }).ToList();
        _db.LeaveRequests.AddRange(leaves);
        foreach (var d in daily)
        {
            var previous = d.Status.ToString();
            var leaveForDate = leaves.Single(l => d.WorkDate >= l.StartDate && d.WorkDate <= l.EndDate);
            d.LeaveRequest = leaveForDate;
            d.Status = leaveType switch
            {
                LeaveType.Unpaid or LeaveType.UnexcusedAbsence => AttendanceStatus.UnpaidLeave,
                _ => AttendanceStatus.PaidLeave
            };
            d.Notes = $"تم تحويل الغياب إلى إجازة معتمدة ({leaveType})";
            d.AdminNotes = reason;
            d.UpdatedAt = DateTime.UtcNow;
            d.UpdatedBy = user;

            _db.AttendanceCorrections.Add(new AttendanceCorrection
            {
                DailyAttendanceId = d.Id,
                EmployeeId = employeeId,
                WorkDate = d.WorkDate,
                FieldName = nameof(DailyAttendance.Status),
                OldValue = previous,
                NewValue = d.Status.ToString(),
                Reason = reason ?? "تحويل غياب إلى إجازة معتمدة",
                Status = ApprovalStatus.Approved,
                ApprovedBy = user,
                ApprovedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = user
            });
        }

        if (leaveType == LeaveType.Annual)
        {
            employee.AnnualLeaveBalance -= dates.Count;
            employee.UpdatedAt = DateTime.UtcNow;
            employee.UpdatedBy = user;
        }

        await _audit.LogAsync("تحويل غياب إلى إجازة", nameof(LeaveRequest), null, null,
            $"{leaveType}: {dates.Count} يوم ({string.Join(", ", dates.Select(x => x.ToString("yyyy-MM-dd")))})", user, ct: ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return leaves[0];
    }

    /// <summary>أيام الغياب الحالية لموظف داخل فترة (لشاشة التحويل).</summary>
    public async Task<List<DailyAttendance>> GetAbsenceDaysAsync(
        int employeeId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        from = from.Date;
        to = to.Date;
        return await _db.DailyAttendances.AsNoTracking()
            .Include(d => d.Shift)
            .Where(d => d.EmployeeId == employeeId && d.WorkDate >= from && d.WorkDate <= to
                        && (d.Status == AttendanceStatus.Absent || d.Status == AttendanceStatus.UnexcusedAbsence))
            .OrderBy(d => d.WorkDate)
            .ToListAsync(ct);
    }
}
