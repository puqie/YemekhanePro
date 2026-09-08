namespace Yemekhane.Application.Calendar;

public sealed record HolidayScopeRequest(string ScopeType, Guid? ScopeId = null);

/// <param name="EndDate">
/// Aralikli tatil (bayram, yariyil): dahil son gun. Bos ise tek gun. Her gun ayri satir
/// olarak yazilir ve ayni <c>GroupId</c> ile baglanir; boylece gun bazli kapali-gun sorgusu
/// degismeden kalir, silme ise tek gun ya da tum aralik olarak yapilabilir.
/// </param>
public sealed record CreateHolidayRequest(DateOnly Date, string Name, string HolidayType, string? Description,
    string TransferBehavior, IReadOnlyCollection<HolidayScopeRequest> Scopes, DateOnly? EndDate = null)
{
    public DateOnly LastDate => EndDate is { } end && end > Date ? end : Date;
    public int DayCount => LastDate.DayNumber - Date.DayNumber + 1;
}

/// <param name="GroupId">Aralikli tatilin ortak kimligi; tek gunluk tatilde null.</param>
/// <param name="DayCount">Olusturmada yazilan gun sayisi; listelemede her satir 1'dir.</param>
public sealed record HolidayDetails(Guid Id, DateOnly Date, string Name, string HolidayType, string? Description,
    string TransferBehavior, IReadOnlyCollection<HolidayScopeRequest> Scopes, Guid? GroupId = null, int DayCount = 1);

public interface IHolidayRepository : ICalendarClosureProvider
{
    /// <summary>Aralik icin her gune bir satir yazar; ilk gunun kaydini <c>DayCount</c> ile doner.</summary>
    Task<HolidayDetails> CreateAsync(CreateHolidayRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<HolidayDetails>> ListAsync(DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken);
    /// <summary>Tek satiri ya da (<paramref name="wholeGroup"/>) ayni gruptaki tum gunleri siler; silinen satir sayisini doner.</summary>
    Task<int> DeleteAsync(Guid id, bool wholeGroup, CancellationToken cancellationToken);
}
