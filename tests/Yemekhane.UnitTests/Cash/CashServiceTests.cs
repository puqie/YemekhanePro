using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Cash;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Cash;

public sealed class CashServiceTests
{
    [Fact]
    public async Task DailyUsesIstanbulMidnightAndExcludesEndBoundary()
    {
        await using var fixture = await CashFixture.CreateAsync();
        var type = fixture.AddType("Nakit");
        fixture.Add(type, "2026-08-30T20:59:59Z", 1m);
        fixture.Add(type, "2026-08-30T21:00:00Z", 10m);
        fixture.Add(type, "2026-08-31T20:59:59Z", 20m);
        fixture.Add(type, "2026-08-31T21:00:00Z", 40m);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetDailyAsync(new DateOnly(2026, 8, 31));

        Assert.Equal(30m, result.TotalAmount);
        Assert.Equal(2, result.TransactionCount);
        Assert.Equal(DateTimeOffset.Parse("2026-08-30T21:00:00Z", CultureInfo.InvariantCulture), result.UtcFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T21:00:00Z", CultureInfo.InvariantCulture), result.UtcToExclusive);
    }

    [Fact]
    public async Task VoidedTransactionsAreReportedSeparatelyFromNetTotal()
    {
        await using var fixture = await CashFixture.CreateAsync();
        var type = fixture.AddType("Nakit");
        fixture.Add(type, "2026-08-31T09:00:00Z", 125.50m);
        fixture.Add(type, "2026-08-31T10:00:00Z", 80.25m, isVoided: true);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetDailyAsync(new DateOnly(2026, 8, 31));

        Assert.Equal(125.50m, result.TotalAmount);
        Assert.Equal(1, result.TransactionCount);
        Assert.Equal(80.25m, result.VoidedAmount);
        Assert.Equal(1, result.VoidedCount);
        Assert.Equal(125.50m, Assert.Single(result.ByIncomeType).Amount);
    }

    [Fact]
    public async Task WeeklyMonthlyAndTypeBreakdownUseCalendarRanges()
    {
        await using var fixture = await CashFixture.CreateAsync();
        var cash = fixture.AddType("Nakit");
        var transfer = fixture.AddType("Havale");
        fixture.Add(cash, "2026-08-30T09:00:00Z", 5m);    // Sunday, previous ISO week
        fixture.Add(cash, "2026-08-31T09:00:00Z", 10m);   // Monday
        fixture.Add(cash, "2026-09-01T09:00:00Z", 20m);
        fixture.Add(transfer, "2026-09-06T09:00:00Z", 30m); // Sunday
        fixture.Add(transfer, "2026-09-07T09:00:00Z", 40m); // next Monday
        await fixture.Context.SaveChangesAsync();

        var weekly = await fixture.Service.GetSummaryAsync(CashSummaryPeriod.IsoWeek,
            new DateOnly(2026, 9, 2));
        var monthly = await fixture.Service.GetSummaryAsync(CashSummaryPeriod.Monthly,
            new DateOnly(2026, 9, 20));

        Assert.Equal(new DateOnly(2026, 8, 31), weekly.From);
        Assert.Equal(new DateOnly(2026, 9, 6), weekly.To);
        Assert.Equal(60m, weekly.TotalAmount);
        Assert.Collection(weekly.ByIncomeType,
            x => Assert.Equal(("Havale", 30m, 1), (x.IncomeTypeName, x.Amount, x.TransactionCount)),
            x => Assert.Equal(("Nakit", 30m, 2), (x.IncomeTypeName, x.Amount, x.TransactionCount)));
        Assert.Equal(90m, monthly.TotalAmount);
    }

    [Fact]
    public async Task CustomRangeIsInclusiveByIstanbulCalendarDay()
    {
        await using var fixture = await CashFixture.CreateAsync();
        var type = fixture.AddType("Nakit");
        fixture.Add(type, "2026-09-01T20:59:59Z", 10m);
        fixture.Add(type, "2026-09-01T21:00:00Z", 20m);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetSummaryAsync(CashSummaryPeriod.Custom,
            from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 1));

        Assert.Equal(10m, result.TotalAmount);
    }

    [Fact]
    public async Task EmptyPeriodReturnsZeroesAndNoBreakdown()
    {
        await using var fixture = await CashFixture.CreateAsync();

        var result = await fixture.Service.GetDailyAsync(new DateOnly(2026, 8, 31));

        Assert.Equal(0m, result.TotalAmount);
        Assert.Equal(0, result.TransactionCount);
        Assert.Equal(0m, result.VoidedAmount);
        Assert.Equal(0, result.VoidedCount);
        Assert.Empty(result.ByIncomeType);
    }

    [Fact]
    public async Task InvalidCustomRangeIsRejected()
    {
        await using var fixture = await CashFixture.CreateAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Service.GetSummaryAsync(
            CashSummaryPeriod.Custom, from: new DateOnly(2026, 9, 2), to: new DateOnly(2026, 9, 1)));
    }

    private sealed class CashFixture(SqliteConnection connection, YemekhaneDbContext context) : IAsyncDisposable
    {
        public YemekhaneDbContext Context { get; } = context;
        public CashService Service { get; } = new(new EfCashRepository(context),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero)));

        public static async Task<CashFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>()
                .UseSqlite(connection).Options);
            await context.Database.MigrateAsync();
            return new CashFixture(connection, context);
        }

        public IncomeType AddType(string name)
        {
            var type = new IncomeType { Name = name };
            Context.Add(type);
            return type;
        }

        public void Add(IncomeType type, string timestamp, decimal amount, bool isVoided = false) =>
            Context.Add(new IncomeTransaction
            {
                OperationId = Guid.NewGuid(), IncomeTypeId = type.Id,
                TransactionAt = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture), Amount = amount,
                CreatedBy = Guid.NewGuid(), IsVoided = isVoided
            });

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
