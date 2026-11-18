using System.ComponentModel.DataAnnotations;
using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Services;
namespace HrPayroll.Web.ViewModels;
public sealed class SalaryAdvancesPage
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string? Department { get; set; }
    public string? Search { get; set; }
    public DateTime Date { get; set; }
    public string PeriodText { get; set; } = string.Empty;
    public bool PaymentsEnabled { get; set; }
    public bool IsReadOnly { get; set; }
    public int WindowStart { get; set; } = 15;
    public int WindowEnd { get; set; } = 15;
    public List<string> Departments { get; set; } = new();
    public List<AdvanceBalance> Balances { get; set; } = new();
    public List<SalaryAdvance> History { get; set; } = new();
    public List<AdvancePaymentInput> Payments { get; set; } = new();
}
public sealed class AdvancePaymentInput
{
    public int EmployeeId { get; set; }
    public decimal Amount { get; set; }
    public Guid RequestId { get; set; }
    public int Id { get; set; }
    public long Revision { get; set; }
    public bool Selected { get; set; }
}
