namespace PdfEditor.Core.Cleaning;

public static class CandidateGroup
{
    public const string Watermark = "watermark";
    public const string Image = "image";
    public const string Text = "text";

    public static readonly IReadOnlyList<(string Group, string Title)> Titles = new[]
    {
        (Watermark, "Watermark"),
        (Image, "Tekrarlanan görsel"),
        (Text, "Tekrarlanan yazı"),
    };
}

public static class Confidence
{
    public const string Certain = "kesin";
    public const string Possible = "olası";
}

public enum TextAlign { Left, Center, Right }

/// <summary>One occurrence of a text on a page, with the style needed to rewrite it.</summary>
public sealed record TextHit(Box Rect, double Baseline, double Size, float[] Color, bool Bold, TextAlign Align);

/// <summary>Something the user may remove: a watermark, a repeated image or a repeated text.</summary>
public sealed class Candidate
{
    public string Id { get; set; } = "";
    public required string Group { get; init; }
    public required string Label { get; set; }
    public required string Confidence { get; init; }
    public required List<int> Pages { get; set; }
    public required Dictionary<int, List<Box>> Boxes { get; set; }
    public required string Signature { get; init; }
    public List<OpKey> Ops { get; set; } = new();
    public List<(int Page, int Xref)> Annots { get; set; } = new();
    public Dictionary<int, List<TextHit>> Hits { get; init; } = new();
    public string? Text { get; init; }
    public bool Selected { get; set; }
    public string Replacement { get; set; } = "";

    public bool Certain => Confidence == Cleaning.Confidence.Certain;

    public Candidate Clone() => (Candidate)MemberwiseClone();
}
