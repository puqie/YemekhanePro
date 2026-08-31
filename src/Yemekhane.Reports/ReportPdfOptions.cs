namespace Yemekhane.Reports;

public sealed class ReportPdfOptions
{
    public const int MaximumBatchSize = 200;

    public string SchoolName { get; set; } = "Okul Yemekhanesi";
    public int BatchSize { get; set; } = MaximumBatchSize;
}
