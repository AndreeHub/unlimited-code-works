using CommunityToolkit.Mvvm.Input;
using ReviewScope.App.Mermaid;
using ReviewScope.Domain;
using ReviewScope.Domain.Mermaid;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ReviewScope.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // -----------------------------------------------------------------------
    // Mermaid import — paste a `flowchart` definition and recreate it as native, editable canvas
    // blocks + connectors. Positions come from MSAGL's layered layout; everything that lands is a
    // first-class RenderBlock / RenderConnection, so nodes drag and edges re-route afterwards.
    // -----------------------------------------------------------------------

    private static readonly Typeface MermaidTypeface = new("Segoe UI");
    private const double MermaidFontSize = 13;

    /// <summary>Labels wider than this wrap onto multiple lines instead of stretching the box.</summary>
    private const double MermaidMaxTextWidth = 200;

    /// <summary>Reads a Mermaid flowchart from the clipboard and drops it onto the canvas. No-op
    /// (with a status message) when the clipboard isn't a parseable flowchart.</summary>
    [RelayCommand]
    public async Task PasteMermaidDiagramAsync()
    {
        if (!IsCanvasDocumentActive) return;
        if (!Clipboard.ContainsText())
        {
            StatusMessage = "Clipboard has no text to read a Mermaid diagram from.";
            return;
        }
        await ImportMermaidTextAsync(Clipboard.GetText());
    }

    /// <summary>Parses <paramref name="text"/> as a Mermaid flowchart, lays it out, and appends the
    /// result to the scene. When <paramref name="worldX"/>/<paramref name="worldY"/> are supplied
    /// (paste-at-cursor) the laid-out group is centered there; otherwise it cascades into open space.
    /// Returns false (with a status message) if it isn't a flowchart.</summary>
    public async Task<bool> ImportMermaidTextAsync(string text, double? worldX = null, double? worldY = null)
    {
        if (!IsCanvasDocumentActive) return false;
        if (!MermaidFlowchartParser.TryParse(text, out var chart) || chart.IsEmpty)
        {
            StatusMessage = "Couldn't read a Mermaid flowchart from that text.";
            return false;
        }

        IReadOnlyDictionary<string, MermaidLayoutEngine.NodeRect> layout;
        try
        {
            layout = MermaidLayoutEngine.Layout(chart, MeasureMermaidNode);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Mermaid layout failed: {ex.Message}";
            return false;
        }

        // Bounding box of the laid-out group, then find an open spot for it on the canvas.
        double groupW = layout.Count == 0 ? 0 : layout.Values.Max(r => r.X + r.Width);
        double groupH = layout.Count == 0 ? 0 : layout.Values.Max(r => r.Y + r.Height);
        var (originX, originY) = worldX.HasValue && worldY.HasValue
            ? (worldX.Value - groupW / 2, worldY.Value - groupH / 2)
            : FindOpenCanvasPlacement(groupW, groupH);

        int z = NextBlockZIndex();
        var keyByNodeId = new Dictionary<string, string>(StringComparer.Ordinal);
        var newBlocks = new List<RenderBlock>(chart.Nodes.Count);

        foreach (var node in chart.Nodes)
        {
            if (!layout.TryGetValue(node.Id, out var rect)) continue;
            var id = Guid.NewGuid();
            string key = $"shape::{id:N}";
            keyByNodeId[node.Id] = key;
            string shapeType = ToCanvasShapeType(node.Shape);
            string label = string.IsNullOrWhiteSpace(node.Label) ? node.Id : node.Label;

            newBlocks.Add(new RenderBlock(
                id, key, BlockKind.Shape,
                label.Replace('\n', ' '), string.Empty,
                originX + rect.X, originY + rect.Y, rect.Width, rect.Height,
                Body: label,
                IsSelected: true,
                ZIndex: z++,
                LayerKey: "layer::architecture",
                ShapeType: shapeType,
                // Solid fill (not the default diagonal hatch) + crisp text → a clean, readable diagram.
                Style: ResolveShapeStyle(shapeType) with { FillStyle = "solid" }));
        }

        var newConnections = new List<RenderConnection>(chart.Edges.Count);
        foreach (var edge in chart.Edges)
        {
            if (!keyByNodeId.TryGetValue(edge.SourceId, out var sourceKey)) continue;
            if (!keyByNodeId.TryGetValue(edge.TargetId, out var targetKey)) continue;

            // Anchor each end on the side facing the other node so the spline leaves/arrives
            // along the flow (bottom→top for a top-down chain) instead of bulging sideways.
            int? sourceAnchor = null, targetAnchor = null;
            if (layout.TryGetValue(edge.SourceId, out var sr) && layout.TryGetValue(edge.TargetId, out var tr))
            {
                var (sa, ta) = ResolveMermaidAnchors(sr, tr);
                sourceAnchor = sa;
                targetAnchor = ta;
            }

            newConnections.Add(new RenderConnection(
                Guid.NewGuid(), sourceKey, targetKey,
                Label: string.IsNullOrWhiteSpace(edge.Label) ? null : edge.Label,
                SourceAnchorIndex: sourceAnchor,
                TargetAnchorIndex: targetAnchor,
                RouteKind: ConnectorRouteKind.Curved,
                ArrowKind: edge.HasArrow ? ConnectorArrowKind.Forward : ConnectorArrowKind.None,
                Dashed: edge.Style == MermaidLinkStyle.Dotted));
        }

        var scene = Scene with
        {
            Blocks = Scene.Blocks.Select(b => b with { IsSelected = false }).Concat(newBlocks).ToList(),
            Connections = Scene.Connections.Select(c => c with { IsSelected = false }).Concat(newConnections).ToList()
        };
        SetSceneFromUserAction(scene, $"Imported Mermaid diagram ({newBlocks.Count} nodes, {newConnections.Count} edges)");
        await PersistSessionAsync();
        return true;
    }

    /// <summary>
    /// Picks the connection anchor index on each block for an edge, based on the relative position
    /// of the two nodes. Anchor indices follow the canvas convention (side = index / 4: 0=top,
    /// 1=right, 2=bottom, 3=left); the chosen pairs are aligned so a straight chain stays straight.
    /// </summary>
    private static (int Source, int Target) ResolveMermaidAnchors(
        MermaidLayoutEngine.NodeRect s, MermaidLayoutEngine.NodeRect t)
    {
        double dx = (t.X + t.Width / 2) - (s.X + s.Width / 2);
        double dy = (t.Y + t.Height / 2) - (s.Y + s.Height / 2);
        if (Math.Abs(dy) >= Math.Abs(dx))
            return dy >= 0 ? (10, 1) : (1, 10);   // bottom→top  /  top→bottom
        return dx >= 0 ? (5, 14) : (14, 5);        // right→left  /  left→right
    }

    /// <summary>Maps a Mermaid bracket shape onto one of the canvas's own shape types.</summary>
    private static string ToCanvasShapeType(MermaidNodeShape shape) => shape switch
    {
        MermaidNodeShape.Cylinder => "database",
        MermaidNodeShape.Circle => "circle",
        MermaidNodeShape.Diamond => "decision",
        MermaidNodeShape.Hexagon => "hexagon",
        _ => "rectangle" // Rectangle, Rounded, Stadium, Subroutine, Asymmetric
    };

    /// <summary>
    /// Sizes a node from its label so the text isn't clipped. Long labels wrap to a sensible max
    /// width and the box grows taller instead of stretching into one huge line. Diamonds and circles
    /// get extra room because the text sits inside an inscribed shape.
    /// </summary>
    private static (double Width, double Height) MeasureMermaidNode(MermaidNode node)
    {
        string label = string.IsNullOrWhiteSpace(node.Label) ? node.Id : node.Label;
        var (lineWidth, lineHeight) = MeasureLabel(label);

        int lines = Math.Max(1, (int)Math.Ceiling(lineWidth / MermaidMaxTextWidth));
        double contentW = lines == 1 ? lineWidth : MermaidMaxTextWidth;
        double contentH = lines * lineHeight;

        return node.Shape switch
        {
            MermaidNodeShape.Diamond => (
                Clamp(contentW * 1.4 + 52, 140, 320),
                Clamp(contentH * 1.8 + 34, 96, 260)),
            MermaidNodeShape.Circle => (
                Clamp(Math.Max(contentW, contentH) + 56, 104, 240),
                Clamp(Math.Max(contentW, contentH) + 56, 104, 240)),
            MermaidNodeShape.Hexagon => (
                Clamp(contentW + 56, 130, 320),
                Clamp(contentH + 30, 64, 240)),
            MermaidNodeShape.Cylinder => (
                Clamp(contentW + 44, 120, 300),
                Clamp(contentH + 48, 80, 260)),
            _ => (
                Clamp(contentW + 40, 120, 280),
                Clamp(contentH + 26, 56, 240))
        };
    }

    /// <summary>Measures the label as a single line, returning its width and one-line height.
    /// The node sizer uses these to decide how many wrapped lines a box needs.</summary>
    private static (double LineWidth, double LineHeight) MeasureLabel(string label)
    {
        string oneLine = label.Replace('\n', ' ');
        var ft = new FormattedText(
            oneLine.Length == 0 ? " " : oneLine,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            MermaidTypeface,
            MermaidFontSize,
            Brushes.Black,
            1.0);
        return (ft.WidthIncludingTrailingWhitespace, ft.Height);
    }

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
}
