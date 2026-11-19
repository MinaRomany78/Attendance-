using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Devices;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HrPayroll.Tests;

public sealed class ZkDeviceTests
{
    [Fact]
    public async Task Different_Device_Timezone_Requires_Reviewed_Normalization_Before_Download()
    {
        using var db = new TestDb();
        await using var ctx = db.CreateContext();
        var device = Device();
        device.TimeZoneId = "UTC";
        ctx.AttendanceDevices.Add(device);
        await ctx.SaveChangesAsync();
        var driver = new FixtureDriver(new ZkAttendanceRecord("0003", new DateTime(2026, 11, 10, 8, 0, 0), "I"));
        var service = new AttendanceDeviceService(ctx, new EphemeralDataProtectionProvider(), new[] { driver }, new AuditService(ctx));
        var import = new AttendanceImportService(ctx, NullLogger<AttendanceImportService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadForPreviewAsync(device.Id, import, default));
        Assert.Equal(0, driver.AttendanceDownloadCalls);
    }

    [Fact]
    public async Task Configuration_Protects_Key_And_Unsupported_Device_Does_Not_Connect()
    {
        using var db = new TestDb();
        await using var ctx = db.CreateContext();
        var service = new AttendanceDeviceService(ctx, new EphemeralDataProtectionProvider(),
            Array.Empty<IZkDeviceClient>(), new AuditService(ctx));
        await service.SaveAsync(Device(), "sanitized-key", "test", default);
        var device = await ctx.AttendanceDevices.SingleAsync();
        Assert.NotEqual("sanitized-key", device.ProtectedCommunicationKey);
        Assert.False(service.HasCompatibleDriver(device));
        var result = await service.TestAsync(device.Id, default);
        Assert.False(result.Connected);
        Assert.Contains("لم تتم محاولة", result.Message);
        Assert.Null(device.LastSuccessfulSyncUtc);
        Assert.DoesNotContain("sanitized-key", (await ctx.AuditLogs.SingleAsync()).NewValue ?? string.Empty);
    }

    [Fact]
    public async Task Ethernet_And_Manual_Records_Share_Employee_Mapping_And_Deduplication()
    {
        using var db = new TestDb();
        await using var ctx = db.CreateContext();
        ctx.Employees.Add(TestDb.Employee(code: "0003"));
        var device = Device();
        ctx.AttendanceDevices.Add(device);
        await ctx.SaveChangesAsync();
        var import = new AttendanceImportService(ctx, NullLogger<AttendanceImportService>.Instance);
        var time = new DateTime(2026, 11, 10, 8, 3, 9, DateTimeKind.Unspecified);
        var manual = await import.AnalyzeRecordsAsync(new[] { new ParsedPunchRow
        {
            RowNumber = 1, DeviceEmployeeCode = "0003", PunchTime = time, IsTimeValid = true, AttType = "I"
        } }, "manual-test", default);
        await import.CommitAsync(manual, "manual-test.txt", "u1", "tester", ct: default);
        var service = new AttendanceDeviceService(ctx, new EphemeralDataProtectionProvider(),
            new[] { new FixtureDriver(new ZkAttendanceRecord("0003", time, "I", DeviceIdentifier: "test-device")) }, new AuditService(ctx));
        var ethernet = await service.DownloadForPreviewAsync(device.Id, import, default);
        Assert.Equal(1, ethernet.DuplicateCount);
        Assert.Equal(0, ethernet.SuccessCount);
        Assert.Equal(1, await ctx.RawPunches.CountAsync());
    }

    private static AttendanceDevice Device() => new()
    {
        Name = "Test device", Model = "Fixture only", IpAddress = "192.0.2.10", TcpPort = 4370,
        Identifier = "test-device", TimeZoneId = "Africa/Cairo"
    };

    private sealed class FixtureDriver(ZkAttendanceRecord record) : IZkDeviceClient
    {
        public int AttendanceDownloadCalls { get; private set; }
        public bool Supports(AttendanceDevice device) => device.Model == "Fixture only";
        public Task<ZkConnectionResult> TestConnectionAsync(ZkDeviceConnection device, CancellationToken ct)
            => Task.FromResult(new ZkConnectionResult(true, "Test fixture"));
        public Task<IReadOnlyList<ZkAttendanceRecord>> DownloadAttendanceAsync(ZkDeviceConnection device, CancellationToken ct)
        {
            AttendanceDownloadCalls++;
            return Task.FromResult<IReadOnlyList<ZkAttendanceRecord>>(new[] { record });
        }
        public Task<ZkDeviceInfo> GetDeviceInfoAsync(ZkDeviceConnection device, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ZkEmployeeRecord>> DownloadEmployeesAsync(ZkDeviceConnection device, CancellationToken ct) => throw new NotSupportedException();
        public Task<ZkSyncStatus> GetLastSyncStatusAsync(ZkDeviceConnection device, CancellationToken ct) => throw new NotSupportedException();
    }
}
