using System.Globalization;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Application.Settings;

namespace Yemekhane.Application.Cards;

// Otomatik SMS kancasi ve kart ucreti bagimliliklari istege baglidir: `new CardService(repo, clock)`
// kuran testler etkilenmez; ucret servisleri verilmezse ucret hic tahsil edilmez.
public sealed class CardService(ICardRepository repository, TimeProvider timeProvider,
    Yemekhane.Application.Sms.ISmsAutomationTrigger? smsAutomation = null,
    ISettingsService? settings = null, IncomeService? income = null)
{
    public async Task<CardDetails> AssignAsync(Guid studentId, AssignCardRequest request, CancellationToken cancellationToken = default)
    {
        var card = await repository.AssignAsync(studentId, NormalizeCardNumber(request.CardNumber), NormalizePrintedNumber(request.PrintedNumber), timeProvider.GetUtcNow(), cancellationToken);
        // Kayit basarisindan SONRA; kanca hata yutar, kart islemi geri alinmaz.
        if (smsAutomation is not null) await smsAutomation.CardChangedAsync(card, replaced: false, cancellationToken);
        return card;
    }

    public async Task<CardDetails> ReplaceAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default) =>
        (await ReplaceWithFeeAsync(studentId, request, Guid.Empty, cancellationToken)).Card;

    /// <summary>
    /// Kart degisimi ve (istenirse) KART UCRETI tahsilati. Saha: "ogrenci kartini tekrardan
    /// cikardiginda biz kart ucreti aliyoruz, bunun islenmesi gerekiyor."
    ///
    /// SIRA onemlidir: ONCE kart degisir, SONRA ucret yazilir. Ters sirada, kart degisimi
    /// basarisiz olursa veliden alinmamis para kasada gorunurdu. Ucret yazilamazsa kart geri
    /// ALINMAZ (kart fiziksel olarak verildi); sonuc <see cref="ReplaceCardResult.FeeWarning"/>
    /// ile bildirilir ve kullanici ucreti Kasa'dan elle girer.
    /// </summary>
    public async Task<ReplaceCardResult> ReplaceWithFeeAsync(Guid studentId, ReplaceCardRequest request,
        Guid actorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new RequestValidationException("Kart değiştirme nedeni zorunludur.");
        var fee = request.ChargeFee ? await ResolveFeeAsync(request.FeeAmount, cancellationToken) : null;

        var card = await repository.ReplaceAsync(studentId, NormalizeCardNumber(request.CardNumber), NormalizePrintedNumber(request.PrintedNumber), request.Reason.Trim(), timeProvider.GetUtcNow(), cancellationToken);
        if (smsAutomation is not null) await smsAutomation.CardChangedAsync(card, replaced: true, cancellationToken);
        if (fee is not (var amount, var incomeTypeId)) return new ReplaceCardResult(card);

        try
        {
            await income!.RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), studentId, card.CardNumber,
                timeProvider.GetUtcNow(), incomeTypeId, amount,
                $"Kart ücreti · {card.CardNumber} · {request.Reason.Trim()}"), actorId, cancellationToken);
            return new ReplaceCardResult(card, amount,
                $"Kart ücreti {amount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"))} ₺ kasaya işlendi.");
        }
        catch (Exception exception) when (exception is RequestValidationException or EntityNotFoundException or EntityConflictException)
        {
            return new ReplaceCardResult(card, null, null,
                $"Kart değişti ama kart ücreti kasaya işlenemedi: {exception.Message} Ücreti Kasa > Gelir Ekle'den elle girin.");
        }
    }

    /// <summary>
    /// O islemde tahsil edilecek tutari ve gelir turunu belirler. Tutar verilmezse Ayarlar'daki
    /// varsayilan kullanilir. Ayar eksikse (tutar 0 ya da gelir turu secilmemis) ucret SESSIZCE
    /// atlanmaz, kullaniciya soylenir: aksi halde para alindi saniliyor ama kayit olmuyordu.
    /// </summary>
    private async Task<(decimal Amount, Guid IncomeTypeId)?> ResolveFeeAsync(decimal? requested, CancellationToken cancellationToken)
    {
        if (settings is null || income is null)
            throw new RequestValidationException("Kart ücreti bu sunucuda tahsil edilemiyor.");
        var configured = (await settings.GetAsync(cancellationToken)).CardFee;
        if (configured.IncomeTypeId is not { } incomeTypeId)
            throw new RequestValidationException("Kart ücreti için Ayarlar > Kart Ücreti bölümünden bir gelir türü seçin.");
        var amount = requested ?? configured.Amount;
        if (amount <= 0m || decimal.Round(amount, 2) != amount)
            throw new RequestValidationException("Kart ücreti sıfırdan büyük ve en fazla iki ondalıklı olmalıdır.");
        return (amount, incomeTypeId);
    }

    public async Task<CardDetails> FindAsync(string cardNumber, CancellationToken cancellationToken = default) =>
        await repository.FindByNumberAsync(NormalizeCardNumber(cardNumber), cancellationToken)
        ?? throw new EntityNotFoundException("Kart sistemde kayıtlı değil.");

    public Task<IReadOnlyList<CardDetails>> GetHistoryAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        repository.GetHistoryAsync(studentId, cancellationToken);

    public async Task DeactivateAsync(Guid cardId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new RequestValidationException("Kart pasifleştirme nedeni zorunludur.");
        if (!await repository.DeactivateAsync(cardId, reason.Trim(), timeProvider.GetUtcNow(), cancellationToken))
            throw new EntityNotFoundException("Aktif kart bulunamadı.");
    }

    /// <summary>
    /// Ogrencinin en son pasife dusen kartini geri acar. Saha: kart yanlislikla degistirilip
    /// pasife dusunce turnike "Kart pasif" diyordu; programda geri acacak yer yoktu ve numara
    /// tekil oldugu icin yeniden de atanamiyordu. SMS kancasi calismaz: veli karti zaten bilir.
    /// </summary>
    public async Task<CardDetails> ReactivateAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        await repository.ReactivateLatestAsync(studentId, timeProvider.GetUtcNow(), cancellationToken)
        ?? throw new EntityNotFoundException("Öğrencinin geri açılacak pasif kartı yok.");

    /// <summary>Kartlar ekrani: SECILEN pasif karti (kimligiyle) geri acar; kart yoksa ya da zaten aktifse 404.</summary>
    public async Task<CardDetails> ReactivateCardAsync(Guid cardId, CancellationToken cancellationToken = default) =>
        await repository.ReactivateAsync(cardId, timeProvider.GetUtcNow(), cancellationToken)
        ?? throw new EntityNotFoundException("Geri açılacak pasif kart bulunamadı.");
    /// <summary>Aktif kartin baski numarasini gunceller; kart degismez.</summary>
    public async Task<CardDetails> SetPrintedNumberAsync(Guid studentId, SetPrintedNumberRequest request, CancellationToken cancellationToken = default) =>
        await repository.SetPrintedNumberAsync(studentId, NormalizePrintedNumber(request.PrintedNumber), timeProvider.GetUtcNow(), cancellationToken)
        ?? throw new EntityNotFoundException("Öğrencinin aktif kartı bulunamadı.");

    private static string? NormalizePrintedNumber(string? printedNumber)
    {
        var normalized = printedNumber?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > 32) throw new RequestValidationException("Baskı No en fazla 32 karakter olabilir.");
        return normalized;
    }

    private static string NormalizeCardNumber(string cardNumber)
    {
        var normalized = cardNumber?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 128) throw new RequestValidationException("Kart No 1-128 karakter olmalıdır.");
        return normalized;
    }
}
