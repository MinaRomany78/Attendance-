using HrPayroll.Domain.Models;
using HrPayroll.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Web.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly DashboardService _dashboard;

    public DashboardController(DashboardService dashboard) => _dashboard = dashboard;

    public async Task<IActionResult> Index(int? year, int? month, DateTime? date, CancellationToken ct)
    {
        var current = date.HasValue ? PayrollPeriod.FromDate(date.Value) : await _dashboard.CurrentPeriodAsync(ct);
        var y = year ?? current.Year; var m = month ?? current.Month;
        var data = await _dashboard.GetAsync(y, m, date, ct);
        return View(data);
    }
}
