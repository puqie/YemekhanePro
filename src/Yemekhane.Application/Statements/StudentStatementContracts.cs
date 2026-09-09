namespace Yemekhane.Application.Statements;

/// <summary>Ekstre satirinin hangi defterden geldigi; PDF ve ekran bolumleri buna gore ayrilir.</summary>
public static class StatementSections
{
    public const string Payment = "Payment";
    public const string Balance = "Balance";
    public const string Tuition = "Tuition";
    public const string Meal = "Meal";

    public static string Label(string? section) => section switch
    {
        Balance => "Bakiye hareketleri",
        Tuition => "Ücret ve taksitler",
        Meal => "Yemek kullanımı ve hakedişler",
        _ => "Ödemeler"
    };
}

/// <summary>
/// Ekstrenin tek satiri. Dort ayri defter (tahsilat, bakiye, taksit, yemek) ayni sekle
/// indirgenir ki tek tabloda tarih sirasiyla okunabilsin.
/// </summary>
/// <param name="Amount">Para satirlarinda tutar; yemek satirlarinda 0.</param>
/// <param name="Quantity">Yemek satirlarinda adet; para satirlarinda 0.</param>
public sealed record StatementLine(
    string Section,
    DateTimeOffset OccurredAt,
    string Title,
    string? Detail,
    decimal Amount,
    int Quantity,
    string? Status,
    bool IsCancelled);

/// <summary>Bir bolumun ozeti; PDF'te bolum basligi altinda gosterilir.</summary>
public sealed record StatementSectionSummary(string Section, string Label, int Count, decimal Total);

/// <summary>
/// Ogrencinin secilen tarih araligindaki tam dokumu: odemeler, bakiye hareketleri,
/// taksit/borc durumu ve yemek kullanimi. Veli "gecen yil ne odedim" diye sordugunda
/// tek belge yeterli olsun diye tek yanitta toplanir.
/// </summary>
public sealed record StudentStatement(
    Guid StudentId,
    string StudentNo,
    string StudentName,
    string? ClassName,
    string? SectionName,
    string? ParentName,
    string? ParentPhone,
    DateOnly From,
    DateOnly To,
    decimal TotalPaid,
    decimal TotalVoided,
    decimal BalanceTopUps,
    decimal BalanceSpent,
    decimal CurrentBalance,
    decimal TuitionDue,
    decimal TuitionPaid,
    decimal TuitionOutstanding,
    decimal TuitionOverdue,
    int MealsUsed,
    IReadOnlyList<StatementSectionSummary> Sections,
    IReadOnlyList<StatementLine> Lines);

public sealed record StudentStatementQuery(Guid StudentId, DateOnly From, DateOnly To);

public interface IStudentStatementRepository
{
    Task<StudentStatement?> BuildAsync(StudentStatementQuery query, DateOnly today, CancellationToken cancellationToken);
}

/// <summary>Ekstrenin PDF ciktisi; rapor PDF'leriyle ayni font ve okul basligini kullanir.</summary>
public interface IStudentStatementPdfService
{
    Task GenerateAsync(StudentStatement statement, Stream output, CancellationToken cancellationToken = default);
}
