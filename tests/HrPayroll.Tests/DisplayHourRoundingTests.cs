using HrPayroll.Domain.Entities;
using HrPayroll.Domain.Enums;
using HrPayroll.Domain.Models;
using HrPayroll.Domain.Services;
using HrPayroll.Infrastructure.Services;
namespace HrPayroll.Tests;
public sealed class DisplayHourRoundingTests
{
    [Theory]
    [InlineData(HourRoundingMode.Up,0.25,1)] [InlineData(HourRoundingMode.Down,0.25,0.75)] [InlineData(HourRoundingMode.Nearest,0.25,0.75)]
    [InlineData(HourRoundingMode.Up,0.5,1)] [InlineData(HourRoundingMode.Down,0.5,0.5)] [InlineData(HourRoundingMode.Nearest,0.5,1)]
    [InlineData(HourRoundingMode.Up,1,1)] [InlineData(HourRoundingMode.Down,1,0)] [InlineData(HourRoundingMode.Nearest,1,1)]
    public async Task Configured_Display_Rounding_Preserves_Existing_Financial_Deductions(HourRoundingMode mode,decimal step,decimal expected)
    {
        using var fixture=new TestDb();await using var db=fixture.CreateContext();
        db.AppSettings.AddRange(new AppSetting{Key=SettingKeys.DisplayHourRoundingMode,Value=mode.ToString()},
            new AppSetting{Key=SettingKeys.DisplayHourRoundingIncrement,Value=step.ToString(System.Globalization.CultureInfo.InvariantCulture)});await db.SaveChangesAsync();
        var policy=await new SettingsService(db).GetPolicyAsync();Assert.Equal(expected,HourDurationCalculator.RoundMinutes(49,policy.HourRounding));
        Assert.False(policy.RoundedAttendanceDeductionsEnabled);Assert.False(policy.RoundedOvertimeEnabled);
        var input=new PayrollInputs{BasicSalary=6000,LateMinutes=49};var actual=PayrollCalculator.Compute(input,policy);var original=PayrollCalculator.Compute(input,new AttendancePolicy());
        Assert.Equal(original.NetSalary,actual.NetSalary);Assert.Equal(original.LateDeduction,actual.LateDeduction);Assert.Equal(49,actual.LateMinutes);
    }
}
