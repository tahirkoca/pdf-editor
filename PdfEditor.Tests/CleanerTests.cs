using MuPDF.NET;
using PdfEditor.Core.Cleaning;
using Xunit;

namespace PdfEditor.Tests;

public class WatermarkStyleTests
{
    internal static string AllText(Document doc) =>
        string.Concat(Enumerable.Range(0, doc.PageCount).Select(i => doc[i].GetText())).Replace('\u00a0', ' ');

    private static Document AssertRemoved(Document doc, string word, string signatureKind)
    {
        var candidates = Analyzer.Analyze(doc);
        var marks = candidates.Where(c => c.Group == CandidateGroup.Watermark && c.Signature.Split('|')[0] == signatureKind).ToList();
        Assert.True(marks.Count == 1, string.Join("; ", candidates.Select(c => c.Label)));
        Assert.True(marks[0].Certain);
        Assert.True(marks[0].Selected);
        Assert.Equal(Enumerable.Range(0, Samples.Pages), marks[0].Pages);
        PdfCleaner.Apply(doc, candidates);
        doc = Samples.Reload(doc);
        Assert.DoesNotContain(word, AllText(doc));
        Assert.Contains("Soru 2:", AllText(doc));
        return doc;
    }

    [Fact]
    public void Annotation()
    {
        var doc = AssertRemoved(Samples.AnnotationWatermark(), "TASLAK", "annot");
        Assert.All(Enumerable.Range(0, doc.PageCount), i => Assert.Empty(doc[i].AnnotXrefs()));
    }

    [Fact]
    public void Artifact() => AssertRemoved(Samples.ArtifactWatermark(), "GIZLI", "artifact");

    [Fact]
    public void Layer() => AssertRemoved(Samples.LayerWatermark(), "FILIGRAN", "layer");

    [Fact]
    public void FaintRotatedText() => AssertRemoved(Samples.FaintRotatedText(), "KOPYA", "faint");
}

public class RepeatedItemTests
{
    [Fact]
    public void RepeatedLogo()
    {
        var doc = Samples.RepeatedLogo();
        var candidates = Analyzer.Analyze(doc);
        var image = Assert.Single(candidates, c => c.Group == CandidateGroup.Image);
        Assert.False(image.Selected);
        image.Selected = true;
        PdfCleaner.Apply(doc, candidates);
        doc = Samples.Reload(doc);
        Assert.All(Enumerable.Range(0, doc.PageCount), i => Assert.Empty(doc[i].GetImageInfo(xrefs: true)));
        Assert.Contains("Soru 3:", WatermarkStyleTests.AllText(doc));
    }

    [Fact]
    public void RepeatedHeaderReplace()
    {
        var doc = Samples.RepeatedHeader();
        var candidates = Analyzer.Analyze(doc);
        var header = Assert.Single(candidates, c => c.Text == "Okul Adı Lisesi");
        Assert.False(header.Selected);
        header.Selected = true;
        header.Replacement = "Benim Okulum";
        PdfCleaner.Apply(doc, candidates);
        doc = Samples.Reload(doc);
        for (var i = 0; i < doc.PageCount; i++)
        {
            var hit = Assert.Single(Analyzer.FindText(doc[i], "Benim Okulum"));
            Assert.Equal(new[] { 0.4f, 0.4f, 0.4f }, hit.Color.Select(c => MathF.Round(c, 1)));
            Assert.Equal(9, Math.Round(hit.Size));
            Assert.Empty(Analyzer.FindText(doc[i], "Okul Adı Lisesi"));
        }
        Assert.Contains("Soru 1:", WatermarkStyleTests.AllText(doc));
    }

    [Fact]
    public void ReplacementKeepsColoredBackground()
    {
        var blue = new[] { 0.2f, 0.4f, 0.8f };
        var doc = Samples.RepeatedHeader(blue);
        PdfCleaner.Apply(doc, Array.Empty<Candidate>(), new[] { ("Okul Adı Lisesi", "") });
        doc = Samples.Reload(doc);
        Assert.Empty(Analyzer.FindText(doc[0], "Okul Adı Lisesi"));
        var pix = doc[0].GetPixmap(matrix: new Matrix(1f, 1f));
        var (r, g, b) = Pixel(pix, 110, 36);
        Assert.True(b > 150 && r < 100, $"Arka plan rengi bozuldu: {r},{g},{b}");
    }

    [Fact]
    public void ManualReplace()
    {
        var doc = Samples.Clean();
        var summary = PdfCleaner.Apply(doc, Array.Empty<Candidate>(), new[] { ("iç açıları", "dış açıları") });
        Assert.Equal(Samples.Pages, summary.Replaced);
        doc = Samples.Reload(doc);
        for (var i = 0; i < doc.PageCount; i++)
            Assert.Single(Analyzer.FindText(doc[i], "dış açıları"));
    }

    [Fact]
    public void CleanDocumentHasNoWatermark()
    {
        var candidates = Analyzer.Analyze(Samples.Clean());
        Assert.DoesNotContain(candidates, c => c.Group == CandidateGroup.Watermark);
        Assert.DoesNotContain(candidates, c => c.Selected);
    }

    private static (int R, int G, int B) Pixel(Pixmap pix, int x, int y)
    {
        var samples = pix.SAMPLES;
        var i = (y * pix.W + x) * pix.N;
        return (samples[i], samples[i + 1], samples[i + 2]);
    }
}

/// <summary>Runs on real worksheets when PDFEDITOR_ACCEPTANCE_DIR points to a folder of PDFs.</summary>
public class AcceptanceTests
{
    [SkippableFact]
    public void Worksheets()
    {
        var dir = Environment.GetEnvironmentVariable("PDFEDITOR_ACCEPTANCE_DIR");
        Skip.If(string.IsNullOrEmpty(dir) || !Directory.Exists(dir), "PDFEDITOR_ACCEPTANCE_DIR ayarlı değil");
        foreach (var path in Directory.GetFiles(dir!, "*.pdf"))
        {
            var doc = new Document(path);
            var candidates = Analyzer.Analyze(doc);
            var mark = Assert.Single(candidates, c => c.Group == CandidateGroup.Watermark);
            Assert.True(mark.Selected);
            Assert.Equal(doc.PageCount - 1, mark.Pages.Count);
            var logo = Assert.Single(candidates, c => c.Group == CandidateGroup.Image);
            Assert.All(logo.Boxes.Values.SelectMany(b => b), r => Assert.Equal(15, Math.Round(r.Width)));
            var brand = Assert.Single(candidates, c => c.Text == "COOLMATEMATİK");

            logo.Selected = true;
            brand.Selected = true;
            brand.Replacement = "Ahmet Hoca";
            PdfCleaner.Apply(doc, candidates);
            doc = Samples.Reload(doc);
            Assert.DoesNotContain("COOLMATEMAT", WatermarkStyleTests.AllText(doc));
            Assert.Equal(brand.Hits.Values.Sum(h => h.Count),
                Enumerable.Range(0, doc.PageCount).Sum(i => Analyzer.FindText(doc[i], "Ahmet Hoca").Count));
            Assert.DoesNotContain(Analyzer.Analyze(doc), c => c.Group == CandidateGroup.Watermark);
        }
    }
}
