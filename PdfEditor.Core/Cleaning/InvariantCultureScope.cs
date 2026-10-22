using System.Globalization;

namespace PdfEditor.Core.Cleaning;

/// <summary>
/// MuPDF.NET formats numbers in generated content streams with the current culture, so on a
/// Turkish system colors become "0,2 0,4 0,8 rg", which PDF readers reject. Every call that
/// writes content must run inside this scope.
/// </summary>
public sealed class InvariantCultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public InvariantCultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose() => CultureInfo.CurrentCulture = _culture;
}
