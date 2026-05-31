using System.Numerics;
using System.Windows;
using ReviewScope.Domain;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using RectangleF = System.Drawing.RectangleF;
using WpfColor = System.Windows.Media.Color;

namespace ReviewScope.Canvas;

internal sealed class ConnectionRenderer
{
    private readonly DrawingContext _ctx;
    
    private const double MinAutoTangent = 45;
    private const double MaxAutoTangent = 280;
    private const double ConnectionLeadDistance = 32;

    public ConnectionRenderer(DrawingContext ctx)
    {
        _ctx = ctx;
    }

    public void DrawConnection(SceneConnectionVisual connVis, Guid? selectedConnectionId, ConnectionControlNodeKind selectedConnectionControlKind)
    {
        var conn = connVis.Connection;
        if (conn.IsDimmed) return;

        WpfColor lineColor = conn.IsSelected
            ? WpfColor.FromArgb(230, 32, 104, 192)
            : CanvasDrawingUtils.ParseColor(conn.Stroke);
        float stroke = _ctx.InvStroke(conn.IsSelected ? 3.0f : 2.1f);

        var brush = _ctx.GetBrush(lineColor);
        // Render connectors as a single clean Direct2D path geometry (cubic/quadratic
        // bezier for Curved routes, polyline for Straight/Orthogonal). Previously the
        // curve was sampled into ~64 short segments and pushed through SketchyDrawer,
        // which per-segment overshoots / bows produced a wobbly chunky stroke.
        DrawCleanConnectionPath(connVis, brush, stroke, conn.Dashed ? _ctx.DashedStroke : null);

        if (conn.ArrowKind == ConnectorArrowKind.None)
        {
            // no arrowhead
        }
        else if (conn.ArrowPosition is double t)
        {
            t = Math.Clamp(t, 0.04, 0.96);
            Point arrowPoint = CanvasDrawingUtils.EvaluateConnectionPoint(connVis, t);
            Vector2 tangent = CanvasDrawingUtils.EvaluateConnectionTangent(connVis, t);
            if (!conn.ArrowForward || conn.ArrowKind == ConnectorArrowKind.Backward) tangent = -tangent;
            DrawInlineArrow(arrowPoint, tangent, brush, stroke);
        }
        else
        {
            CanvasDrawingUtils.GetConnectionPathPoints(connVis, out Point startLead, out _, out Point endLead);
            if (conn.ArrowKind is ConnectorArrowKind.Forward or ConnectorArrowKind.Both)
                DrawArrowhead(connVis.End, endLead, brush, stroke);
            if (conn.ArrowKind is ConnectorArrowKind.Backward or ConnectorArrowKind.Both)
                DrawArrowhead(connVis.Start, startLead, brush, stroke);
        }

        if (!string.IsNullOrWhiteSpace(conn.Label))
        {
            // Place the label on the actual rendered path (curve/orthogonal/bent), not the
            // straight Start→End midpoint — otherwise it floats away from bent connectors.
            Point mid = CanvasDrawingUtils.EvaluateConnectionPoint(connVis, 0.5);
            _ctx.DrawText(conn.Label!, (float)mid.X - 60, (float)mid.Y - 10, 120, 10, WpfColor.FromRgb(83, 96, 112), sketchy: true);
        }

        if (conn.IsSelected)
            DrawConnectionControlNodes(connVis, selectedConnectionId, selectedConnectionControlKind);
    }

