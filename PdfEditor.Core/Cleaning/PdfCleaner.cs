using MuPDF.NET;

namespace PdfEditor.Core.Cleaning;

public sealed class PasswordRequiredException : Exception
{
    public PasswordRequiredException(string path) : base($"{Path.GetFileName(path)} şifreli.") => FilePath = path;

    public string FilePath { get; }
}

public sealed record CleanSummary(int Pages, int Watermarks, int Images, int Texts, int Replaced);

/// <summary>Applies selected candidates and text replacements to PDF documents.</summary>
public static class PdfCleaner
{
    /// <summary>MuPDF is not thread safe; every engine call from the UI goes through this lock.</summary>
    public static readonly object Sync = new();

    private static readonly string[] RegularFonts =
    {
        @"C:\Windows\Fonts\arial.ttf",
        @"C:\Windows\Fonts\segoeui.ttf",
    };

    private static readonly string[] BoldFonts =
    {
        @"C:\Windows\Fonts\arialbd.ttf",
        @"C:\Windows\Fonts\segoeuib.ttf",
    };

    private const double RedactShrink = 0.2;

    public static string? DefaultFont(bool bold = false) =>
        (bold ? BoldFonts.Concat(RegularFonts) : RegularFonts).FirstOrDefault(File.Exists);

    public static Document Open(string path, string? password = null)
    {
        var doc = new Document(path);
        if (doc.NeedsPass && (string.IsNullOrEmpty(password) || doc.Authenticate(password) == 0))
        {
            doc.Close();
            throw new PasswordRequiredException(path);
        }
        return doc;
    }

    public static Document OpenBytes(byte[] data) => new(data, "pdf");

    public static void Save(Document doc, string path)
    {
        using var culture = new InvariantCultureScope();
        doc.Save(path, garbage: 4, deflate: 1);
    }

    private sealed class Fonts
    {
        private readonly string? _custom;
        private readonly Dictionary<string, (string Name, Font Font)> _fonts = new();

        public Fonts(string? custom) => _custom = string.IsNullOrWhiteSpace(custom) ? null : custom;

        public (string Path, string Name, Font Font) Get(bool bold)
        {
            var path = _custom ?? DefaultFont(bold)
                ?? throw new FileNotFoundException("Türkçe karakter destekleyen bir TTF font bulunamadı; font dosyası seçin.");
            if (!_fonts.TryGetValue(path, out var entry))
            {
                entry = ($"pdfedit{_fonts.Count}", new Font(fontFile: path));
                _fonts[path] = entry;
            }
            return (path, entry.Name, entry.Font);
        }
    }

    private static void SetPageContent(Document doc, Page page, byte[] data)
    {
        var xref = doc.GetNewXref();
        doc.UpdateObject(xref, "<<>>");
        doc.UpdateStream(xref, data);
        doc.XrefSetKey(page.Xref, "Contents", $"{xref} 0 R");
    }

    private static Box RedactRect(Box rect)
    {
        var inset = rect.Height * RedactShrink;
        return new Box(rect.X0, rect.Y0 + inset, rect.X1, rect.Y1 - inset);
    }

    private static void Write(Page page, TextHit hit, string text, Fonts fonts)
    {
        var (path, name, font) = fonts.Get(hit.Bold);
        var width = font.TextLength(text, (float)hit.Size);
        var x = hit.Align switch
        {
            TextAlign.Right => hit.Rect.X1 - width,
            TextAlign.Center => (hit.Rect.X0 + hit.Rect.X1 - width) / 2,
            _ => hit.Rect.X0,
        };
        page.InsertText(new Point((float)x, (float)hit.Baseline), text, fontSize: (float)hit.Size,
            fontName: name, fontFile: path, color: hit.Color, fill: hit.Color);
    }

