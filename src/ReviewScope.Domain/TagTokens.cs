using System.Text;

namespace ReviewScope.Domain;

public enum TagTokenKind { Tag, WikiLink }

public readonly record struct TagToken(TagTokenKind Kind, int Start, int Length, string Value);

/// <summary>
/// Logseq-style #tag and [[wiki link]] tokenizer used by the outline note editor.
/// Scans any text once and emits non-overlapping tokens.
/// </summary>
public static class TagTokens
{
    /// <summary>Characters allowed inside a #tag (after the leading #).</summary>
    public static bool IsTagChar(char c) =>
        char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '/';

    /// <summary>The # must sit at the start of input or be preceded by whitespace, punctuation, or another delimiter — otherwise it's a Markdown header or part of a URL fragment.</summary>
    public static bool IsTagStartBoundary(string text, int hashIndex)
    {
        if (hashIndex <= 0) return true;
        char prev = text[hashIndex - 1];
        return char.IsWhiteSpace(prev) || prev == '(' || prev == '[' || prev == ',';
    }

    public static IEnumerable<TagToken> Scan(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '#' && IsTagStartBoundary(text, i))
            {
                int valueStart = i + 1;
                int j = valueStart;
                while (j < text.Length && IsTagChar(text[j])) j++;
                if (j > valueStart)
                {
                    yield return new TagToken(TagTokenKind.Tag, i, j - i, text[valueStart..j]);
                    i = j;
                    continue;
                }
            }
            else if (c == '[' && i + 1 < text.Length && text[i + 1] == '[')
            {
                int valueStart = i + 2;
                int j = text.IndexOf("]]", valueStart, StringComparison.Ordinal);
                // Reject links that span a newline so a stray "[[" doesn't swallow the rest of the doc.
                if (j > valueStart)
                {
                    int nl = text.IndexOf('\n', valueStart, j - valueStart);
                    if (nl < 0)
                    {
                        string value = text[valueStart..j].Trim();
                        if (value.Length > 0)
                        {
                            yield return new TagToken(TagTokenKind.WikiLink, i, j + 2 - i, value);
                            i = j + 2;
                            continue;
                        }
                    }
                }
            }

            i++;
        }
    }

    /// <summary>
    /// Pull out the distinct, case-insensitively-deduplicated tag and wiki-link values
    /// from a body of text. Order matches first-occurrence so the inspector chips read
    /// in author order.
    /// </summary>
    public static (IReadOnlyList<string> Tags, IReadOnlyList<string> WikiLinks) Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return (Array.Empty<string>(), Array.Empty<string>());

        var tagsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linksSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();
        var links = new List<string>();
        foreach (var token in Scan(text))
        {
            if (token.Kind == TagTokenKind.Tag)
            {
                if (tagsSeen.Add(token.Value)) tags.Add(token.Value);
            }
            else
            {
                if (linksSeen.Add(token.Value)) links.Add(token.Value);
            }
        }
        return (tags, links);
    }

    /// <summary>
    /// Look backwards from the caret to detect whether the user is currently typing a
    /// #tag or [[wiki link]] token. Returns the trigger character ('#' or '['), the
    /// position of the trigger character itself, and the prefix already typed.
    /// </summary>
    public static AutocompleteContext? DetectAutocomplete(string text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length) return null;

        // [[wiki: scan back for an unmatched "[[" on the current line.
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '\n') break;
            if (c == ']') break; // closed elsewhere — not in an active link
            if (c == '[' && i - 1 >= 0 && text[i - 1] == '[')
            {
                int prefixStart = i + 1;
                string prefix = text[prefixStart..caret];
                if (prefix.Contains('[') || prefix.Contains(']')) return null;
                return new AutocompleteContext(TagTokenKind.WikiLink, i - 1, prefix);
            }
        }

        // #tag: walk back over tag characters to find a '#' triggered at a word boundary.
        int k = caret - 1;
        while (k >= 0 && IsTagChar(text[k])) k--;
        if (k >= 0 && text[k] == '#' && IsTagStartBoundary(text, k))
        {
            string prefix = text[(k + 1)..caret];
            return new AutocompleteContext(TagTokenKind.Tag, k, prefix);
        }

        return null;
    }
}

public readonly record struct AutocompleteContext(TagTokenKind Kind, int TriggerIndex, string Prefix);
