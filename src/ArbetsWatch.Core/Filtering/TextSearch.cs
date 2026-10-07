using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ArbetsWatch.Core.Filtering;

/// <summary>
/// Free-text search over an ad's title and description. Matching is case-insensitive substring matching, so
/// "utvecklare" finds "Systemutvecklare" (Swedish compounds). Every term must occur; a quoted phrase must occur
/// as written. Search narrows the list only: it never affects which ads count as new.
/// </summary>
public sealed record TextSearch
{
    /// <summary>Longest search text accepted; longer input is cut (a pasted ad shouldn't become 200 terms).</summary>
    public const int MaxLength = 200;

    /// <summary>At most this many terms are used.</summary>
    public const int MaxTerms = 8;

    public static TextSearch None { get; } = new([]);

    private TextSearch(IReadOnlyList<string> terms) => Terms = terms;

    /// <summary>Normalized terms; all must occur in <see cref="Body"/>.</summary>
    public IReadOnlyList<string> Terms { get; }

    public bool IsEmpty => Terms.Count == 0;

    /// <summary>
    /// Splits the input on whitespace into terms; <c>"…"</c> keeps a phrase together. Empty input searches nothing.
    /// </summary>
    public static TextSearch Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        var input = text.Length > MaxLength ? text[..MaxLength] : text;
        var terms = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        void Flush()
        {
            var term = Fold(current.ToString());
            if (term.Length > 0 && !terms.Contains(term, StringComparer.Ordinal))
            {
                terms.Add(term);
            }

            current.Clear();
        }

        foreach (var c in input)
        {
            if (c == '"')
            {
                Flush();
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return terms.Count == 0 ? None : new TextSearch(terms.Take(MaxTerms).ToList());
    }

    /// <summary>The searchable text stored for an ad: title and description, folded like the terms.</summary>
    public static string Body(string? headline, string? description) =>
        Fold(string.IsNullOrWhiteSpace(description) ? headline ?? string.Empty : $"{headline}\n{description}");

    /// <summary>The in-memory form of <see cref="Where"/> (tested for equivalence).</summary>
    public bool Matches(string body) => Terms.All(t => body.Contains(t, StringComparison.Ordinal));

    /// <summary>
    /// A boolean SQL expression over the stored body column (<paramref name="bodyColumn"/>, e.g. <c>t.body</c>);
    /// parameters are added to <paramref name="command"/>. An empty search is <c>1</c>.
    /// </summary>
    public string Where(SqliteCommand command, string bodyColumn)
    {
        if (IsEmpty)
        {
            return "1";
        }

        var parts = new List<string>(Terms.Count);
        foreach (var term in Terms)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"$q{command.Parameters.Count}");
            command.Parameters.AddWithValue(name, term);
            parts.Add($"instr({bodyColumn}, {name}) > 0");
        }

        return "(" + string.Join(" AND ", parts) + ")";
    }

    public bool Equals(TextSearch? other) => other is not null && Terms.SequenceEqual(other.Terms, StringComparer.Ordinal);

    public override int GetHashCode() => Terms.Count == 0 ? 0 : HashCode.Combine(Terms.Count, Terms[0]);

    /// <summary>
    /// Lower case (invariant, so Å/Ä/Ö fold too), composed Unicode, and runs of whitespace as one space, so the
    /// same words match however the ad was typed.
    /// </summary>
    private static string Fold(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var result = new StringBuilder(normalized.Length);
        var space = false;
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                space = result.Length > 0;
                continue;
            }

            if (space)
            {
                result.Append(' ');
                space = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}
