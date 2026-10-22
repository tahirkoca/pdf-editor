using System.Globalization;
using MuPDF.NET;

namespace PdfEditor.Core.Cleaning;

/// <summary>Content stream owner: a page (by number) or a form XObject (by xref).</summary>
public readonly record struct ContainerKey(bool IsPage, int Id)
{
    public override string ToString() => IsPage ? $"page:{Id}" : $"form:{Id}";
}

public readonly record struct OpKey(ContainerKey Container, int Index);

/// <summary>Marked content tag (BDC/BMC) with its property dictionary.</summary>
public sealed record MarkedTag(string? Tag, Dictionary<string, object?>? Props);

public enum ElementKind { Text, Image, Form, Path, Shading }

/// <summary>One painted object: text block, image, form, path or shading.</summary>
public sealed class PdfElement
{
    public required ElementKind Kind { get; init; }
    public required ContainerKey Container { get; init; }
    public List<int> Ops { get; } = new();
    public Box BBox { get; set; }
    public double Rotation { get; init; }
    public double Alpha { get; init; } = 1;
    public double[]? Fill { get; init; }
    public IReadOnlyList<MarkedTag> Tags { get; init; } = Array.Empty<MarkedTag>();
    public IReadOnlyList<OpKey> Parents { get; init; } = Array.Empty<OpKey>();
    public string? Name { get; init; }
    public int Xref { get; init; }
    public List<byte> Text { get; } = new();
    public double FontSize { get; init; }
    public List<(double X, double Y)> Points { get; } = new();

    public OpKey Key => new(Container, Ops[0]);
}

/// <summary>Lookup of named resources for a page or form XObject.</summary>
public sealed class PdfResources
{
    private readonly Document _doc;
    private readonly int _xref;
    private readonly bool _isPage;
    private readonly PdfResources? _parent;
    private readonly Dictionary<string, (string Kind, string Value)> _cache = new();

    public PdfResources(Document doc, int xref, bool isPage, PdfResources? parent = null)
    {
        _doc = doc;
        _xref = xref;
        _isPage = isPage;
        _parent = parent;
    }

    public (string Kind, string Value) Get(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
            return cached;
        (string Kind, string Value) result = ("null", "null");
        var xref = _xref;
        while (xref > 0)
        {
            var (kind, value) = _doc.XrefGetKey(xref, $"Resources/{path}");
            if (kind != "null")
            {
                result = (kind, value);
                break;
            }
            if (!_isPage)
                break;
            var (pkind, pvalue) = _doc.XrefGetKey(xref, "Parent");
            xref = pkind == "xref" ? PdfValues.XrefOf(pvalue) : 0;
        }
        if (result.Kind == "null" && _parent != null)
            result = _parent.Get(path);
        _cache[path] = result;
        return result;
    }

    public double? Opacity(PdfName name)
    {
        var (kind, value) = Get($"ExtGState/{name.Bare}/ca");
        return kind is "int" or "float" or "real" && PdfValues.TryNumber(value, out var v) ? v : null;
    }

    public bool FontIsTwoByte(PdfName name)
    {
        var (kind, value) = Get($"Font/{name.Bare}/Subtype");
        return kind == "name" && value == "/Type0";
    }

    public int Ref(string category, PdfName name)
    {
        var (kind, value) = Get($"{category}/{name.Bare}");
        return kind == "xref" ? PdfValues.XrefOf(value) : 0;
    }
}

