using System.Net;
using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Devices;
using HrPayroll.Infrastructure.Persistence;
using HrPayroll.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Web.Controllers;

[Authorize(Policy = "ManageAttendance")]
public sealed class AttendanceDevicesController : Controller
{
    private readonly AppDbContext _db;
    private readonly AttendanceDeviceService _devices;
    public AttendanceDevicesController(AppDbContext db, AttendanceDeviceService devices) { _db = db; _devices = devices; }

    public async Task<IActionResult> Index(int? id, CancellationToken ct)
    {
        var vm = new AttendanceDeviceViewModel();
        if (id.HasValue)
        {
            var d = await _db.AttendanceDevices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (d is null) return NotFound();
            vm.Role = d.Role; vm.Id = d.Id; vm.Name = d.Name; vm.IpAddress = d.IpAddress; vm.TcpPort = d.TcpPort;
            vm.Model = d.Model; vm.Identifier = d.Identifier; vm.TimeZoneId = d.TimeZoneId;
        }
        return View(await Populate(vm, ct));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(AttendanceDeviceViewModel vm, CancellationToken ct)
    {
        if (vm.Id != 0 && !await _db.AttendanceDevices.AnyAsync(d => d.Id == vm.Id, ct)) return NotFound();
        if (!IPAddress.TryParse(vm.IpAddress?.Trim(), out var address) || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            ModelState.AddModelError(nameof(vm.IpAddress), "أدخل عنوان IP صالحاً للجهاز على الشبكة المحلية.");
        try { TimeZoneInfo.FindSystemTimeZoneById(vm.TimeZoneId ?? string.Empty); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { ModelState.AddModelError(nameof(vm.TimeZoneId), "المنطقة الزمنية غير معروفة. استخدم Africa/Cairo أو Egypt Standard Time."); }
        if (!ModelState.IsValid)
        {
            vm.CommunicationKey = null;
            ModelState.Remove(nameof(vm.CommunicationKey));
            return View("Index", await Populate(vm, ct));
        }
        try { await _devices.SaveAsync(new AttendanceDevice
        {
            Role = vm.Role, Id = vm.Id, Name = vm.Name, IpAddress = vm.IpAddress ?? string.Empty, TcpPort = vm.TcpPort,
            Model = vm.Model, Identifier = vm.Identifier, TimeZoneId = vm.TimeZoneId ?? "Africa/Cairo"
        }, vm.CommunicationKey, User.Identity?.Name, ct); }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            vm.CommunicationKey = null; ModelState.Remove(nameof(vm.CommunicationKey));
            return View("Index", await Populate(vm, ct));
        }
        TempData["Success"] = "تم حفظ إعدادات الجهاز. الاتصال يتطلب برنامج تشغيل متوافقاً مع الطراز.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(int id, CancellationToken ct)
    {
        if (!await _db.AttendanceDevices.AnyAsync(d => d.Id == id, ct)) return NotFound();
        try
        {
            var result = await _devices.TestAsync(id, ct);
            TempData[result.Connected ? "Success" : "Error"] = result.Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { TempData["Error"] = "انتهت مهلة الاتصال بالجهاز."; }
        catch (Exception) when (!ct.IsCancellationRequested)
        { TempData["Error"] = "تعذّر الاتصال بالجهاز. راجع الشبكة وتوافق برنامج التشغيل والإعدادات."; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Synchronize(int? id, [FromServices] HrPayroll.Infrastructure.Import.AttendanceImportService import,
        [FromServices] HrPayroll.Infrastructure.Services.AttendanceProcessingService processing, CancellationToken ct)
    {
        try
        {
            var results = await _devices.SynchronizeAsync(id, import, processing, this.CurrentUserId(), this.CurrentUserName(), ct);
            TempData[results.Count > 0 && results.All(r => r.Successful) ? "Success" : "Error"] = results.Count == 0
                ? "لم يتم إعداد أجهزة دخول وخروج." : string.Join(" | ", results.Select(r =>
                    $"جهاز {r.AttendanceDeviceId}: " + (r.Successful ? $"محفوظ {r.Inserted}، مكرر {r.Duplicates}، غير صالح {r.Invalid}، غير مرتبط {r.Unmatched}، للمراجعة {r.Review}" : r.Error)));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { TempData["Error"] = ex is InvalidOperationException ? ex.Message : "تعذرت معالجة الحضور. راجع سجل المزامنة قبل إعادة المحاولة."; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MapEmployee(int deviceId, string code, int employeeId, CancellationToken ct)
    {
        try { await _devices.MapEmployeeAsync(deviceId, code, employeeId, User.Identity?.Name, ct); TempData["Success"] = "تم ربط كود الجهاز بالموظف."; }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> History(int id, CancellationToken ct)
    {
        var logs = await _db.Set<DeviceSyncLog>().AsNoTracking().Where(l => l.AttendanceDeviceId == id)
            .OrderByDescending(l => l.CreatedAt).Take(100).ToListAsync(ct);
        return View(logs);
    }
    private async Task<AttendanceDeviceViewModel> Populate(AttendanceDeviceViewModel vm, CancellationToken ct)
    {
        vm.Devices = await _db.AttendanceDevices.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        vm.SupportedDeviceIds = vm.Devices.Where(_devices.HasCompatibleDriver).Select(d => d.Id).ToHashSet();
        return vm;
    }
}
