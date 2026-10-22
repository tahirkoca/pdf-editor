namespace PdfEditor.Core.Html;

/// <summary>
/// PDF çıktı ayarları (sayfa boyutu, kenar boşluğu, yön).
/// </summary>
public class PdfConversionOptions
{
    /// <summary>Sayfa formatı: A4, Letter, A3, vb.</summary>
    public string PaperFormat { get; set; } = "A4";

    /// <summary>Kenar boşluğu (cm).</summary>
    public double MarginCm { get; set; } = 1.0;

    /// <summary>Yatay (landscape) sayfa.</summary>
    public bool Landscape { get; set; }
}
