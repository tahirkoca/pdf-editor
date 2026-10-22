using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfEditor.Core.Cleaning;

/// <summary>A PDF name operand such as /F1 (Value keeps the leading slash).</summary>
public sealed record PdfName(string Value)
{
    public string Bare => Value.TrimStart('/');
    public override string ToString() => Value;
}

/// <summary>One content stream operator with its operands and byte range.</summary>
public sealed class PdfOp
{
    public PdfOp(string name, List<object?> args, int start, int end)
    {
        Name = name;
        Args = args;
        Start = start;
        End = end;
    }

    public string Name { get; }
    public List<object?> Args { get; }
    public int Start { get; }
    public int End { get; }

    public override string ToString() => $"{Name} [{string.Join(" ", Args)}]";
}

/// <summary>Tokenizer for PDF content streams and operator neutralization.</summary>
public static class ContentStream
{
    public static readonly HashSet<string> PathPaint = new() { "S", "s", "f", "F", "f*", "B", "B*", "b", "b*" };
    public static readonly HashSet<string> TextShow = new() { "Tj", "TJ", "'", "\"" };

    private static readonly Regex NumberRe = new(@"^[+-]?(?:\d+\.?\d*|\.\d+)$", RegexOptions.Compiled);
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private enum Kind { Name, String, DictOpen, DictClose, ArrayOpen, ArrayClose, Junk, Number, Bool, Null, Operator }

    private readonly record struct Token(Kind Kind, object? Value, int Start);

    public static List<PdfOp> Parse(byte[] data)
    {
        var lexer = new Lexer(Latin1.GetString(data));
        var ops = new List<PdfOp>();
        var operands = new List<object?>();
        int? start = null;
        while (lexer.Next() is { } tok)
        {
            if (tok.Kind is Kind.ArrayClose or Kind.DictClose or Kind.Junk)
                continue;
            start ??= tok.Start;
            if (tok.Kind == Kind.Operator)
            {
                var name = (string)tok.Value!;
                if (name == "BI")
                    operands = new List<object?> { lexer.SkipInlineImage() };
                ops.Add(new PdfOp(name, operands, start.Value, lexer.Pos));
                operands = new List<object?>();
                start = null;
            }
            else
            {
                operands.Add(lexer.Value(tok));
            }
        }
        return ops;
    }

    /// <summary>
    /// Returns <paramref name="data"/> with the painting operators at <paramref name="indices"/>
    /// disabled. State operators (q/Q, cm, gs, BT/ET, ...) are kept so the remaining
    /// content keeps its graphics state.
    /// </summary>
    public static byte[] Neutralize(byte[] data, IEnumerable<int> indices)
    {
        var ops = Parse(data);
        var output = new MemoryStream(data.Length);
        var last = 0;
        foreach (var i in indices.Distinct().OrderBy(i => i))
        {
            if (i < 0 || i >= ops.Count)
                continue;
            var op = ops[i];
            output.Write(data, last, op.Start - last);
            var replacement = Replacement(op);
            output.Write(replacement, 0, replacement.Length);
            last = op.End;
        }
        output.Write(data, last, data.Length - last);
        return output.ToArray();
    }

    private static byte[] Replacement(PdfOp op)
    {
        if (PathPaint.Contains(op.Name))
            return Latin1.GetBytes(" n ");
        if (op.Name == "'")
            return Latin1.GetBytes(" T* ");
        if (op.Name == "\"" && op.Args.Count >= 2)
            return Latin1.GetBytes($" {Format(op.Args[0])} Tw {Format(op.Args[1])} Tc T* ");
        return Latin1.GetBytes(" ");
    }

    private static string Format(object? value) =>
        value is double d ? d.ToString("G", CultureInfo.InvariantCulture) : value?.ToString() ?? "0";

    private sealed class Lexer
    {
        private readonly string _s;
        private readonly int _n;
        private int _pos;

        public Lexer(string s)
        {
            _s = s;
            _n = s.Length;
        }

        public int Pos => _pos;

