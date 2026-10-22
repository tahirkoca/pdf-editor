using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MuPDF.NET;

namespace PdfEditor.Core.Cleaning;

/// <summary>Finds watermark, repeated image and repeated text candidates in any PDF.</summary>
public static class Analyzer
{
    private static readonly Regex WatermarkLayerRe = new("watermark|filigran|wasserzeichen|filigrane", RegexOptions.IgnoreCase);
    private const double FaintAlpha = 0.5;
    private const double LightColor = 0.6;
    private const double MinRotation = 5.0;
    private const double BigTextSize = 30.0;
    private const double BigImageArea = 0.05;
    private const double BigPathArea = 0.15;
    private const double RepeatRatio = 0.5;
    private const double MarginRatio = 0.15;
    private const double PositionTolerance = 0.05;
    private const double ImageYTolerance = 0.10;
    private const double SizeTolerance = 0.10;
    private const double MaxBackingArea = 6.0;
    internal const int TextFlags = 1 | 2 | 64;

    private static readonly Dictionary<ElementKind, string> KindWords = new()
    {
        [ElementKind.Text] = "yazı",
        [ElementKind.Image] = "görsel",
        [ElementKind.Path] = "şekil",
        [ElementKind.Shading] = "şekil",
    };

    /// <summary>Returns all candidates of <paramref name="doc"/>; certain watermarks come pre-selected.</summary>
    public static List<Candidate> Analyze(Document doc)
    {
        var pages = Enumerable.Range(0, doc.PageCount).Select(i => doc[i]).ToList();
        var elements = pages.ToDictionary(p => p.Number, p => ContentWalker.PageElements(doc, p));
        var used = new HashSet<PdfElement>(ReferenceEqualityComparer.Instance);
        var candidates = new List<Candidate>();
        candidates.AddRange(AnnotationCandidates(doc, pages));
        candidates.AddRange(ArtifactCandidates(doc, pages, elements, used));
        candidates.AddRange(LayerCandidates(doc, pages, elements, used));
        candidates.AddRange(FaintCandidates(doc, pages, elements, used));
        candidates.AddRange(ImageCandidates(doc, pages, elements, used));
        candidates.AddRange(TextCandidates(pages));
        for (var i = 0; i < candidates.Count; i++)
        {
            candidates[i].Id = $"{candidates[i].Group[0]}{i + 1}";
            candidates[i].Selected = candidates[i].Certain;
        }
        return candidates;
    }

    public static Box PageRect(Page page, Box bbox) => bbox.Transform(Mat.From(page.TransformationMatrix)).Normalize();

    private static Box Area(Page page) => Box.From(page.Rect);

    public static string Zone(Box rect, Box area)
    {
        var cx = rect.CenterX - area.X0;
        var cy = rect.CenterY - area.Y0;
        var v = new[] { "üst", "orta", "alt" }[Math.Clamp((int)(3 * cy / area.Height), 0, 2)];
        var h = new[] { "sol", "orta", "sağ" }[Math.Clamp((int)(3 * cx / area.Width), 0, 2)];
        if (v == "orta" && h == "orta")
            return "sayfanın ortası";
        return h == "orta" ? $"{v} orta" : $"{v} {h}";
    }

    private static string PagesLabel(int count) => $"{count} sayfa";

    private static string CommonZone(IReadOnlyList<Page> pages, Dictionary<int, List<Box>> boxes) =>
        boxes.SelectMany(kv => kv.Value.Select(r => Zone(r, Area(pages[kv.Key]))))
             .GroupBy(z => z).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";

    /// <summary>
    /// Operator locations removing <paramref name="selected"/> elements of one page. A form
    /// XObject whose painted content is entirely selected is removed with a single Do deletion.
    /// </summary>
    private static IEnumerable<OpKey> OpsFor(List<PdfElement> selected, List<PdfElement> pageElements)
    {
        var keys = selected.Select(e => e.Key).ToHashSet();
        var forms = pageElements.Where(e => e.Kind == ElementKind.Form).ToList();
        var painting = pageElements.Where(e => e.Kind != ElementKind.Form).ToList();
        var collapsed = new HashSet<OpKey>();
        foreach (var form in forms)
        {
            var inner = painting.Where(e => e.Parents.Contains(form.Key)).ToList();
            if (inner.Count > 0 && inner.All(e => keys.Contains(e.Key)))
                collapsed.Add(form.Key);
        }
        foreach (var el in selected.Concat(forms.Where(f => collapsed.Contains(f.Key))))
        {
            if (el.Parents.Any(collapsed.Contains))
                continue;
            foreach (var index in el.Ops)
                yield return new OpKey(el.Container, index);
        }
    }

