using Adminbot.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Global reports that cannot be claimed must never queue behind an active SQLite writer.</summary>
    /// <param name="status">Stored status for the global weekly key; Processing is seeded with a live lease.</param>
    /// <returns>A task proving that a real competing transaction does not block the read-only poll.</returns>
    /// <remarks>Regression for production SQLITE_BUSY on a no-op UPDATE after the report was already sent.</remarks>
    [Theory]
    [InlineData(UsageReportDispatchStatuses.Sent)]
    [InlineData(UsageReportDispatchStatuses.DeliveryRecordedWithError)]
    [InlineData(UsageReportDispatchStatuses.Processing)]
    public async Task Global_weekly_ineligible_report_skips_writer_admission(string status)
    {
        using var databases = new Databases();
        const string key = "weekly:20261107";
        var now = DateTime.UtcNow;
        await using var blocker = databases.Users.CreateDbContext();
        blocker.UsageReportDispatches.Add(new UsageReportDispatch
        {
            ReportKey = key,
            PeriodStartUtc = now.AddDays(-7),
            PeriodEndUtc = now,
            Status = status,
            AttemptCount = 1,
            LeaseUntilUtc = status == UsageReportDispatchStatuses.Processing ? now.AddMinutes(10) : null
        });
        await blocker.SaveChangesAsync();
        // Hold the same process-local writer turn used in production while leaving WAL reads available.
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("UPDATE UsageReportDispatches SET AttemptCount = AttemptCount WHERE ReportKey = 'weekly:20261107'");
        var connection = new SqliteConnectionStringBuilder(blocker.Database.GetDbConnection().ConnectionString)
        {
            DefaultTimeout = 1,
            Pooling = false
        };
        var factory = new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>().UseSqlite(connection.ToString()).Options);
        var store = new UsageReportDispatchStore(factory);
        Assert.False(await store.TryClaimAsync(key, now.AddDays(-7), now, default));
        await using var verify = factory.CreateDbContext();
        var row = await verify.UsageReportDispatches.AsNoTracking().SingleAsync();
        Assert.Equal(status, row.Status);
        Assert.Equal(1, row.AttemptCount);
    }

    /// <summary>A new or expired global report still grants just one claim across concurrent workers.</summary>
    /// <param name="expired">True to recover a previously attempted expired processing lease; false for a missing row.</param>
    /// <returns>A task proving one row, one new claim and a live lease after contention.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Global_weekly_new_or_expired_report_still_has_one_atomic_claim(bool expired)
    {
        using var databases = new Databases();
        const string key = "weekly:20261107";
        var now = DateTime.UtcNow;
        if (expired)
        {
            await using var seed = databases.Users.CreateDbContext();
            seed.UsageReportDispatches.Add(new UsageReportDispatch
            {
                ReportKey = key,
                PeriodStartUtc = now.AddDays(-7),
                PeriodEndUtc = now,
                Status = UsageReportDispatchStatuses.Processing,
                AttemptCount = 1,
                LeaseUntilUtc = now.AddMinutes(-1)
            });
            await seed.SaveChangesAsync();
        }

        var store = new UsageReportDispatchStore(databases.Users);
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
            () => store.TryClaimAsync(key, now.AddDays(-7), now, default))));
        Assert.Single(claims, claimed => claimed);
        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.UsageReportDispatches.AsNoTracking().SingleAsync();
        Assert.Equal(UsageReportDispatchStatuses.Processing, row.Status);
        Assert.Equal(expired ? 2 : 1, row.AttemptCount);
        Assert.True(row.LeaseUntilUtc > now);
    }
}
