namespace Yemekhane.Domain.Entities;

/// <summary>
/// Ogrencinin on odemeli TL bakiyesinin defter (ledger) satiri. Eski programdaki
/// "TL Bakiye Yukleme" sekmesinin karsiligi: bakiye ayri bir sutunda tutulmaz,
/// satirlarin toplamidir. Boylece her yukleme/dusum/iade kimin, ne zaman, hangi
/// islem (gelir kaydi ya da gecis kaydi) nedeniyle yaptigiyla birlikte izlenir.
///
/// AmountCents kurus cinsindendir: yukleme (+), dusum (−), iade (+/−), duzeltme (+/−).
/// ExpiresOn yalnizca yuklemede anlamlidir: bu tarihten sonra o yuklemenin
/// harcanmamis kalani gecis kararinda kullanilamaz (bkz. BalanceLedger).
///
/// Ayri dosya: Entities.cs kullanicinin uzerinde calistigi dosyadir; oraya dokunulmadi.
/// </summary>
public sealed class StudentBalanceEntry : Entity
{
    public Guid StudentId { get; set; }
    public long AmountCents { get; set; }
    /// <summary>TopUp, Deduction, Refund, Adjustment (bkz. StudentBalanceEntryKinds).</summary>
    public required string Kind { get; set; }
    /// <summary>Satiri dogurmus kaydin turu: IncomeTransaction (yukleme/iade) ya da AccessLog (dusum).</summary>
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public Guid? CreatedBy { get; set; }
}
