using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Calendar;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Calendar;

/// <summary>
/// Tatil araligi: bayram/yariyil icin baslangic-bitis tek istekte yazilir (her gun ayri satir,
/// ortak GroupId), ayni gun + kapsam ikinci kez yazilmaz, yanlis tatil tek gun ya da tum aralik
/// olarak silinir. Okul "birden fazla gun secebilmeliyim, mantik disi kullanimlar var" dedi:
/// onceden tek gun ekleniyor, silinemiyor ve ayni gune ust uste tatil biriktirilebiliyordu.
/// </summary>
public sealed class HolidayRangeTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext context;
    private readonly EfHolidayRepository repository;
    private readonly HolidayService service;

    public HolidayRangeTests()
    {
        connection.Open();
        context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        context.Database.Migrate();
        repository = new EfHolidayRepository(context);
        service = new HolidayService(repository);
    }

    public async ValueTask DisposeAsync() { await context.DisposeAsync(); await connection.DisposeAsync(); }

    private static CreateHolidayRequest Request(DateOnly date, DateOnly? end = null, string name = "Kurban Bayramı", params HolidayScopeRequest[] scopes) =>
        new(date, name, "Official", null, "NextBusinessDay", scopes.Length == 0 ? [new("AllSchool")] : scopes, end);

    [Fact]
    public async Task RangeWritesOneRowPerDaySharingAGroupAndClosesEveryDay()
    {
        var created = await service.CreateAsync(Request(new DateOnly(2026, 6, 6), new DateOnly(2026, 6, 9)));

        Assert.Equal(4, created.DayCount);
        Assert.NotNull(created.GroupId);
        Assert.Equal(new DateOnly(2026, 6, 6), created.Date);
        var rows = await context.Holidays.OrderBy(x => x.Date).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal(created.GroupId, row.GroupId));
        Assert.Equal(4, await context.Set<HolidayScope>().CountAsync());
        var scope = new CalendarScope("Class", Guid.NewGuid());
        Assert.True(await repository.IsClosedAsync(new DateOnly(2026, 6, 6), scope, default));
        Assert.True(await repository.IsClosedAsync(new DateOnly(2026, 6, 9), scope, default));
        Assert.False(await repository.IsClosedAsync(new DateOnly(2026, 6, 10), scope, default));
        var listed = await service.ListAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        Assert.Equal(4, listed.Count);
        Assert.All(listed, item => Assert.Equal(created.GroupId, item.GroupId));
    }

    [Fact]
    public async Task SingleDayHasNoGroupAndSameEndDateCountsAsOneDay()
    {
        var single = await service.CreateAsync(Request(new DateOnly(2026, 4, 23)));
        var explicitSame = await service.CreateAsync(Request(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 1), "1 Mayıs"));

        Assert.Null(single.GroupId); Assert.Equal(1, single.DayCount);
        Assert.Null(explicitSame.GroupId); Assert.Equal(1, explicitSame.DayCount);
    }

    [Fact]
    public async Task EndBeforeStartAndOverlongRangesAreRejectedWithoutWriting()
    {
        var backwards = await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateAsync(Request(new DateOnly(2026, 6, 9), new DateOnly(2026, 6, 6))));
        Assert.Contains("başlangıçtan önce", backwards.Message, StringComparison.Ordinal);

        var tooLong = await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateAsync(Request(new DateOnly(2026, 6, 15), new DateOnly(2026, 9, 1))));
        Assert.Contains("62 gün", tooLong.Message, StringComparison.Ordinal);

        Assert.Equal(0, await context.Holidays.CountAsync());
    }

    [Fact]
    public async Task SameDayAndOverlappingScopeIsRefusedAsConflict()
    {
        var class5A = Guid.NewGuid(); var class5B = Guid.NewGuid();
        await service.CreateAsync(Request(new DateOnly(2026, 9, 14), null, "5A Gezi", new HolidayScopeRequest("Class", class5A)));

        // Baska sinif: cakisma yok.
        await service.CreateAsync(Request(new DateOnly(2026, 9, 14), null, "5B Gezi", new HolidayScopeRequest("Class", class5B)));
        // Ayni sinif ayni gun: 409.
        var sameClass = await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.CreateAsync(Request(new DateOnly(2026, 9, 14), null, "5A Tekrar", new HolidayScopeRequest("Class", class5A))));
        Assert.Contains("14 Eylül 2026", sameClass.Message, StringComparison.Ordinal);
        Assert.Contains("5A Gezi", sameClass.Message, StringComparison.Ordinal);
        // Tum okul o gunu zaten kapsayan sinif tatiliyle cakisir; aralik icindeki tek bir gun bile yeter.
        var school = await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.CreateAsync(Request(new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 15), "Ara tatil")));
        Assert.Contains("5A Gezi", school.Message, StringComparison.Ordinal);
        Assert.Equal(2, await context.Holidays.CountAsync());
    }

    [Fact]
    public async Task DeleteRemovesOneDayOrTheWholeGroupWithScopes()
    {
        var created = await service.CreateAsync(Request(new DateOnly(2026, 1, 19), new DateOnly(2026, 1, 30), "Yarıyıl"));
        var rows = await context.Holidays.AsNoTracking().OrderBy(x => x.Date).ToListAsync();

        Assert.Equal(1, await service.DeleteAsync(rows[3].Id, wholeGroup: false));
        Assert.Equal(11, await context.Holidays.CountAsync());
        Assert.Equal(11, await context.Set<HolidayScope>().CountAsync());
        Assert.False(await repository.IsClosedAsync(rows[3].Date, new CalendarScope("AllSchool", null), default));

        Assert.Equal(11, await service.DeleteAsync(rows[0].Id, wholeGroup: true));
        Assert.Equal(0, await context.Holidays.CountAsync());
        Assert.Equal(0, await context.Set<HolidayScope>().CountAsync());
        Assert.Contains(await context.AuditLogs.ToListAsync(), x => x.Action == "HolidayDeleted" && x.Description.Contains("11 günlük", StringComparison.Ordinal));
        _ = created;
    }

    [Fact]
    public async Task DeletingAMissingHolidayIsNotFound()
    {
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.DeleteAsync(Guid.NewGuid(), false));
    }

    /// <summary>Takvim projeksiyonu araligin toplam gununu tasir: cekmece "Tüm aralığı sil (12 gün)" diyebilsin.</summary>
    [Fact]
    public async Task MonthProjectionCarriesGroupDayCountAcrossMonthBoundary()
    {
        await service.CreateAsync(Request(new DateOnly(2026, 1, 26), new DateOnly(2026, 2, 6), "Yarıyıl"));
        await service.CreateAsync(Request(new DateOnly(2026, 2, 20), null, "Tek gün"));
        var calendar = new EfCalendarRepository(context);

        var february = await calendar.GetMonthAsync(new DateOnly(2026, 2, 1), null, default);

        var sixth = february.Days.Single(x => x.Date == new DateOnly(2026, 2, 6)).Holidays.Single();
        Assert.Equal(12, sixth.GroupDayCount);
        Assert.NotNull(sixth.GroupId);
        var twentieth = february.Days.Single(x => x.Date == new DateOnly(2026, 2, 20)).Holidays.Single();
        Assert.Equal(1, twentieth.GroupDayCount);
        Assert.Null(twentieth.GroupId);
    }
}
