namespace Yemekhane.Application.Devices;

/// <summary>
/// Bir cihazdaki (yuklenmis, bekleyen veya hatali) tek bir kartin operator satiri.
/// Eski programdaki "Cihaz Sicil Listesi" karsiligi; ancak kaynak cihazin bellegi degil,
/// sunucunun kart-cihaz durum tablosudur (<c>device_card_states</c>). Cihazdan dogrudan okuma
/// adaptor katmanina baglidir ve bu listenin kapsami disindadir.
/// </summary>
public sealed record DeviceCardListRow(
    Guid CardId,
    Guid StudentId,
    string StudentNo,
    string StudentName,
    string? ClassName,
    string CardNumber,
    string Status,
    DateTimeOffset? LastSyncedAt,
    int AttemptCount,
    string? LastError);

/// <summary>Cihaz kart listesi sorgusu: arama no / ad soyad / kart numarasinda BASTAN eslesir.</summary>
public sealed record DeviceCardListQuery(Guid DeviceId, string? Search = null, int Page = 1, int PageSize = 50)
{
    public const int MaximumPageSize = 200;
}

public sealed record DeviceCardListResult(
    IReadOnlyList<DeviceCardListRow> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// Cihaz kart listesini okur. <see cref="IDeviceCardSyncService"/>'e eklenmedi: o arayuzun
/// sahte uygulamalari cihaz testlerinde yasiyor ve yalnizca okuma icin genisletmek onlari kirardi.
/// </summary>
public interface IDeviceCardListQuery
{
    Task<DeviceCardListResult> ListAsync(DeviceCardListQuery query, CancellationToken cancellationToken);
}
