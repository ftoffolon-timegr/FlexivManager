using System.Text;

namespace RobotGateway.Core;

/// <summary>One valid line of an ERP file.</summary>
public sealed record ParsedArticle(string Code, string RawCode);

/// <summary>A line that cannot become a code (too long or with characters a barcode cannot carry).</summary>
public sealed record RejectedLine(int LineNumber, string Text, string Reason);

public sealed record ParsedArticleFile(IReadOnlyList<ParsedArticle> Codes, IReadOnlyList<RejectedLine> Rejected);

/// <summary>
/// ERP article file: plain text, one article code per line. The code is an opaque string: no format check,
/// only blank lines are skipped, duplicates are merged, and a line that could never be used as a product code
/// (over <see cref="ProductCode.MaxLength"/> characters, or non-ASCII) is rejected and reported.
/// </summary>
public static class ArticleFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 (with or without BOM) or UTF-16 with BOM; anything that is not valid UTF-8 is read as Latin-1.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        var start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        try { return StrictUtf8.GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static ParsedArticleFile Parse(string text)
    {
        var codes = new List<ParsedArticle>();
        var rejected = new List<RejectedLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = Clean(lines[i]);
            if (raw.Length == 0)
                continue;
            var code = ProductCode.Normalize(raw);
            if (ProductCode.Validate(code) is { } reason)
            {
                rejected.Add(new RejectedLine(i + 1, raw.Length > 80 ? raw[..80] + "…" : raw, reason));
                continue;
            }
            if (seen.Add(code))
                codes.Add(new ParsedArticle(code, raw));
        }
        return new ParsedArticleFile(codes, rejected);
    }

    /// <summary>Removes control characters (CR, TAB…) and surrounding blanks; case and inner text are kept.</summary>
    private static string Clean(string line) =>
        new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim();
}