    /// <summary>Applies the selected candidates and (old, new) text replacements in place.</summary>
    public static CleanSummary Apply(Document doc, IEnumerable<Candidate> candidates,
        IEnumerable<(string Old, string New)>? replacements = null, string? fontFile = null)
    {
        using var culture = new InvariantCultureScope();
        var selected = candidates.Where(c => c.Selected).ToList();

        foreach (var cand in selected)
            foreach (var (pno, xref) in cand.Annots)
            {
                var page = doc[pno];
                if (page.LoadAnnot(xref) is { } annot)
                    page.DeleteAnnot(annot);
            }

        foreach (var group in selected.SelectMany(c => c.Ops).GroupBy(o => o.Container))
        {
            var indices = group.Select(o => o.Index).ToList();
            if (group.Key.IsPage)
            {
                var page = doc[group.Key.Id];
                SetPageContent(doc, page, ContentStream.Neutralize(ContentWalker.PageContent(doc, page), indices));
            }
            else
            {
                var xref = group.Key.Id;
                doc.UpdateStream(xref, ContentStream.Neutralize(doc.XrefStream(xref) ?? Array.Empty<byte>(), indices));
            }
        }

        var edits = new Dictionary<int, Dictionary<Box, (TextHit Hit, string New)>>();
        void AddEdit(int pno, TextHit hit, string text)
        {
            var key = new Box(Math.Round(hit.Rect.X0, 1), Math.Round(hit.Rect.Y0, 1), Math.Round(hit.Rect.X1, 1), Math.Round(hit.Rect.Y1, 1));
            (edits.TryGetValue(pno, out var map) ? map : edits[pno] = new())[key] = (hit, text);
        }
        foreach (var cand in selected.Where(c => c.Group == CandidateGroup.Text))
            foreach (var (pno, hits) in cand.Hits)
                foreach (var hit in hits)
                    AddEdit(pno, hit, cand.Replacement);
        var replaced = 0;
        foreach (var (oldText, newText) in replacements ?? Enumerable.Empty<(string, string)>())
            for (var pno = 0; pno < doc.PageCount; pno++)
                foreach (var hit in Analyzer.FindText(doc[pno], oldText))
                {
                    AddEdit(pno, hit, newText);
                    replaced++;
                }

        var fonts = new Fonts(fontFile);
        foreach (var (pno, items) in edits)
        {
            var page = doc[pno];
            foreach (var (hit, _) in items.Values)
                page.AddRedactAnnot(RedactRect(hit.Rect).ToRect(), fillColor: null, crossOut: false);
            page.ApplyRedactions(images: 0, graphics: 0, text: 0);
            foreach (var (hit, text) in items.Values)
                if (!string.IsNullOrEmpty(text))
                    Write(page, hit, text, fonts);
        }

        return new CleanSummary(doc.PageCount,
            selected.Count(c => c.Group == CandidateGroup.Watermark),
            selected.Count(c => c.Group == CandidateGroup.Image),
            selected.Count(c => c.Group == CandidateGroup.Text),
            replaced);
    }

    /// <summary>Selects candidates matching the selection made on another document.</summary>
    public static void SelectLike(IEnumerable<Candidate> candidates, IEnumerable<Candidate> reference)
    {
        var refList = reference.ToList();
        var chosen = refList.Where(c => c.Selected).GroupBy(c => c.Signature).ToDictionary(g => g.Key, g => g.First());
        var rejected = refList.Where(c => !c.Selected).Select(c => c.Signature).ToHashSet();
        foreach (var cand in candidates)
        {
            if (chosen.TryGetValue(cand.Signature, out var match))
            {
                cand.Selected = true;
                cand.Replacement = match.Replacement;
            }
            else
            {
                cand.Selected = cand.Certain && !rejected.Contains(cand.Signature);
            }
        }
    }

    /// <summary>Analyzes <paramref name="src"/>, lets <paramref name="choose"/> adjust the selection, applies and saves.</summary>
    public static CleanSummary ProcessFile(string src, string dst, Action<List<Candidate>>? choose = null,
        IEnumerable<(string Old, string New)>? replacements = null, string? fontFile = null, string? password = null)
    {
        var doc = Open(src, password);
        try
        {
            var candidates = Analyzer.Analyze(doc);
            choose?.Invoke(candidates);
            var summary = Apply(doc, candidates, replacements, fontFile);
            Save(doc, dst);
            return summary;
        }
        finally
        {
            doc.Close();
        }
    }
}
