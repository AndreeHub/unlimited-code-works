using Microsoft.Msagl.Core;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using ReviewScope.Domain.Mermaid;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;

namespace ReviewScope.App.Mermaid;

/// <summary>
/// Lays out a parsed <see cref="MermaidFlowchart"/> using MSAGL's Sugiyama layered algorithm —
/// the same family of layered DAG layout that Mermaid's own engine (dagre) uses. We consume only
/// the resulting node positions; edges are drawn natively by the canvas (key-bound connectors), so
/// the imported diagram stays fully editable.
///
/// Output is in a normalised world space whose top-left is (0,0); the caller offsets the whole
/// group onto the canvas.
/// </summary>
public static class MermaidLayoutEngine
{
    public readonly record struct NodeRect(double X, double Y, double Width, double Height);

    private const double NodeSeparation = 42;
    private const double LayerSeparation = 58;

    /// <summary>
    /// Computes a position+size rectangle (top-left origin) for every node id in the chart.
    /// <paramref name="sizeOf"/> supplies each node's measured pixel size (the caller measures
    /// labels with the platform's text engine).
    /// </summary>
    public static IReadOnlyDictionary<string, NodeRect> Layout(
        MermaidFlowchart chart,
        Func<MermaidNode, (double Width, double Height)> sizeOf)
    {
        bool horizontal = chart.Direction is MermaidDirection.LeftRight or MermaidDirection.RightLeft;

        var graph = new GeometryGraph();
        var msaglNodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, (double W, double H)>(StringComparer.Ordinal);

        foreach (var node in chart.Nodes)
        {
            var (w, h) = sizeOf(node);
            sizes[node.Id] = (w, h);
            // For LR/RL we lay out top-down with swapped extents, then swap axes back. That keeps
            // layer spacing tied to widths and within-layer spacing tied to heights, so nodes don't
            // overlap after the swap — MSAGL itself only ever does a vertical layered pass here.
            var (feedW, feedH) = horizontal ? (h, w) : (w, h);
            var msagl = new Node(CurveFactory.CreateRectangle(feedW, feedH, new MsaglPoint(0, 0)), node.Id);
            graph.Nodes.Add(msagl);
            msaglNodes[node.Id] = msagl;
        }

        foreach (var edge in chart.Edges)
        {
            if (edge.SourceId == edge.TargetId) continue; // self-loops break layered ranking
            if (msaglNodes.TryGetValue(edge.SourceId, out var s) && msaglNodes.TryGetValue(edge.TargetId, out var t))
                graph.Edges.Add(new Edge(s, t));
        }

        var settings = new SugiyamaLayoutSettings
        {
            NodeSeparation = NodeSeparation,
            LayerSeparation = LayerSeparation
        };
        LayoutHelpers.CalculateLayout(graph, settings, new CancelToken(), null);

        // Read centres in MSAGL space, mapping into our world axes.
        var centers = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        foreach (var (id, msagl) in msaglNodes)
        {
            MsaglPoint c = msagl.Center;
            // Vertical: layer axis = Y. Horizontal: swap so layer axis (MSAGL Y) becomes world X.
            centers[id] = horizontal ? (c.Y, c.X) : (c.X, c.Y);
        }

        OrientToDirection(chart, centers, horizontal);

        // Normalise to a top-left origin and convert centres → top-left rects.
        double minX = double.MaxValue, minY = double.MaxValue;
        foreach (var (id, c) in centers)
        {
            var (w, h) = sizes[id];
            minX = Math.Min(minX, c.X - w / 2);
            minY = Math.Min(minY, c.Y - h / 2);
        }

        var result = new Dictionary<string, NodeRect>(StringComparer.Ordinal);
        foreach (var node in chart.Nodes)
        {
            var (w, h) = sizes[node.Id];
            var c = centers[node.Id];
            result[node.Id] = new NodeRect(c.X - w / 2 - minX, c.Y - h / 2 - minY, w, h);
        }
        return result;
    }

    /// <summary>
    /// Flips the primary (depth) axis so edges flow the way the header asks: down for TD, right for
    /// LR, up for BT, left for RL. We don't trust MSAGL's internal up/down convention — instead we
    /// align it to the actual edge directions (most arrows should point along +primary), then invert
    /// for the reversed directions.
    /// </summary>
    private static void OrientToDirection(
        MermaidFlowchart chart,
        Dictionary<string, (double X, double Y)> centers,
        bool horizontal)
    {
        double Primary((double X, double Y) c) => horizontal ? c.X : c.Y;

        int signSum = 0;
        foreach (var edge in chart.Edges)
        {
            if (edge.SourceId == edge.TargetId) continue;
            if (centers.TryGetValue(edge.SourceId, out var u) && centers.TryGetValue(edge.TargetId, out var v))
                signSum += Math.Sign(Primary(v) - Primary(u));
        }

        bool reverseHeader = chart.Direction is MermaidDirection.BottomUp or MermaidDirection.RightLeft;
        // First make arrows point along +primary (flip if they mostly point the other way), then
        // invert for BT/RL which want the opposite flow.
        bool flip = (signSum < 0) ^ reverseHeader;
        if (!flip) return;

        foreach (var id in centers.Keys.ToList())
        {
            var c = centers[id];
            centers[id] = horizontal ? (-c.X, c.Y) : (c.X, -c.Y);
        }
    }
}
