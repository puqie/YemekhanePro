namespace Yemekhane.Application.Balances;

/// <summary>Defter satirinin hesap icin gereken kismi. OccurredOn: satirin Istanbul takvim gunu.</summary>
public readonly record struct LedgerLine(
    Guid Id,
    long AmountCents,
    string Kind,
    DateTimeOffset OccurredAt,
    DateOnly OccurredOn,
    DateOnly? ExpiresOn,
    Guid? ReferenceId);

/// <summary>TotalCents = tum satirlar; AvailableCents = AsOf gunu harcanabilir; ExpiredCents = yanmis kalan.</summary>
public readonly record struct LedgerTotals(long TotalCents, long AvailableCents, long ExpiredCents);

/// <summary>
/// Bakiye hesabi. Toplam bakiye satirlarin duz toplamidir; ama eski programdaki "bitis
/// tarihi" icin yuklemeler PARTI (lot) olarak izlenir: her dusum, o gun gecerli olan en
/// eski yuklemeden dusulur (FIFO). Bitis tarihi gecen bir yuklemenin harcanmamis kalani
/// "yanmis" sayilir ve kullanilabilir bakiyeden dusulur.
///
/// Neden FIFO: iki yukleme (biri suresiz, biri ay sonuna kadar) ve arada dusumler varken
/// "hangi para harcandi" sorusunun tek deterministik cevabi budur. Duz "suresi gecenleri
/// toplamdan cikar" yaklasimi, harcanmis bir yuklemeyi ikinci kez dusup bakiyeyi eksiye
/// cekiyordu.
///
/// Iptal (void) iadesi kendi yuklemesini hedefler: ReferenceId ile eslesen parti azaltilir;
/// parti zaten harcandiysa fark toplam bakiyeyi eksiye tasir (kasa iptalinde bu uyarilir).
/// </summary>
public static class BalanceLedger
{
    public static LedgerTotals Compute(IEnumerable<LedgerLine> lines, DateOnly asOf)
    {
        var ordered = lines.OrderBy(x => x.OccurredAt).ThenBy(x => x.AmountCents < 0).ToList();
        var lots = new List<Lot>();
        long total = 0, overdraft = 0;
        foreach (var line in ordered)
        {
            total += line.AmountCents;
            if (line.AmountCents >= 0)
            {
                lots.Add(new Lot(line.ReferenceId, line.AmountCents, line.ExpiresOn));
                continue;
            }

            var owed = -line.AmountCents;
            if (line.Kind == StudentBalanceEntryKinds.Refund && line.ReferenceId is { } reference)
            {
                // Yuklemenin iptali: once o yuklemenin kendi kalanindan duser.
                var own = lots.FirstOrDefault(x => x.ReferenceId == reference);
                if (own is not null) owed -= own.Take(owed);
            }
            foreach (var lot in lots)
            {
                if (owed == 0) break;
                if (lot.ExpiresOn is { } expires && expires < line.OccurredOn) continue;
                owed -= lot.Take(owed);
            }
            overdraft += owed;
        }

        var expired = lots.Where(x => x.ExpiresOn is { } e && e < asOf).Sum(x => x.Remaining);
        var available = lots.Where(x => x.ExpiresOn is null || x.ExpiresOn >= asOf).Sum(x => x.Remaining) - overdraft;
        return new LedgerTotals(total, available, expired);
    }

    private sealed class Lot(Guid? referenceId, long remaining, DateOnly? expiresOn)
    {
        public Guid? ReferenceId { get; } = referenceId;
        public long Remaining { get; private set; } = remaining;
        public DateOnly? ExpiresOn { get; } = expiresOn;

        public long Take(long amount)
        {
            var taken = Math.Min(Remaining, amount);
            Remaining -= taken;
            return taken;
        }
    }
}
