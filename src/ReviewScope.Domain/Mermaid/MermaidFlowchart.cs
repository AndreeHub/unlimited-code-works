namespace ReviewScope.Domain.Mermaid;

/// <summary>Flow direction declared in the diagram header (e.g. <c>flowchart TD</c>).</summary>
public enum MermaidDirection { TopDown, BottomUp, LeftRight, RightLeft }

/// <summary>The node-bracket shapes Mermaid flowcharts support. Mapped onto the canvas's own
/// <c>ShapeType</c> strings when the diagram is imported.</summary>
public enum MermaidNodeShape
{
    Rectangle,   // A[text]
    Rounded,     // A(text)
    Stadium,     // A([text])
    Subroutine,  // A[[text]]
    Cylinder,    // A[(text)]   -> database
    Circle,      // A((text))
    Diamond,     // A{text}     -> decision
    Hexagon,     // A{{text}}
    Asymmetric   // A>text]
}

/// <summary>Line style of an edge: solid <c>--&gt;</c>, dotted <c>-.-&gt;</c>, or thick <c>==&gt;</c>.</summary>
public enum MermaidLinkStyle { Solid, Dotted, Thick }

public sealed record MermaidNode(string Id, string Label, MermaidNodeShape Shape);

public sealed record MermaidEdge(
    string SourceId,
    string TargetId,
    string? Label,
    MermaidLinkStyle Style,
    bool HasArrow);

/// <summary>A parsed Mermaid flowchart: the declared direction plus the nodes (in first-seen order)
/// and the edges between them. Geometry-free — laying it out is a separate step.</summary>
public sealed record MermaidFlowchart(
    MermaidDirection Direction,
    IReadOnlyList<MermaidNode> Nodes,
    IReadOnlyList<MermaidEdge> Edges)
{
    public bool IsEmpty => Nodes.Count == 0;

    public static MermaidFlowchart Empty { get; } =
        new(MermaidDirection.TopDown, Array.Empty<MermaidNode>(), Array.Empty<MermaidEdge>());
}
