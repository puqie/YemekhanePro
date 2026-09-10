using Yemekhane.Application.Common;
using Yemekhane.Application.Income;

namespace Yemekhane.UnitTests.Income;

/// <summary>
/// Gelir listesi ile Kasa Ozeti AYNI tarih araligini AYNI sekilde yorumlamalidir.
///
/// <para>
/// Ekran "31 Ekim"i secince gunun BASINI gonderiyor; gelir sorgusu ise
/// <c>TransactionAt &lt;= To</c> kullaniyor. Duzeltme olmadan 31 Ekim gun icindeki
/// tahsilatlarin HICBIRI listeye girmezdi. Kasa Ozeti ayni araligi tam gun sayiyor,
/// dolayisiyla iki ekran farkli toplam gosterip muhasebeyi bir gunluk tahsilat
/// kadar ayiriyordu.
/// </para>
/// </summary>
public sealed class IncomeDateBoundaryTests
{
    private static async Task<IncomeTransactionFilter> CapturedAsync(DateTimeOffset? to)
    {
        var repository = new CapturingRepository();
        var service = new IncomeService(repository);
        await service.ListAsync(new IncomeTransactionFilter(To: to));
        return repository.Last!;
    }

    /// <summary>Gun basi verilirse GUN SONUNA cekilir; o gunun tahsilatlari listeye girer.</summary>
    [Fact]
    public async Task AMidnightEndDateIsStretchedToTheEndOfThatDay()
    {
        var midnight = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.FromHours(3));

        var captured = await CapturedAsync(midnight);

        Assert.Equal(new DateOnly(2026, 10, 31), DateOnly.FromDateTime(captured.To!.Value.DateTime));
        Assert.True(captured.To!.Value > midnight, "Bitiş gün sonuna çekilmedi.");
        Assert.True(captured.To!.Value < midnight.AddDays(1), "Bitiş ertesi güne taştı.");
    }

    /// <summary>Saat ZATEN verilmisse dokunulmaz: kullanici bilerek daraltmis olabilir.</summary>
    [Fact]
    public async Task AnExplicitTimeIsLeftAlone()
    {
        var noon = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal(noon, (await CapturedAsync(noon)).To);
    }

    /// <summary>Bitis verilmezse yine verilmez.</summary>
    [Fact]
    public async Task AnAbsentEndDateStaysAbsent() =>
        Assert.Null((await CapturedAsync(null)).To);

    private sealed class CapturingRepository : IIncomeRepository
    {
        public IncomeTransactionFilter? Last;

        public Task<PagedResult<IncomeTransactionDetails>> ListTransactionsAsync(IncomeTransactionFilter filter,
            CancellationToken cancellationToken)
        {
            Last = filter;
            return Task.FromResult(new PagedResult<IncomeTransactionDetails>([], 1, 50, 0));
        }

        public Task<IReadOnlyList<IncomeTypeDetails>> ListTypesAsync(bool includeInactive, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IncomeTypeDetails>>([]);
        public Task<IncomeTypeDetails?> GetTypeAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<IncomeTypeDetails?>(null);
        public Task<bool> TypeNameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
        public Task<IncomeTypeDetails> AddTypeAsync(SaveIncomeTypeRequest request, Guid actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IncomeTypeDetails?> UpdateTypeAsync(Guid id, SaveIncomeTypeRequest request, Guid actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> DeactivateTypeAsync(Guid id, Guid actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IncomeTransactionDetails> CreateTransactionAsync(CreateIncomeTransactionRequest request, Guid actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IncomeTransactionDetails?> VoidTransactionAsync(Guid id, string reason, Guid actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