public static class PdfValues
{
    public static int XrefOf(string value)
    {
        var first = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out var x) ? x : 0;
    }

    public static bool TryNumber(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    public static double[]? NumberArray(string value, int length)
    {
        var parts = value.Trim('[', ']', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nums = new List<double>();
        foreach (var part in parts)
        {
            if (!TryNumber(part, out var v))
                return null;
            nums.Add(v);
        }
        return nums.Count == length ? nums.ToArray() : null;
    }
}

/// <summary>Walks content streams tracking graphics state and collects painted elements.</summary>
public static class ContentWalker
{
    private const int MaxFormDepth = 12;

    public static byte[] PageContent(Document doc, Page page)
    {
        var output = new MemoryStream();
        var first = true;
        foreach (var xref in page.GetContents())
        {
            if (!first)
                output.WriteByte((byte)'\n');
            first = false;
            var data = doc.XrefStream(xref) ?? Array.Empty<byte>();
            output.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// All painted elements of a page, including those inside form XObjects.
    /// Bounding boxes are in default PDF user space (origin bottom-left).
    /// </summary>
    public static List<PdfElement> PageElements(Document doc, Page page)
    {
        var output = new List<PdfElement>();
        var res = new PdfResources(doc, page.Xref, isPage: true);
        Walk(doc, PageContent(doc, page), new ContainerKey(true, page.Number), res, Mat.Identity, 1.0,
             Array.Empty<MarkedTag>(), Array.Empty<OpKey>(), output, 0, new HashSet<int>());
        return output;
    }

    private sealed class GState
    {
        public Mat Ctm;
        public double Alpha = 1;
        public double[]? Fill = { 0, 0, 0 };
        public GState Copy() => (GState)MemberwiseClone();
    }

    private sealed class TextState
    {
        public Mat Tm = Mat.Identity;
        public Mat Tlm = Mat.Identity;
        public PdfName? Font;
        public double Size;
        public bool TwoByte;
        public double Th = 1;
        public double Tl;
        public double Tc;
        public double Tw;
        public double Rise;

        public void NextLine(double tx, double ty)
        {
            Tlm = new Mat(1, 0, 0, 1, tx, ty).Then(Tlm);
            Tm = Tlm;
        }
    }

    private static List<double> Numbers(List<object?> args) => args.OfType<double>().ToList();

    private static double Num(List<object?> args, int i) => i < args.Count && args[i] is double d ? d : 0;

    private static List<(double, double)> RectPoints(Mat m, double x0, double y0, double x1, double y1) =>
        new Box(x0, y0, x1, y1).Corners().Select(p => m.Apply(p.X, p.Y)).ToList();

    private static void Walk(Document doc, byte[] data, ContainerKey container, PdfResources res, Mat ctm,
        double baseAlpha, IReadOnlyList<MarkedTag> tags, IReadOnlyList<OpKey> parents, List<PdfElement> output,
        int depth, HashSet<int> visited)
    {
        var ops = ContentStream.Parse(data);
        var gs = new GState { Ctm = ctm };
        var stack = new Stack<GState>();
        var marked = new List<MarkedTag>(tags);
        var path = new List<(double X, double Y)>();
        var ts = new TextState();
        PdfElement? textElement = null;

        PdfElement Make(ElementKind kind, int index, List<(double X, double Y)> points, double rotation,
            string? name = null, int xref = 0, double fontSize = 0)
        {
            var el = new PdfElement
            {
                Kind = kind, Container = container, Rotation = rotation, Alpha = baseAlpha * gs.Alpha,
                Fill = gs.Fill, Tags = marked.ToArray(), Parents = parents, Name = name, Xref = xref,
                FontSize = fontSize,
            };
            el.Ops.Add(index);
            el.Points.AddRange(points);
            el.BBox = Box.Bounds(points);
            output.Add(el);
            return el;
        }

        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var args = op.Args;
            switch (op.Name)
            {
                case "q":
                    stack.Push(gs.Copy());
                    break;
                case "Q":
                    if (stack.Count > 0)
                        gs = stack.Pop();
                    break;
                case "cm":
                {
                    var n = Numbers(args);
                    if (n.Count == 6)
                        gs.Ctm = new Mat(n[0], n[1], n[2], n[3], n[4], n[5]).Then(gs.Ctm);
                    break;
                }
                case "gs":
                    if (args.Count > 0 && args[0] is PdfName gsName && res.Opacity(gsName) is { } ca)
                        gs.Alpha = ca;
                    break;
                case "rg" or "sc" or "scn" or "g" or "k":
                {
                    var n = Numbers(args);
                    gs.Fill = n.Count switch
                    {
                        3 => n.ToArray(),
                        1 => new[] { n[0], n[0], n[0] },
                        4 => new[] { (1 - n[0]) * (1 - n[3]), (1 - n[1]) * (1 - n[3]), (1 - n[2]) * (1 - n[3]) },
                        _ => null,
                    };
                    break;
                }
                case "cs":
                    gs.Fill = null;
                    break;
                case "BDC":
                {
                    var tag = args.Count > 0 ? (args[0] as PdfName)?.Value : null;
                    var props = args.Count > 1 ? args[1] : null;
                    Dictionary<string, object?>? dict = props switch
                    {
                        Dictionary<string, object?> d => d,
                        PdfName propName => new Dictionary<string, object?> { ["__ref__"] = res.Ref("Properties", propName) },
                        _ => null,
                    };
                    marked.Add(new MarkedTag(tag, dict));
                    break;
                }
                case "BMC":
                    marked.Add(new MarkedTag(args.Count > 0 ? (args[0] as PdfName)?.Value : null, null));
                    break;
                case "EMC":
                    if (marked.Count > tags.Count)
                        marked.RemoveAt(marked.Count - 1);
                    break;
                case "BT":
                    ts.Tm = ts.Tlm = Mat.Identity;
                    textElement = null;
                    break;
                case "ET":
                    textElement = null;
                    break;
                case "Tf":
                    if (args.Count >= 2)
                    {
                        ts.Font = args[0] as PdfName;
                        ts.Size = Num(args, 1);
                        ts.TwoByte = ts.Font != null && res.FontIsTwoByte(ts.Font);
                    }
                    break;
                case "Tm":
                {
                    var n = Numbers(args);
                    if (n.Count == 6)
                        ts.Tm = ts.Tlm = new Mat(n[0], n[1], n[2], n[3], n[4], n[5]);
                    break;
                }
                case "Td":
                    if (args.Count == 2)
                        ts.NextLine(Num(args, 0), Num(args, 1));
                    break;
                case "TD":
                    if (args.Count == 2)
                    {
                        ts.Tl = -Num(args, 1);
                        ts.NextLine(Num(args, 0), Num(args, 1));
                    }
                    break;
                case "T*":
                    ts.NextLine(0, -ts.Tl);
                    break;
                case "TL":
                    ts.Tl = Num(args, 0);
                    break;
                case "Tz":
                    ts.Th = Num(args, 0) / 100.0;
                    break;
                case "Tc":
                    ts.Tc = Num(args, 0);
                    break;
                case "Tw":
                    ts.Tw = Num(args, 0);
                    break;
                case "Ts":
                    ts.Rise = Num(args, 0);
                    break;
                case "Tj" or "TJ" or "'" or "\"":
                {
                    if (op.Name == "'")
                        ts.NextLine(0, -ts.Tl);
                    else if (op.Name == "\"" && args.Count >= 3)
                    {
                        ts.Tw = Num(args, 0);
                        ts.Tc = Num(args, 1);
                        ts.NextLine(0, -ts.Tl);
                    }
                    var last = args.Count > 0 ? args[^1] : null;
                    var items = last as List<object?> ?? new List<object?> { last };
                    var m = ts.Tm.Then(gs.Ctm);
                    var trm = new Mat(ts.Size * ts.Th, 0, 0, ts.Size, 0, ts.Rise).Then(m);
                    var raw = new List<byte>();
                    double tx = 0;
                    foreach (var item in items)
                    {
                        if (item is byte[] bytes)
                        {
                            var count = ts.TwoByte ? bytes.Length / 2 : bytes.Length;
                            var spaces = ts.TwoByte ? 0 : bytes.Count(b => b == (byte)' ');
                            tx += (count * (0.55 * ts.Size + ts.Tc) + spaces * ts.Tw) * ts.Th;
                            raw.AddRange(bytes);
                        }
                        else if (item is double adjust)
                        {
                            tx -= adjust / 1000.0 * ts.Size * ts.Th;
                        }
                    }
                    var points = RectPoints(m, 0, ts.Rise - 0.25 * ts.Size, tx, ts.Rise + 0.85 * ts.Size);
                    ts.Tm = new Mat(1, 0, 0, 1, tx, 0).Then(ts.Tm);
                    if (textElement == null)
                    {
                        textElement = Make(ElementKind.Text, i, points, trm.Angle, ts.Font?.Value, fontSize: trm.Scale);
                    }
                    else
                    {
                        textElement.Ops.Add(i);
                        textElement.Points.AddRange(points);
                        textElement.BBox = Box.Bounds(textElement.Points);
                    }
                    textElement.Text.AddRange(raw);
                    break;
                }
                case "m" or "l":
                {
                    var n = Numbers(args);
                    if (n.Count == 2)
                        path.Add(gs.Ctm.Apply(n[0], n[1]));
                    break;
                }
                case "c" or "v" or "y":
                {
                    var n = Numbers(args);
                    for (var j = 0; j + 1 < n.Count; j += 2)
                        path.Add(gs.Ctm.Apply(n[j], n[j + 1]));
                    break;
                }
                case "re":
                {
                    var n = Numbers(args);
                    if (n.Count == 4)
                        path.AddRange(RectPoints(gs.Ctm, n[0], n[1], n[0] + n[2], n[1] + n[3]));
                    break;
                }
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    Make(ElementKind.Path, i, path, gs.Ctm.Angle);
                    path = new List<(double X, double Y)>();
                    break;
                case "n":
                    path = new List<(double X, double Y)>();
                    break;
                case "sh":
                    Make(ElementKind.Shading, i, new List<(double X, double Y)>(), gs.Ctm.Angle);
                    break;
                case "BI":
                    Make(ElementKind.Image, i, RectPoints(gs.Ctm, 0, 0, 1, 1), gs.Ctm.Angle);
                    break;
                case "Do":
                {
                    if (args.Count == 0 || args[0] is not PdfName xname)
                        break;
                    var xref = res.Ref("XObject", xname);
                    if (xref == 0)
                        break;
                    var subtype = doc.XrefGetKey(xref, "Subtype").Item2;
                    if (subtype == "/Image")
                    {
                        Make(ElementKind.Image, i, RectPoints(gs.Ctm, 0, 0, 1, 1), gs.Ctm.Angle, xname.Value, xref);
                    }
                    else if (subtype == "/Form")
                    {
                        var matrix = FormArray(doc, xref, "Matrix", 6) is { } mv
                            ? new Mat(mv[0], mv[1], mv[2], mv[3], mv[4], mv[5]) : Mat.Identity;
                        var bbox = FormArray(doc, xref, "BBox", 4) ?? new double[4];
                        var m = matrix.Then(gs.Ctm);
                        Make(ElementKind.Form, i, RectPoints(m, bbox[0], bbox[1], bbox[2], bbox[3]), m.Angle, xname.Value, xref);
                        if (depth < MaxFormDepth && !visited.Contains(xref))
                        {
                            var nextVisited = new HashSet<int>(visited) { xref };
                            var nextParents = parents.Append(new OpKey(container, i)).ToArray();
                            Walk(doc, doc.XrefStream(xref) ?? Array.Empty<byte>(), new ContainerKey(false, xref),
                                 new PdfResources(doc, xref, isPage: false, parent: res), m, baseAlpha * gs.Alpha,
                                 marked.ToArray(), nextParents, output, depth + 1, nextVisited);
                        }
                    }
                    break;
                }
            }
        }
    }

    private static double[]? FormArray(Document doc, int xref, string key, int length)
    {
        var (kind, value) = doc.XrefGetKey(xref, key);
        return kind == "array" ? PdfValues.NumberArray(value, length) : null;
    }
}
