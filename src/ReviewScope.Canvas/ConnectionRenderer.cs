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
        var points = SampleConnectionPoints(connVis).ToArray();
        SketchyDrawer.DrawPolygon(_ctx.RenderTarget, points, null, brush, stroke, conn.Id.ToString(), close: false, strokeStyle: conn.Dashed ? _ctx.DashedStroke : null);

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
            Point mid = new((connVis.Start.X + connVis.End.X) / 2, (connVis.Start.Y + connVis.End.Y) / 2);
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

        List<Vector2> previewPoints = new();
        previewPoints.Add(new Vector2((float)start.X, (float)start.Y));
        previewPoints.Add(new Vector2((float)startLead.X, (float)startLead.Y));
        int bezierSamples = 16;
        for (int i = 1; i < bezierSamples; i++)
        {
            float t = i / (float)bezierSamples;
            float omt = 1f - t;
            float xVal = omt * omt * omt * (float)startLead.X +
                         3f * omt * omt * t * (float)c1.X +
                         3f * omt * t * t * (float)c2.X +
                         t * t * t * (float)endLead.X;
            float yVal = omt * omt * omt * (float)startLead.Y +
                         3f * omt * omt * t * (float)c1.Y +
                         3f * omt * t * t * (float)c2.Y +
                         t * t * t * (float)endLead.Y;
            previewPoints.Add(new Vector2(xVal, yVal));
        }
        previewPoints.Add(new Vector2((float)endLead.X, (float)endLead.Y));
        previewPoints.Add(new Vector2((float)end.X, (float)end.Y));

        float strokeW = _ctx.InvStroke(isHoveringTarget ? 2.8f : 2.0f);
        var brush = _ctx.GetBrush(color);
        SketchyDrawer.DrawPolygon(_ctx.RenderTarget, previewPoints.ToArray(), null, brush, strokeW, "connection_preview", close: false);
        if (draftMid is Point p)
            DrawControlPoint(p, _ctx.GetBrush(WpfColor.FromArgb(230, 35, 162, 109)));
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

    private static IReadOnlyList<Vector2> SampleConnectionPoints(SceneConnectionVisual connVis)
    {
        const int samples = 64;
        var points = new Vector2[samples];
        for (int i = 0; i < samples; i++)
        {
            Point p = CanvasDrawingUtils.EvaluateConnectionPoint(connVis, i / (double)(samples - 1));
            points[i] = new Vector2((float)p.X, (float)p.Y);
        }
        return points;
    }
}
