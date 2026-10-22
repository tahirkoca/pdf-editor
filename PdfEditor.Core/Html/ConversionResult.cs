namespace PdfEditor.Core.Html;

/// <summary>
/// Tek dosya dönüştürme sonucu (log satırı).
/// </summary>
public record ConversionLogItem(string FilePath, string FileName, bool Success, string Message);

/// <summary>
/// Toplu dönüştürme sonucu.
/// </summary>
public class ConversionResult
{
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public List<ConversionLogItem> Items { get; } = new();
}
