namespace Yemekhane.Reports;

public sealed class ReportExcelOptions
{
    public const int ExcelMaximumRows = 1_048_576;

    public string SchoolName { get; set; } = "Okul Yemekhanesi";
    public int BatchSize { get; set; } = ReportService.MaximumPageSize;
    public int MaximumRowsPerSheet { get; set; } = ExcelMaximumRows;
}
