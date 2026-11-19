using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Import;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HrPayroll.Tests;

public sealed class AttendanceCommitFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_File_Cannot_Leak_Into_A_Subsequent_Commit_On_The_Same_Context(bool failAfterSave)
    {
        using var db = new TestDb();
        await using var seed = db.CreateContext();
        seed.Employees.Add(TestDb.Employee(code: "test001"));
        await seed.SaveChangesAsync();
        var interceptor = new FailOneImportSave(failAfterSave);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(seed.Database.GetDbConnection()).AddInterceptors(interceptor).Options;
        await using var context = new AppDbContext(options);
        var employee = await context.Employees.SingleAsync();
        var service = new AttendanceImportService(context, NullLogger<AttendanceImportService>.Instance);
        var failedTime = new DateTime(2026, 11, 10, 8, 0, 0);
        var failed = await service.AnalyzeRecordsAsync(new[] { Row(failedTime) }, "failed fixture");
        await Assert.ThrowsAsync<DbUpdateException>(() => service.CommitAsync(failed, "failed-fixture.txt", "u1", "tester"));
        Assert.Empty(context.ChangeTracker.Entries<ImportBatch>());
        Assert.Empty(context.ChangeTracker.Entries<RawPunch>());
        Assert.Equal(EntityState.Unchanged, context.Entry(employee).State);
        Assert.Equal(0, await context.ImportBatches.CountAsync());
        Assert.Equal(0, await context.RawPunches.CountAsync());

        var successTime = failedTime.AddDays(1);
        var success = await service.AnalyzeRecordsAsync(new[] { Row(successTime) }, "successful fixture");
        var result = await service.CommitAsync(success, "successful-fixture.txt", "u1", "tester");
        Assert.Equal(1, result.Inserted);
        Assert.Equal("successful-fixture.txt", (await context.ImportBatches.SingleAsync()).FileName);
        Assert.Equal(successTime, (await context.RawPunches.SingleAsync()).PunchTime);
    }

    private static ParsedPunchRow Row(DateTime time) => new()
    {
        RowNumber = 1, DeviceEmployeeCode = "test001", AttType = "I", PunchTime = time,
        IsTimeValid = true, RawPunchTime = time.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
    };

    private sealed class FailOneImportSave(bool failAfterSave) : SaveChangesInterceptor
    {
        private bool _failed;
        private bool ShouldFail(DbContextEventData data) => !_failed && data.Context is not null
            && data.Context.ChangeTracker.Entries<ImportBatch>().Any();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!failAfterSave && ShouldFail(eventData)) Fail();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (failAfterSave && ShouldFail(eventData)) Fail();
            return ValueTask.FromResult(result);
        }

        private void Fail()
        {
            _failed = true;
            throw new DbUpdateException("Sanitized simulated import failure.");
        }
    }
}
