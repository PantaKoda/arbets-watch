using System.Text.RegularExpressions;

namespace ArbetsWatch.Core.Updates;

/// <summary>
/// Turns release notes (GitHub Markdown, untrusted) into readable plain text: headings lose their '#', list
/// markers become bullets, emphasis and link syntax are removed. Nothing is ever interpreted as markup.
/// </summary>
public static partial class ReleaseNotesText
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "No notes were published for this release.";
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line =>
        {
            var text = Heading().Replace(line, string.Empty);
            text = Bullet().Replace(text, "$1• ");
            text = Link().Replace(text, "$1");
            return text.Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal)
                .TrimEnd();
        });
        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();
}