        private static bool IsWhite(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0';

        private static bool IsDelimiter(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

        private void Skip()
        {
            while (_pos < _n)
            {
                var c = _s[_pos];
                if (IsWhite(c))
                    _pos++;
                else if (c == '%')
                    while (_pos < _n && _s[_pos] != '\r' && _s[_pos] != '\n')
                        _pos++;
                else
                    break;
            }
        }

        public Token? Next()
        {
            Skip();
            var p = _pos;
            if (p >= _n)
                return null;
            var ch = _s[p];
            switch (ch)
            {
                case '/':
                {
                    var end = p + 1;
                    while (end < _n && !IsWhite(_s[end]) && !IsDelimiter(_s[end]))
                        end++;
                    _pos = end;
                    return new Token(Kind.Name, new PdfName(DecodeName(_s.Substring(p, end - p))), p);
                }
                case '(':
                    return new Token(Kind.String, ReadString(), p);
                case '<':
                    if (p + 1 < _n && _s[p + 1] == '<')
                    {
                        _pos = p + 2;
                        return new Token(Kind.DictOpen, null, p);
                    }
                    var close = _s.IndexOf('>', p + 1);
                    if (close > 0 && IsHex(p + 1, close))
                    {
                        _pos = close + 1;
                        return new Token(Kind.String, DecodeHex(_s.Substring(p + 1, close - p - 1)), p);
                    }
                    _pos = p + 1;
                    return new Token(Kind.Junk, null, p);
                case '>':
                    if (p + 1 < _n && _s[p + 1] == '>')
                    {
                        _pos = p + 2;
                        return new Token(Kind.DictClose, null, p);
                    }
                    _pos = p + 1;
                    return new Token(Kind.Junk, null, p);
                case '[':
                    _pos = p + 1;
                    return new Token(Kind.ArrayOpen, null, p);
                case ']':
                    _pos = p + 1;
                    return new Token(Kind.ArrayClose, null, p);
                case ')':
                case '{':
                case '}':
                    _pos = p + 1;
                    return new Token(Kind.Junk, null, p);
            }
            var stop = p;
            while (stop < _n && !IsWhite(_s[stop]) && !IsDelimiter(_s[stop]))
                stop++;
            _pos = stop;
            var tok = _s.Substring(p, stop - p);
            if (NumberRe.IsMatch(tok))
                return new Token(Kind.Number, double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture), p);
            if (tok is "true" or "false")
                return new Token(Kind.Bool, tok == "true", p);
            if (tok == "null")
                return new Token(Kind.Null, null, p);
            return new Token(Kind.Operator, tok, p);
        }

        private bool IsHex(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                var c = _s[i];
                if (!Uri.IsHexDigit(c) && !IsWhite(c))
                    return false;
            }
            return true;
        }

        public object? Value(Token tok)
        {
            if (tok.Kind == Kind.ArrayOpen)
            {
                var items = new List<object?>();
                while (Next() is { } t && t.Kind != Kind.ArrayClose)
                    items.Add(Value(t));
                return items;
            }
            if (tok.Kind == Kind.DictOpen)
            {
                var dict = new Dictionary<string, object?>();
                string? key = null;
                var expectKey = true;
                while (Next() is { } t && t.Kind != Kind.DictClose)
                {
                    var item = Value(t);
                    if (expectKey)
                    {
                        key = (item as PdfName)?.Value;
                        expectKey = false;
                    }
                    else
                    {
                        if (key != null)
                            dict[key] = item;
                        expectKey = true;
                    }
                }
                return dict;
            }
            return tok.Value;
        }

        public Dictionary<string, object?> SkipInlineImage()
        {
            var parameters = new Dictionary<string, object?>();
            string? key = null;
            while (Next() is { } tok)
            {
                if (tok.Kind == Kind.Operator && (string)tok.Value! == "ID")
                    break;
                var item = Value(tok);
                if (key == null)
                    key = (item as PdfName)?.Value ?? "";
                else
                {
                    parameters[key] = item;
                    key = null;
                }
            }
            var dataStart = Math.Min(_pos + 1, _n);
            var idx = dataStart;
            while (true)
            {
                idx = _s.IndexOf("EI", idx, StringComparison.Ordinal);
                if (idx < 0)
                {
                    var any = _s.IndexOf("EI", dataStart, StringComparison.Ordinal);
                    _pos = any < 0 ? _n : any + 2;
                    break;
                }
                var before = idx > 0 && IsWhite(_s[idx - 1]);
                var after = idx + 2 >= _n || IsWhite(_s[idx + 2]);
                if (before && after)
                {
                    _pos = idx + 2;
                    break;
                }
                idx += 2;
            }
            return parameters;
        }

        private byte[] ReadString()
        {
            var output = new List<byte>();
            var depth = 0;
            var i = _pos;
            while (i < _n)
            {
                var c = _s[i];
                if (c == '\\')
                {
                    i++;
                    if (i >= _n)
                        break;
                    var e = _s[i];
                    switch (e)
                    {
                        case 'n': output.Add((byte)'\n'); i++; continue;
                        case 'r': output.Add((byte)'\r'); i++; continue;
                        case 't': output.Add((byte)'\t'); i++; continue;
                        case 'b': output.Add((byte)'\b'); i++; continue;
                        case 'f': output.Add((byte)'\f'); i++; continue;
                        case '(' or ')' or '\\': output.Add((byte)e); i++; continue;
                        case '\r':
                            i++;
                            if (i < _n && _s[i] == '\n')
                                i++;
                            continue;
                        case '\n':
                            i++;
                            continue;
                    }
                    if (e >= '0' && e <= '7')
                    {
                        var j = i;
                        while (j < _n && j < i + 3 && _s[j] >= '0' && _s[j] <= '7')
                            j++;
                        output.Add((byte)(Convert.ToInt32(_s.Substring(i, j - i), 8) & 0xFF));
                        i = j;
                        continue;
                    }
                    output.Add((byte)e);
                    i++;
                    continue;
                }
                if (c == '(')
                {
                    depth++;
                    if (depth > 1)
                        output.Add((byte)c);
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        _pos = i + 1;
                        return output.ToArray();
                    }
                    output.Add((byte)c);
                }
                else
                {
                    output.Add((byte)c);
                }
                i++;
            }
            _pos = _n;
            return output.ToArray();
        }

        private static string DecodeName(string raw) =>
            Regex.Replace(raw, "#([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

        private static byte[] DecodeHex(string raw)
        {
            var digits = new string(raw.Where(Uri.IsHexDigit).ToArray());
            if (digits.Length % 2 == 1)
                digits += "0";
            return Convert.FromHexString(digits);
        }
    }
}
