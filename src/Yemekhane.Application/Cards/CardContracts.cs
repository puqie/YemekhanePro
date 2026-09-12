namespace Yemekhane.Application.Cards;

/// <summary>
/// PrintedNumber: kartin ON yuzune basili kisa numara (orn. 6296). Cipin numarasi (CardNumber)
/// DEGILDIR; okul kartlarinda iki numara birden yazar ve kayip kart bulununca sahibini bulmak
/// icin bu aranir. Istege baglidir.
/// </summary>
public sealed record CardDetails(Guid Id, Guid StudentId, string StudentNo, string StudentName, string CardNumber,
    DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string? ReplacementReason, bool IsActive, string? PrintedNumber = null);

public sealed record AssignCardRequest(string CardNumber, string? PrintedNumber = null);
public sealed record ReplaceCardRequest(string CardNumber, string Reason, string? PrintedNumber = null);
public sealed record SetPrintedNumberRequest(string? PrintedNumber);

public interface ICardRepository
{
    /// <summary>Cip numarasi ya da (aktif kartlarda) baski numarasi ile bulur.</summary>
    Task<CardDetails?> FindByNumberAsync(string cardNumber, CancellationToken cancellationToken);
    Task<IReadOnlyList<CardDetails>> GetHistoryAsync(Guid studentId, CancellationToken cancellationToken);
    Task<CardDetails> AssignAsync(Guid studentId, string cardNumber, string? printedNumber, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    Task<CardDetails> ReplaceAsync(Guid studentId, string cardNumber, string? printedNumber, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    /// <summary>Aktif kartin baski numarasini gunceller; aktif kart yoksa null.</summary>
    Task<CardDetails?> SetPrintedNumberAsync(Guid studentId, string? printedNumber, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid cardId, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    /// <summary>
    /// Ogrencinin EN SON pasife dusen kartini yeniden aktif eder; pasif karti yoksa null.
    /// Aktif karti varsa EntityConflictException: bir ogrencide tek aktif kart olur.
    /// </summary>
    Task<CardDetails?> ReactivateLatestAsync(Guid studentId, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    /// <summary>
    /// Kartlar ekrani: SECILEN pasif karti kimligiyle geri acar. Kart yoksa ya da zaten aktifse null;
    /// ogrencinin baska aktif karti varsa EntityConflictException.
    /// </summary>
    Task<CardDetails?> ReactivateAsync(Guid cardId, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
}

/// <summary>Kartlar ekraninin tek satiri: ogrenci kimligi + kartin durumu ve gecerlilik araligi.</summary>
/// <param name="StudentActive">Pasif/silinmis ogrencinin karti aktif olsa da turnikeden gecemez; ekranda soylenir.</param>
public sealed record CardListRow(Guid CardId, Guid StudentId, string StudentNo, string StudentName, string? ClassName,
    string CardNumber, string? PrintedNumber, bool IsActive, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo,
    string? ReplacementReason, bool StudentActive);

/// <param name="IsActive">null = tumu, true = yalnizca aktif, false = yalnizca pasif.</param>
public sealed record CardListQuery(string? Search = null, bool? IsActive = null, int Page = 1, int PageSize = 50)
{
    public const int MaximumPageSize = 200;
}

/// <param name="ActiveCount">Suzgecten BAGIMSIZ toplam aktif kart; baslik ozetinde gosterilir.</param>
public sealed record CardListResult(IReadOnlyList<CardListRow> Items, int Page, int PageSize, int TotalCount,
    int ActiveCount, int PassiveCount);

/// <summary>Kartlar ekraninin liste sorgusu; depodan ayri tutulur (salt okunur, sayfali).</summary>
public interface ICardListQuery
{
    Task<CardListResult> ListAsync(CardListQuery query, CancellationToken cancellationToken);
}
