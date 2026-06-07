using System.Text.RegularExpressions;

namespace ReviewScope.Domain.Mermaid;

/// <summary>
/// A pragmatic parser for the common subset of Mermaid <c>flowchart</c>/<c>graph</c> syntax:
/// a direction header, node declarations with the standard bracket shapes, and edges
/// (<c>--&gt;</c>, <c>---</c>, <c>-.-&gt;</c>, <c>==&gt;</c>) with optional labels in either the
/// <c>A --&gt;|label| B</c> or <c>A -- label --&gt; B</c> form. Chains (<c>A --&gt; B --&gt; C</c>)
/// are expanded into individual edges.
///
/// Out of scope (intentionally, for now): subgraphs (their <c>subgraph</c>/<c>end</c> wrappers are
/// skipped but the edges inside are still imported, flattened), <c>&amp;</c> fan-out groups,
/// <c>style</c>/<c>classDef</c>/<c>click</c>/<c>linkStyle</c> directives (ignored), and non-flowchart
/// diagram types (sequence/class/gantt — <see cref="TryParse"/> returns false).
/// </summary>
public static class MermaidFlowchartParser
{
    // A node token: an id followed by an optional bracketed shape. Longest bracket forms first so
    // e.g. "[[" wins over "[". IgnorePatternWhitespace keeps the alternation readable.
    private static readonly Regex NodeToken = new(
        """
        ^\s*
        (?<id>[A-Za-z0-9_.\-]+)
        (?<shape>
              \[\[(?<l>.*)\]\]
            | \[\((?<l>.*)\)\]
            | \(\((?<l>.*)\)\)
            | \(\[(?<l>.*)\]\)
            | \{\{(?<l>.*)\}\}
            | \[(?<l>.*)\]
            | \((?<l>.*)\)
            | \{(?<l>.*)\}
            | >(?<l>.*)\]
        )?
        \s*$
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline | RegexOptions.Compiled);

    // One connector, optionally followed by a |piped label|. Longest/most-specific first.
    private static readonly Regex Connector = new(
        @"(?<conn>-\.->|-\.-|-->|---|==>|===)(?:\|(?<lbl>[^|]*)\|)?",
        RegexOptions.Compiled);

    // Inline-label normalisers: rewrite "A -- label --> B" into "A -->|label| B" so the connector
    // splitter only has to deal with the piped form. Dotted/thick handled before solid.
    private static readonly Regex InlineDotted = new(@"-\.\s*([^.>|][^|]*?)\s*\.->", RegexOptions.Compiled);
    private static readonly Regex InlineThick = new(@"==\s*([^=>|][^|]*?)\s*==>", RegexOptions.Compiled);
    private static readonly Regex InlineSolid = new(@"--\s*([^->|][^|]*?)\s*-->", RegexOptions.Compiled);

    private static readonly Regex HeaderLine = new(
        @"^\s*(flowchart|graph)\s+(?<dir>TB|TD|BT|RL|LR)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Tries to parse <paramref name="text"/> as a flowchart. Returns false when the text
    /// is not a flowchart/graph diagram or yields no nodes.</summary>
    public static bool TryParse(string? text, out MermaidFlowchart chart)
    {
        chart = MermaidFlowchart.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        // Find the header; bail if this clearly isn't a flowchart (e.g. sequenceDiagram).
        MermaidDirection direction = MermaidDirection.TopDown;
        int firstContent = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = StripComment(lines[i]).Trim();
            if (t.Length == 0) continue;
            firstContent = i;
            var header = HeaderLine.Match(t);
            if (header.Success)
                direction = ParseDirection(header.Groups["dir"].Value);
            else if (LooksLikeOtherDiagram(t))
                return false;
            break;
        }
        if (firstContent < 0) return false;

        var nodeOrder = new List<string>();
        var nodes = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        var edges = new List<MermaidEdge>();

        for (int i = firstContent; i < lines.Length; i++)
        {
            string raw = StripComment(lines[i]);
            // Strip a leading header on the first content line ("flowchart TD A-->B" is legal).
            if (i == firstContent)
                raw = HeaderLine.Replace(raw, string.Empty);

            foreach (var statement in raw.Split(';'))
            {
                string stmt = statement.Trim();
                if (stmt.Length == 0) continue;
                if (IsDirective(stmt)) continue;
                ParseStatement(stmt, nodeOrder, nodes, edges);
            }
        }

        if (nodeOrder.Count == 0) return false;

        var resultNodes = nodeOrder
            .Select(id => new MermaidNode(id, nodes[id].Label, nodes[id].Shape))
            .ToList();
        chart = new MermaidFlowchart(direction, resultNodes, edges);
        return true;
    }

    private static void ParseStatement(
        string stmt,
        List<string> nodeOrder,
        Dictionary<string, MutableNode> nodes,
        List<MermaidEdge> edges)
    {
        // Normalise inline labels to the piped form first.
        stmt = InlineDotted.Replace(stmt, "-.->|$1|");
        stmt = InlineThick.Replace(stmt, "==>|$1|");
        stmt = InlineSolid.Replace(stmt, "-->|$1|");

        var links = Connector.Matches(stmt);
        if (links.Count == 0)
        {
            // No connector: a standalone node declaration (or a bare id).
            RegisterNode(stmt, nodeOrder, nodes);
            return;
        }

        // Split into node segments separated by connectors; emit one edge per connector.
        int cursor = 0;
        string? previousId = null;
        for (int m = 0; m <= links.Count; m++)
        {
            int segEnd = m < links.Count ? links[m].Index : stmt.Length;
            string segment = stmt.Substring(cursor, segEnd - cursor);
            string? id = RegisterNode(segment, nodeOrder, nodes);

            if (m > 0 && previousId is not null && id is not null)
            {
                var link = links[m - 1];
                string conn = link.Groups["conn"].Value;
                string? label = link.Groups["lbl"].Success ? CleanLabel(link.Groups["lbl"].Value) : null;
                edges.Add(new MermaidEdge(previousId, id, label, LinkStyleOf(conn), conn.EndsWith('>')));
            }

            if (id is not null) previousId = id;
            if (m < links.Count) cursor = links[m].Index + links[m].Length;
        }
    }

    /// <summary>Parses a node segment, (re)registering it. Returns the node id, or null if the
    /// segment had no recognisable id.</summary>
    private static string? RegisterNode(string segment, List<string> nodeOrder, Dictionary<string, MutableNode> nodes)
    {
        segment = StripClassAssignment(segment).Trim();
        if (segment.Length == 0) return null;

        var match = NodeToken.Match(segment);
        if (!match.Success) return null;

        string id = match.Groups["id"].Value;
        bool hasShape = match.Groups["shape"].Success && match.Groups["shape"].Value.Length > 0;
        string? label = hasShape ? CleanLabel(match.Groups["l"].Value) : null;
        MermaidNodeShape? shape = hasShape ? ShapeOf(match.Groups["shape"].Value) : null;

        if (!nodes.TryGetValue(id, out var existing))
        {
            nodeOrder.Add(id);
            nodes[id] = new MutableNode(label ?? id, shape ?? MermaidNodeShape.Rectangle, hasShape);
        }
        else if (hasShape && !existing.Explicit)
        {
            // Upgrade a node first seen as a bare reference once its real shape/label appears.
            existing.Label = label ?? existing.Label;
            existing.Shape = shape ?? existing.Shape;
            existing.Explicit = true;
        }
        return id;
    }

    private static MermaidNodeShape ShapeOf(string bracket) => bracket switch
    {
        _ when bracket.StartsWith("[[", StringComparison.Ordinal) => MermaidNodeShape.Subroutine,
        _ when bracket.StartsWith("[(", StringComparison.Ordinal) => MermaidNodeShape.Cylinder,
        _ when bracket.StartsWith("((", StringComparison.Ordinal) => MermaidNodeShape.Circle,
        _ when bracket.StartsWith("([", StringComparison.Ordinal) => MermaidNodeShape.Stadium,
        _ when bracket.StartsWith("{{", StringComparison.Ordinal) => MermaidNodeShape.Hexagon,
        _ when bracket.StartsWith("[", StringComparison.Ordinal) => MermaidNodeShape.Rectangle,
        _ when bracket.StartsWith("(", StringComparison.Ordinal) => MermaidNodeShape.Rounded,
        _ when bracket.StartsWith("{", StringComparison.Ordinal) => MermaidNodeShape.Diamond,
        _ when bracket.StartsWith(">", StringComparison.Ordinal) => MermaidNodeShape.Asymmetric,
        _ => MermaidNodeShape.Rectangle
    };

    private static MermaidLinkStyle LinkStyleOf(string conn) =>
        conn.StartsWith("-.", StringComparison.Ordinal) ? MermaidLinkStyle.Dotted
        : conn.StartsWith("==", StringComparison.Ordinal) || conn.StartsWith("===", StringComparison.Ordinal) ? MermaidLinkStyle.Thick
        : MermaidLinkStyle.Solid;

    private static MermaidDirection ParseDirection(string dir) => dir.ToUpperInvariant() switch
    {
        "BT" => MermaidDirection.BottomUp,
        "LR" => MermaidDirection.LeftRight,
        "RL" => MermaidDirection.RightLeft,
        _ => MermaidDirection.TopDown // TB, TD, or unspecified
    };

    private static string CleanLabel(string label)
    {
        string s = label.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s[1..^1];
        // Mermaid line breaks → real newlines.
        s = Regex.Replace(s, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    /// <summary>Drops a trailing <c>:::className</c> class assignment from a node segment.</summary>
    private static string StripClassAssignment(string segment)
    {
        int idx = segment.IndexOf(":::", StringComparison.Ordinal);
        return idx >= 0 ? segment[..idx] : segment;
    }

    private static string StripComment(string line)
    {
        int idx = line.IndexOf("%%", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static bool IsDirective(string stmt)
    {
        string lower = stmt.ToLowerInvariant();
        return lower.StartsWith("subgraph", StringComparison.Ordinal)
            || lower == "end"
            || lower.StartsWith("direction ", StringComparison.Ordinal)
            || lower.StartsWith("style ", StringComparison.Ordinal)
            || lower.StartsWith("classdef ", StringComparison.Ordinal)
            || lower.StartsWith("class ", StringComparison.Ordinal)
            || lower.StartsWith("click ", StringComparison.Ordinal)
            || lower.StartsWith("linkstyle ", StringComparison.Ordinal);
    }

    private static bool LooksLikeOtherDiagram(string firstLine)
    {
        string lower = firstLine.ToLowerInvariant();
        foreach (var kind in new[]
                 {
                     "sequencediagram", "classdiagram", "statediagram", "erdiagram",
                     "gantt", "pie", "journey", "gitgraph", "mindmap", "timeline", "quadrantchart"
                 })
            if (lower.StartsWith(kind, StringComparison.Ordinal)) return true;
        return false;
    }

    private sealed class MutableNode(string label, MermaidNodeShape shape, bool isExplicit)
    {
        public string Label { get; set; } = label;
        public MermaidNodeShape Shape { get; set; } = shape;
        public bool Explicit { get; set; } = isExplicit;
    }
}
