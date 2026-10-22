using System.Text;
using MuPDF.NET;
using PdfEditor.Core.Cleaning;

namespace PdfEditor.Tests;

/// <summary>Synthetic PDFs, one per watermark style.</summary>
public static class Samples
{
    public const int Pages = 3;
    public const string Question = "Soru {0}: Bir üçgenin iç açıları toplamı kaç derecedir?";

    private static string Font => PdfCleaner.DefaultFont() ?? throw new InvalidOperationException("Arial bulunamadı");

    private static Document Base(string? header = null, float[]? headerBackground = null)
    {
        using var culture = new InvariantCultureScope();
        var doc = new Document();
        for (var n = 1; n <= Pages; n++)
        {
            var page = doc.NewPage();
            if (headerBackground is [var r, var g, var b])
                AppendContent(doc, page, FormattableString.Invariant($"q {r} {g} {b} rg 60 794 240 23 re f Q\n"));
            page.InsertText(new Point(72, 150), string.Format(Question, n), fontSize: 12, fontName: "tr", fontFile: Font);
            if (header != null)
                page.InsertText(new Point(72, 40), header, fontSize: 9, fontName: "tr", fontFile: Font,
                    color: new[] { 0.4f, 0.4f, 0.4f });
        }
        return doc;
    }

    public static Document Reload(Document doc) => new(doc.Write(), "pdf");

    private static void AppendContent(Document doc, Page page, string content)
    {
        var xref = doc.GetNewXref();
        doc.UpdateObject(xref, "<<>>");
        doc.UpdateStream(xref, Encoding.Latin1.GetBytes(content));
        var refs = string.Join(" ", page.GetContents().Select(x => $"{x} 0 R").Append($"{xref} 0 R"));
        doc.XrefSetKey(page.Xref, "Contents", $"[{refs}]");
    }

    public static Document AnnotationWatermark()
    {
        var doc = Base();
        for (var i = 0; i < doc.PageCount; i++)
        {
            var annot = doc[i].AddFreeTextAnnot(new Rect(150, 350, 450, 450), "TASLAK", fontSize: 40);
            doc.XrefSetKey(annot.Xref, "Subtype", "/Watermark");
        }
        return Reload(doc);
    }

    public static Document ArtifactWatermark()
    {
        var doc = Base();
        for (var i = 0; i < doc.PageCount; i++)
        {
            var page = doc[i];
            page.InsertText(new Point(0, 0), " ", fontName: "helv");
            AppendContent(doc, page,
                "/Artifact <</Type /Pagination /Subtype /Watermark>> BDC\n" +
                "q 0.8 g BT /helv 60 Tf 1 0 0 1 150 400 Tm (GIZLI) Tj ET Q\nEMC\n");
        }
        return Reload(doc);
    }

    public static Document LayerWatermark()
    {
        var doc = Base();
        var ocg = doc.AddOcg("Watermark", on: true);
        for (var i = 0; i < doc.PageCount; i++)
            doc[i].InsertText(new Point(120, 450), "FILIGRAN", fontSize: 50, oc: ocg);
        return Reload(doc);
    }

    public static Document FaintRotatedText()
    {
        var doc = Base();
        for (var i = 0; i < doc.PageCount; i++)
        {
            var point = new Point(150, 600);
            doc[i].InsertText(point, "KOPYA", fontSize: 70, color: new[] { 0.5f, 0.5f, 0.5f },
                morphFix: point, morphMat: new Matrix(40f), fillOpacity: 0.15f);
        }
        return Reload(doc);
    }

    public static Document RepeatedLogo()
    {
        var doc = Base();
        var pix = new Pixmap(new Colorspace(Utils.CS_RGB), new IRect(0, 0, 20, 20), false);
        pix.SetRect(pix.IRect, new float[] { 220, 40, 40 });
        var png = pix.ToBytes("png");
        for (var i = 0; i < doc.PageCount; i++)
            doc[i].InsertImage(new Rect(500, 20, 520, 40), stream: png);
        return Reload(doc);
    }

    public static Document RepeatedHeader(float[]? background = null) =>
        Reload(Base(header: "Okul Adı Lisesi", headerBackground: background));

    public static Document Clean() => Reload(Base());
}