    public void DrawConnectionPreview(Point start, int? sourceAnchorIndex, Point end, int? targetAnchorIndex, Point? draftMid, bool draftMidBends, WpfColor color, bool isHoveringTarget)
    {
        Vector2 sourceNormal = sourceAnchorIndex is int source ? CanvasDrawingUtils.GetConnectionAnchorNormal(source) : Vector2.Zero;
        Vector2 targetNormal = targetAnchorIndex is int target ? CanvasDrawingUtils.GetConnectionAnchorNormal(target) : Vector2.Zero;
        Point startLead = new(start.X + sourceNormal.X * CanvasDrawingUtils.ConnectionLeadDistance, start.Y + sourceNormal.Y * CanvasDrawingUtils.ConnectionLeadDistance);
        Point endLead = new(end.X + targetNormal.X * CanvasDrawingUtils.ConnectionLeadDistance, end.Y + targetNormal.Y * CanvasDrawingUtils.ConnectionLeadDistance);
        Point mid = draftMid ?? new Point((startLead.X + endLead.X) / 2, (startLead.Y + endLead.Y) / 2);
        Point c1;
        Point c2;
        if (draftMid is not null && draftMidBends)
        {
            Point control = CanvasDrawingUtils.GetQuadraticControlThroughMid(startLead, mid, endLead);
            c1 = new(startLead.X + (control.X - startLead.X) * 2 / 3, startLead.Y + (control.Y - startLead.Y) * 2 / 3);
            c2 = new(endLead.X + (control.X - endLead.X) * 2 / 3, endLead.Y + (control.Y - endLead.Y) * 2 / 3);
        }
        else
        {
            double dx = endLead.X - startLead.X;
            double dy = endLead.Y - startLead.Y;
            double tangent = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) * 0.42, CanvasDrawingUtils.MinAutoTangent, CanvasDrawingUtils.MaxAutoTangent);
            c1 = new(startLead.X + sourceNormal.X * tangent, startLead.Y + sourceNormal.Y * tangent);
            c2 = new(endLead.X + targetNormal.X * tangent, endLead.Y + targetNormal.Y * tangent);
        }

        float strokeW = _ctx.InvStroke(isHoveringTarget ? 2.8f : 2.0f);
        var brush = _ctx.GetBrush(color);

        // Draw start lead-in, the cubic bezier, and the end lead-out as one continuous
        // Direct2D path so the preview is a smooth curve. (Previously this sampled the
        // bezier into ~16 short segments and ran them through SketchyDrawer, which
        // added per-segment overshoot + bow noise — that's why the in-flight connector
        // looked like a chunky wobbly snake.)
        using (var path = _ctx.Factory.CreatePathGeometry())
        using (var sink = path.Open())
        {
            sink.BeginFigure(new Vector2((float)start.X, (float)start.Y), FigureBegin.Hollow);
            sink.AddLine(new Vector2((float)startLead.X, (float)startLead.Y));
            sink.AddBezier(new Vortice.Direct2D1.BezierSegment
            {
                Point1 = new Vector2((float)c1.X, (float)c1.Y),
                Point2 = new Vector2((float)c2.X, (float)c2.Y),
                Point3 = new Vector2((float)endLead.X, (float)endLead.Y)
            });
            sink.AddLine(new Vector2((float)end.X, (float)end.Y));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
            _ctx.RenderTarget.DrawGeometry(path, brush, strokeW);
        }

        if (draftMid is Point p)
            DrawControlPoint(p, _ctx.GetBrush(WpfColor.FromArgb(230, 35, 162, 109)));
    }

    // Draws a committed connection as a single clean Direct2D path. Routes:
    //   - Curved      : start→startLead line + cubic/quadratic bezier + endLead→end line
    //   - Straight    : start→end line
    //   - Orthogonal  : full polyline from BuildConnectionPolyline
    private void DrawCleanConnectionPath(SceneConnectionVisual connVis, ID2D1Brush brush, float strokeWidth, ID2D1StrokeStyle? strokeStyle)
    {
        var conn = connVis.Connection;
        using var path = _ctx.Factory.CreatePathGeometry();
        using var sink = path.Open();

        if (conn.RouteKind == ConnectorRouteKind.Curved)
        {
            CanvasDrawingUtils.GetConnectionPathPoints(connVis, out Point startLead, out Point mid, out Point endLead);
            sink.BeginFigure(new Vector2((float)connVis.Start.X, (float)connVis.Start.Y), FigureBegin.Hollow);
            sink.AddLine(new Vector2((float)startLead.X, (float)startLead.Y));

            if (CanvasDrawingUtils.HasCustomConnectionMidPoint(conn) && conn.MidControlBends)
            {
                Point control = CanvasDrawingUtils.GetQuadraticControlThroughMid(startLead, mid, endLead);
                sink.AddQuadraticBezier(new QuadraticBezierSegment
                {
                    Point1 = new Vector2((float)control.X, (float)control.Y),
                    Point2 = new Vector2((float)endLead.X, (float)endLead.Y)
                });
            }
            else
            {
                CanvasDrawingUtils.GetAutoCubicControls(connVis, startLead, endLead, out Point cc1, out Point cc2);
                sink.AddBezier(new Vortice.Direct2D1.BezierSegment
                {
                    Point1 = new Vector2((float)cc1.X, (float)cc1.Y),
                    Point2 = new Vector2((float)cc2.X, (float)cc2.Y),
                    Point3 = new Vector2((float)endLead.X, (float)endLead.Y)
                });
            }
            sink.AddLine(new Vector2((float)connVis.End.X, (float)connVis.End.Y));
        }
        else
        {
            var pts = CanvasDrawingUtils.BuildConnectionPolyline(connVis);
            if (pts.Count < 2)
            {
                sink.BeginFigure(new Vector2((float)connVis.Start.X, (float)connVis.Start.Y), FigureBegin.Hollow);
                sink.AddLine(new Vector2((float)connVis.End.X, (float)connVis.End.Y));
            }
            else
            {
                sink.BeginFigure(new Vector2((float)pts[0].X, (float)pts[0].Y), FigureBegin.Hollow);
                for (int i = 1; i < pts.Count; i++)
                    sink.AddLine(new Vector2((float)pts[i].X, (float)pts[i].Y));
            }
        }

        sink.EndFigure(FigureEnd.Open);
        sink.Close();

        if (strokeStyle is not null)
            _ctx.RenderTarget.DrawGeometry(path, brush, strokeWidth, strokeStyle);
        else
            _ctx.RenderTarget.DrawGeometry(path, brush, strokeWidth);
    }

    private void DrawConnectionControlNodes(SceneConnectionVisual connVis, Guid? selectedConnectionId, ConnectionControlNodeKind selectedConnectionControlKind)
    {
        CanvasViewport.GetConnectionPathPoints(connVis, out _, out Point middleControl, out _);
        var nodeBrush = _ctx.GetBrush(selectedConnectionId == connVis.Connection.Id && selectedConnectionControlKind == ConnectionControlNodeKind.Middle
            ? WpfColor.FromArgb(240, 35, 162, 109)
            : WpfColor.FromArgb(230, 32, 104, 192));

        DrawControlPoint(middleControl, nodeBrush);
    }

    private void DrawControlPoint(Point p, ID2D1SolidColorBrush brush)
    {
        float r = Math.Max(2.5f, _ctx.InvStroke(3.2f));
        _ctx.RenderTarget.FillEllipse(new Ellipse(new Vector2((float)p.X, (float)p.Y), r, r), brush);
    }

    private void DrawArrowhead(Point tip, Point from, ID2D1SolidColorBrush brush, float stroke)
    {
        Vector2 dir = new Vector2((float)(tip.X - from.X), (float)(tip.Y - from.Y));
        float len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 0.01f) return;
        dir /= len;
        Vector2 perp = new(-dir.Y, dir.X);
        float aLen = Math.Max(11f, stroke * 5.8f);
        Vector2 tipV = new((float)tip.X, (float)tip.Y);
        Vector2 left = tipV - dir * aLen + perp * (aLen * 0.45f);
        Vector2 right = tipV - dir * aLen - perp * (aLen * 0.45f);

        using var path = _ctx.Factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(tipV, FigureBegin.Filled);
        sink.AddLine(left);
        sink.AddLine(right);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        _ctx.RenderTarget.FillGeometry(path, brush);
    }

    private void DrawInlineArrow(Point center, Vector2 tangent, ID2D1SolidColorBrush brush, float stroke)
    {
        float len = tangent.Length();
        if (len < 0.001f) return;
        Vector2 dir = tangent / len;
        Point from = new(center.X - dir.X * 1.0, center.Y - dir.Y * 1.0);
        DrawArrowhead(center, from, brush, stroke);
    }

}
