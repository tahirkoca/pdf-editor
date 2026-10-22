namespace PdfEditor.Core.Cleaning;

/// <summary>Affine PDF matrix [a b c d e f].</summary>
public readonly record struct Mat(double A, double B, double C, double D, double E, double F)
{
    public static readonly Mat Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>This matrix followed by <paramref name="m"/>.</summary>
    public Mat Then(Mat m) => new(
        A * m.A + B * m.C,
        A * m.B + B * m.D,
        C * m.A + D * m.C,
        C * m.B + D * m.D,
        E * m.A + F * m.C + m.E,
        E * m.B + F * m.D + m.F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    public double Angle => Math.Atan2(B, A) * 180.0 / Math.PI;

    public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));

    public static Mat From(MuPDF.NET.Matrix m) => new(m.A, m.B, m.C, m.D, m.E, m.F);
}

/// <summary>Axis aligned rectangle.</summary>
public readonly record struct Box(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Y0 + Y1) / 2;

    public Box Normalize() => new(Math.Min(X0, X1), Math.Min(Y0, Y1), Math.Max(X0, X1), Math.Max(Y0, Y1));

    public Box Union(Box o) => new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));

    public bool Intersects(Box o) => X0 < o.X1 && o.X0 < X1 && Y0 < o.Y1 && o.Y0 < Y1;

    public Box Inflate(double d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);

    public Box Transform(Mat m) => Bounds(Corners().Select(p => m.Apply(p.X, p.Y)));

    public IEnumerable<(double X, double Y)> Corners()
    {
        yield return (X0, Y0);
        yield return (X1, Y0);
        yield return (X0, Y1);
        yield return (X1, Y1);
    }

    public static Box Bounds(IEnumerable<(double X, double Y)> points)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        var any = false;
        foreach (var (x, y) in points)
        {
            any = true;
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
        }
        return any ? new Box(x0, y0, x1, y1) : default;
    }

    public static Box? Union(Box? a, Box b) => a is { } box ? box.Union(b) : b;

    public static Box From(MuPDF.NET.Rect r) => new(r.X0, r.Y0, r.X1, r.Y1);

    public MuPDF.NET.Rect ToRect() => new((float)X0, (float)Y0, (float)X1, (float)Y1);
}