    private static Candidate Build(IReadOnlyList<Page> pages, string group, string label, string confidence,
        Dictionary<int, List<PdfElement>> perPage, string signature, Dictionary<int, List<PdfElement>> elements)
    {
        var boxes = new Dictionary<int, List<Box>>();
        var ops = new HashSet<OpKey>();
        foreach (var (pno, els) in perPage.OrderBy(kv => kv.Key))
        {
            var page = pages[pno];
            var painted = els.Where(e => e.Kind != ElementKind.Form).ToList();
            boxes[pno] = (painted.Count > 0 ? painted : els).Select(e => PageRect(page, e.BBox)).ToList();
            ops.UnionWith(OpsFor(els, elements[pno]));
        }
        return new Candidate
        {
            Group = group, Label = label, Confidence = confidence, Pages = perPage.Keys.OrderBy(k => k).ToList(),
            Boxes = boxes, Signature = signature,
            Ops = ops.OrderBy(o => o.Container.ToString(), StringComparer.Ordinal).ThenBy(o => o.Index).ToList(),
        };
    }

    private static void MarkUsed(Dictionary<int, List<PdfElement>> perPage, HashSet<PdfElement> used)
    {
        foreach (var els in perPage.Values)
            used.UnionWith(els);
    }

    private static IEnumerable<Candidate> AnnotationCandidates(Document doc, IReadOnlyList<Page> pages)
    {
        var perPage = new Dictionary<int, List<int>>();
        foreach (var page in pages)
            foreach (var (xref, type, _) in page.AnnotXrefs())
            {
                var subtype = doc.XrefGetKey(xref, "Subtype").Item2;
                if (subtype == "/Watermark" || type.ToString().EndsWith("WATERMARK", StringComparison.OrdinalIgnoreCase))
                    (perPage.TryGetValue(page.Number, out var list) ? list : perPage[page.Number] = new()).Add(xref);
            }
        if (perPage.Count == 0)
            yield break;
        var boxes = new Dictionary<int, List<Box>>();
        foreach (var (pno, xrefs) in perPage)
            boxes[pno] = xrefs.Select(x => pages[pno].LoadAnnot(x)).Where(a => a != null).Select(a => Box.From(a.Rect)).ToList();
        var pageList = perPage.Keys.OrderBy(k => k).ToList();
        yield return new Candidate
        {
            Group = CandidateGroup.Watermark, Confidence = Confidence.Certain, Pages = pageList, Boxes = boxes,
            Label = $"Watermark açıklaması (annotation), {PagesLabel(pageList.Count)}",
            Signature = "annot",
            Annots = perPage.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value.Select(x => (kv.Key, x))).ToList(),
        };
    }

    private static bool IsWatermarkArtifact(IReadOnlyList<MarkedTag> tags) => tags.Any(t =>
        t.Tag == "/Watermark" ||
        (t.Tag == "/Artifact" && t.Props != null && t.Props.TryGetValue("/Subtype", out var s) && s is PdfName { Value: "/Watermark" }));

    private static IEnumerable<Candidate> ArtifactCandidates(Document doc, IReadOnlyList<Page> pages,
        Dictionary<int, List<PdfElement>> elements, HashSet<PdfElement> used)
    {
        var perPage = elements
            .Select(kv => (kv.Key, Hits: kv.Value.Where(e => IsWatermarkArtifact(e.Tags)).ToList()))
            .Where(x => x.Hits.Count > 0).ToDictionary(x => x.Key, x => x.Hits);
        if (perPage.Count == 0)
            yield break;
        MarkUsed(perPage, used);
        yield return Build(pages, CandidateGroup.Watermark,
            $"Watermark olarak etiketli içerik, {PagesLabel(perPage.Count)}", Confidence.Certain, perPage, "artifact", elements);
    }

    private static IEnumerable<Candidate> LayerCandidates(Document doc, IReadOnlyList<Page> pages,
        Dictionary<int, List<PdfElement>> elements, HashSet<PdfElement> used)
    {
        foreach (var (ocg, info) in doc.GetOcgs())
        {
            var name = info.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
            if (!WatermarkLayerRe.IsMatch(name))
                continue;
            var perPage = elements
                .Select(kv => (kv.Key, Hits: kv.Value.Where(e => !used.Contains(e) && e.Tags.Any(t =>
                    t.Tag == "/OC" && t.Props != null && t.Props.TryGetValue("__ref__", out var r) && r is int x && x == ocg)).ToList()))
                .Where(x => x.Hits.Count > 0).ToDictionary(x => x.Key, x => x.Hits);
            var annots = new List<(int, int)>();
            foreach (var page in pages)
                foreach (var (xref, _, _) in page.AnnotXrefs())
                {
                    var (kind, value) = doc.XrefGetKey(xref, "OC");
                    if (kind == "xref" && PdfValues.XrefOf(value) == ocg)
                        annots.Add((page.Number, xref));
                }
            if (perPage.Count == 0 && annots.Count == 0)
                continue;
            MarkUsed(perPage, used);
            var cand = Build(pages, CandidateGroup.Watermark, "", Confidence.Certain, perPage, $"layer|{name.ToLowerInvariant()}", elements);
            cand.Annots = annots;
            foreach (var (pno, xref) in annots)
                if (pages[pno].LoadAnnot(xref) is { } annot)
                    (cand.Boxes.TryGetValue(pno, out var list) ? list : cand.Boxes[pno] = new()).Add(Box.From(annot.Rect));
            cand.Pages = cand.Pages.Concat(annots.Select(a => a.Item1)).Distinct().OrderBy(p => p).ToList();
            cand.Label = $"Watermark katmanı \"{name}\", {PagesLabel(cand.Pages.Count)}";
            yield return cand;
        }
    }

    private static bool OffAxis(double rotation) => Math.Abs((((rotation + 45.0) % 90.0) + 90.0) % 90.0 - 45.0) >= MinRotation;

    private enum Strength { None, Weak, Strong }

    /// <summary>How watermark-like a painted element is.</summary>
    private static Strength StrengthOf(PdfElement el, double pageArea)
    {
        if (el.Kind == ElementKind.Form)
            return Strength.None;
        var light = el.Kind == ElementKind.Text && el.Fill != null && el.Fill.Min() >= LightColor && el.Fill.Max() < 0.999;
        if (el.Alpha > FaintAlpha && !light)
            return Strength.None;
        var area = Math.Abs(el.BBox.Width) * Math.Abs(el.BBox.Height) / pageArea;
        if (OffAxis(el.Rotation))
            return Strength.Strong;
        if (el.Kind == ElementKind.Text && el.FontSize >= BigTextSize)
            return Strength.Strong;
        if (el.Kind == ElementKind.Image && area >= BigImageArea)
            return Strength.Strong;
        if (el.Kind is ElementKind.Path or ElementKind.Shading && area >= BigPathArea && !light)
            return Strength.Weak;
        return Strength.None;
    }

    private sealed record Item(PdfElement Element, Box Rect, Strength Strength, double Rotation);

    private static List<List<Item>> Cluster(List<Item> items, double gap)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }
        for (var i = 0; i < items.Count; i++)
        {
            var grown = items[i].Rect.Inflate(gap);
            for (var j = i + 1; j < items.Count; j++)
                if (items[i].Strength == items[j].Strength &&
                    Math.Abs(items[i].Rotation - items[j].Rotation) < MinRotation &&
                    grown.Intersects(items[j].Rect))
                    parent[Find(j)] = Find(i);
        }
        return Enumerable.Range(0, items.Count).GroupBy(Find).Select(g => g.Select(i => items[i]).ToList()).ToList();
    }

    private static bool Close(Box a, Box b, Box area, double yTolerance = PositionTolerance) =>
        Math.Abs(a.CenterX - b.CenterX) <= PositionTolerance * area.Width &&
        Math.Abs(a.CenterY - b.CenterY) <= yTolerance * area.Height &&
        Math.Abs(a.Width - b.Width) <= SizeTolerance * area.Width &&
        Math.Abs(a.Height - b.Height) <= SizeTolerance * area.Height;

    private static bool Repeated(int pageCount, int docPages) =>
        docPages == 1 || (pageCount >= 2 && pageCount >= RepeatRatio * docPages);

    private sealed class FaintGroup
    {
        public required Strength Strength;
        public required double Rotation;
        public required Box Rect;
        public Dictionary<int, List<PdfElement>> PerPage { get; } = new();
        public Dictionary<int, List<Box>> Boxes { get; } = new();
    }

    private static IEnumerable<Candidate> FaintCandidates(Document doc, IReadOnlyList<Page> pages,
        Dictionary<int, List<PdfElement>> elements, HashSet<PdfElement> used)
    {
        var groups = new List<FaintGroup>();
        foreach (var (pno, els) in elements)
        {
            var page = pages[pno];
            var area = Area(page);
            var items = new List<Item>();
            foreach (var el in els)
            {
                if (used.Contains(el))
                    continue;
                var strength = StrengthOf(el, area.Width * area.Height);
                if (strength != Strength.None)
                    items.Add(new Item(el, PageRect(page, el.BBox), strength, el.Rotation));
            }
            foreach (var cluster in Cluster(items, 0.02 * area.Width))
            {
                var rect = cluster.Skip(1).Aggregate(cluster[0].Rect, (acc, it) => acc.Union(it.Rect));
                var strength = cluster[0].Strength;
                var rotation = cluster[0].Rotation;
                var group = groups.FirstOrDefault(g => g.Strength == strength &&
                    Math.Abs(g.Rotation - rotation) < MinRotation && Close(g.Rect, rect, area));
                if (group == null)
                {
                    group = new FaintGroup { Strength = strength, Rotation = rotation, Rect = rect };
                    groups.Add(group);
                }
                (group.PerPage.TryGetValue(pno, out var list) ? list : group.PerPage[pno] = new()).AddRange(cluster.Select(c => c.Element));
                (group.Boxes.TryGetValue(pno, out var boxes) ? boxes : group.Boxes[pno] = new()).Add(rect);
            }
        }

        foreach (var g in groups)
        {
            var repeated = Repeated(g.PerPage.Count, doc.PageCount);
            var certain = g.Strength == Strength.Strong && repeated;
            if (g.Strength == Strength.Weak && !repeated)
                continue;
            MarkUsed(g.PerPage, used);
            var els = g.PerPage.Values.SelectMany(e => e).ToList();
            var kinds = string.Join("/", els.Select(e => KindWords.GetValueOrDefault(e.Kind, e.Kind.ToString())).Distinct().OrderBy(k => k, StringComparer.Ordinal));
            var alpha = els.Min(e => e.Alpha);
            var details = new List<string> { alpha < 1 ? $"opaklık %{Math.Round(alpha * 100)}" : "açık renk" };
            if (OffAxis(g.Rotation))
                details.Add($"{Math.Abs(Math.Round(g.Rotation))}° eğik");
            var first = g.PerPage.Keys.Min();
            var firstArea = Area(pages[first]);
            var label = $"Soluk {kinds} ({string.Join(", ", details)}, {Zone(g.Rect, firstArea)}), {PagesLabel(g.PerPage.Count)}";
            var signature = string.Join("|", "faint", kinds, Math.Round(g.Rotation / MinRotation),
                Math.Round(g.Rect.CenterX / firstArea.Width, 1), Math.Round(g.Rect.CenterY / firstArea.Height, 1));
            var cand = Build(pages, CandidateGroup.Watermark, label, certain ? Confidence.Certain : Confidence.Possible,
                g.PerPage, signature, elements);
            cand.Boxes = g.Boxes;
            yield return cand;
        }
    }

    private static string ImageDigest(Document doc, int xref, Dictionary<int, string> cache)
    {
        if (!cache.TryGetValue(xref, out var digest))
        {
            digest = Convert.ToHexString(MD5.HashData(doc.XrefStreamRaw(xref) ?? Array.Empty<byte>()));
            cache[xref] = digest;
        }
        return digest;
    }

    /// <summary>A white filled path painted right before <paramref name="el"/> and covering it.</summary>
    private static PdfElement? WhiteBacking(PdfElement el, Box rect, Page page, List<PdfElement> pageElements)
    {
        PdfElement? previous = null;
        foreach (var other in pageElements)
        {
            if (other.Container != el.Container || other.Kind == ElementKind.Form || other.Ops[^1] >= el.Ops[0])
                continue;
            if (previous == null || other.Ops[^1] > previous.Ops[^1])
                previous = other;
        }
        if (previous is not { Kind: ElementKind.Path, Fill: { } fill } || fill.Min() < 0.98)
            return null;
        var backing = PageRect(page, previous.BBox);
        if (!backing.Intersects(rect))
            return null;
        if (backing.Width * backing.Height > MaxBackingArea * Math.Max(rect.Width * rect.Height, 1.0))
            return null;
        return previous;
    }

    private sealed class ImageGroup
    {
        public required string Digest;
        public List<Box> Rects { get; } = new();
        public Dictionary<int, List<(PdfElement Element, Box Rect)>> PerPage { get; } = new();
    }

    private static IEnumerable<Candidate> ImageCandidates(Document doc, IReadOnlyList<Page> pages,
        Dictionary<int, List<PdfElement>> elements, HashSet<PdfElement> used)
    {
        var cache = new Dictionary<int, string>();
        var groups = new List<ImageGroup>();
        foreach (var (pno, els) in elements)
        {
            var page = pages[pno];
            var area = Area(page);
            foreach (var el in els)
            {
                if (el.Kind != ElementKind.Image || el.Xref == 0 || used.Contains(el))
                    continue;
                var digest = ImageDigest(doc, el.Xref, cache);
                var rect = PageRect(page, el.BBox);
                var group = groups.FirstOrDefault(g => g.Digest == digest && g.Rects.Any(r => Close(r, rect, area, ImageYTolerance)));
                if (group == null)
                {
                    group = new ImageGroup { Digest = digest };
                    groups.Add(group);
                }
                group.Rects.Add(rect);
                (group.PerPage.TryGetValue(pno, out var list) ? list : group.PerPage[pno] = new()).Add((el, rect));
            }
        }

        foreach (var g in groups)
        {
            if (doc.PageCount == 1 || !Repeated(g.PerPage.Count, doc.PageCount))
                continue;
            var chosen = new Dictionary<int, List<PdfElement>>();
            foreach (var (pno, items) in g.PerPage)
            {
                chosen[pno] = new List<PdfElement>();
                foreach (var (el, rect) in items)
                {
                    chosen[pno].Add(el);
                    if (WhiteBacking(el, rect, pages[pno], elements[pno]) is { } backing)
                        chosen[pno].Add(backing);
                }
            }
            MarkUsed(chosen, used);
            var first = g.Rects[0];
            var boxes = g.PerPage.ToDictionary(kv => kv.Key, kv => kv.Value.Select(x => x.Rect).ToList());
            var label = $"Görsel {first.Width:0}x{first.Height:0} pt ({CommonZone(pages, boxes)}), {PagesLabel(g.PerPage.Count)}";
            var cand = Build(pages, CandidateGroup.Image, label, Confidence.Possible, chosen, $"image|{g.Digest}", elements);
            cand.Boxes = boxes;
            yield return cand;
        }
    }

    public static string NormalizeText(string text)
    {
        text = text.Replace('\u00a0', ' ').Normalize(NormalizationForm.FormC);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static TextAlign AlignOf(Page page, Box rect)
    {
        var area = Area(page);
        if (Math.Abs(rect.CenterX - area.CenterX) <= 0.03 * area.Width)
            return TextAlign.Center;
        return rect.X0 > area.X0 + 0.5 * area.Width ? TextAlign.Right : TextAlign.Left;
    }

    private static float[] ColorOf(int srgb) =>
        new[] { ((srgb >> 16) & 0xFF) / 255f, ((srgb >> 8) & 0xFF) / 255f, (srgb & 0xFF) / 255f };

    private static TextHit HitFor(Page page, Box rect, Span span, double baseline) =>
        new(rect, baseline, span.Size, ColorOf(span.Color), ((int)span.Flags & 16) != 0, AlignOf(page, rect));

    private static bool Horizontal(Line line) => Math.Abs(line.Dir.Y) < 1e-3 && line.Dir.X > 0;

    private static IEnumerable<Line> Lines(PageInfo info) =>
        info.Blocks.Where(b => b.Lines != null).SelectMany(b => b.Lines).Where(l => l.Spans != null && l.Spans.Count > 0);

    private static IEnumerable<Candidate> TextCandidates(IReadOnlyList<Page> pages)
    {
        var groups = new Dictionary<string, Dictionary<int, List<TextHit>>>();
        foreach (var page in pages)
        {
            var area = Area(page);
            var info = page.GetTextPage(TextFlags, null!).ExtractDict(null!, false);
            foreach (var line in Lines(info))
            {
                if (!Horizontal(line))
                    continue;
                var text = NormalizeText(string.Concat(line.Spans.Select(s => s.Text)));
                if (text.Length < 2 || !text.Any(char.IsLetter))
                    continue;
                Box? rect = null;
                foreach (var span in line.Spans.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
                    rect = Box.Union(rect, Box.From(span.Bbox));
                if (rect is not { } r || r.Width <= 0 || r.Height <= 0)
                    continue;
                var cy = r.CenterY - area.Y0;
                if (cy > MarginRatio * area.Height && cy < (1 - MarginRatio) * area.Height)
                    continue;
                var span0 = line.Spans.First(s => !string.IsNullOrWhiteSpace(s.Text));
                var hit = HitFor(page, r, span0, span0.Origin.Y);
                var perPage = groups.TryGetValue(text, out var p) ? p : groups[text] = new();
                (perPage.TryGetValue(page.Number, out var hits) ? hits : perPage[page.Number] = new()).Add(hit);
            }
        }

        foreach (var (text, perPage) in groups)
        {
            if (pages.Count == 1 || !Repeated(perPage.Count, pages.Count))
                continue;
            var boxes = perPage.ToDictionary(kv => kv.Key, kv => kv.Value.Select(h => h.Rect).ToList());
            yield return new Candidate
            {
                Group = CandidateGroup.Text, Confidence = Confidence.Possible, Pages = perPage.Keys.OrderBy(k => k).ToList(),
                Boxes = boxes, Label = $"\"{text}\" ({CommonZone(pages, boxes)}), {PagesLabel(perPage.Count)}",
                Signature = $"text|{text}", Hits = perPage, Text = text,
            };
        }
    }

    /// <summary>Occurrences of <paramref name="needle"/> on a page, also across span boundaries.</summary>
    public static List<TextHit> FindText(Page page, string needle)
    {
        var target = needle.Replace('\u00a0', ' ').Normalize(NormalizationForm.FormD);
        var hits = new List<TextHit>();
        if (string.IsNullOrWhiteSpace(target))
            return hits;
        var info = page.GetTextPage(TextFlags, null!).ExtractRAWDict(false);
        foreach (var line in Lines(info))
        {
            if (!Horizontal(line))
                continue;
            var seq = new StringBuilder();
            var owners = new List<(MuPDF.NET.Char Char, Span Span)>();
            foreach (var span in line.Spans)
                foreach (var ch in span.Chars ?? new List<MuPDF.NET.Char>())
                    foreach (var c in ch.C.ToString().Replace('\u00a0', ' ').Normalize(NormalizationForm.FormD))
                    {
                        seq.Append(c);
                        owners.Add((ch, span));
                    }
            var joined = seq.ToString();
            var start = 0;
            while (true)
            {
                var idx = joined.IndexOf(target, start, StringComparison.Ordinal);
                if (idx < 0)
                    break;
                Box? rect = null;
                for (var k = idx; k < idx + target.Length; k++)
                {
                    var b = owners[k].Char.Bbox;
                    rect = Box.Union(rect, new Box(b.x0, b.y0, b.x1, b.y1));
                }
                var (firstChar, span) = owners[idx];
                hits.Add(HitFor(page, rect!.Value, span, firstChar.Origin.y));
                start = idx + target.Length;
            }
        }
        return hits;
    }
}
