using ReviewScope.Domain.Mermaid;
using Xunit;

namespace ReviewScope.Domain.Tests;

public class MermaidFlowchartParserTests
{
    // The user's real example: a top-down flowchart with quoted rectangle labels, fan-out
    // (E -> F and E -> G), and a join-free tree of arrows.
    private const string UserExample = """
        flowchart TD
            A["User clicks View Range Manager ribbon / launcher"] --> B["ViewRangeInspectorCommand.Execute"]
            B --> C["MagicServices.GetService<ViewRangeController>()"]
            C --> D["ViewRangeController.Launch(UIApplication)"]
            D --> E["Initialize(uiApp): stores UIApplication + Document"]
            E --> F["BuildOpenData()"]
            F --> F1["GetViewGroups(): floor + ceiling ViewPlan DTOs"]
            F --> F2["GetAllLevels(): project Level DTOs"]
            E --> G["BuildWiring(): creates callback delegates"]
            G --> H["MagicTools_UI_Controller.LaunchViewRangeManagerWindow"]
            H --> I["Create WPF Window + ViewRangeManagerView"]
            I --> J["Resolve ViewRangeManagerViewModel"]
            J --> K["ViewModel.Initialize(openData, wiring)"]
            K --> L["Register backend callbacks"]
            K --> M["LoadInitialData()"]
            M --> N["Populate sidebar view groups"]
            M --> O["Populate editor level dropdowns"]
            M --> P["Auto-check first view"]
            P --> Q["RequestLoadViewRanges"]
            Q --> R["ExternalEvent: ViewRangeLoadRanges"]
        """;

    [Fact]
    public void Parses_user_example_into_expected_nodes_and_edges()
    {
        Assert.True(MermaidFlowchartParser.TryParse(UserExample, out var chart));
        Assert.Equal(MermaidDirection.TopDown, chart.Direction);

        // A,B,C,D,E,F,F1,F2,G,H,I,J,K,L,M,N,O,P,Q,R = 20 nodes, 19 edges.
        Assert.Equal(
            "A,B,C,D,E,F,F1,F2,G,H,I,J,K,L,M,N,O,P,Q,R",
            string.Join(",", chart.Nodes.Select(n => n.Id)));
        Assert.Equal(19, chart.Edges.Count);

        var a = Assert.Single(chart.Nodes, n => n.Id == "A");
        Assert.Equal("User clicks View Range Manager ribbon / launcher", a.Label);
        Assert.Equal(MermaidNodeShape.Rectangle, a.Shape);

        // Angle brackets inside the label survive (C uses GetService<ViewRangeController>()).
        var c = Assert.Single(chart.Nodes, n => n.Id == "C");
        Assert.Equal("MagicServices.GetService<ViewRangeController>()", c.Label);

        // Fan-out from E and M is preserved as separate edges.
        Assert.Contains(chart.Edges, e => e.SourceId == "E" && e.TargetId == "F");
        Assert.Contains(chart.Edges, e => e.SourceId == "E" && e.TargetId == "G");
        Assert.Contains(chart.Edges, e => e.SourceId == "M" && e.TargetId == "N");
        Assert.Contains(chart.Edges, e => e.SourceId == "M" && e.TargetId == "O");
        Assert.Contains(chart.Edges, e => e.SourceId == "M" && e.TargetId == "P");
        Assert.All(chart.Edges, e => Assert.True(e.HasArrow));
    }

    [Fact]
    public void Parses_shapes_and_edge_labels()
    {
        const string src = """
            flowchart LR
                Start([Start]) --> Cond{Is valid?}
                Cond -->|yes| Save[(Database)]
                Cond -- no --> Err((Error))
                Save -.-> Done[[Finish]]
            """;
        Assert.True(MermaidFlowchartParser.TryParse(src, out var chart));
        Assert.Equal(MermaidDirection.LeftRight, chart.Direction);

        Assert.Equal(MermaidNodeShape.Stadium, Single(chart, "Start").Shape);
        Assert.Equal(MermaidNodeShape.Diamond, Single(chart, "Cond").Shape);
        Assert.Equal(MermaidNodeShape.Cylinder, Single(chart, "Save").Shape);
        Assert.Equal(MermaidNodeShape.Circle, Single(chart, "Err").Shape);
        Assert.Equal(MermaidNodeShape.Subroutine, Single(chart, "Done").Shape);

        // Both label forms: piped (|yes|) and inline (-- no -->).
        Assert.Equal("yes", Edge(chart, "Cond", "Save").Label);
        Assert.Equal("no", Edge(chart, "Cond", "Err").Label);

        // Dotted link style detected.
        Assert.Equal(MermaidLinkStyle.Dotted, Edge(chart, "Save", "Done").Style);
    }

    [Fact]
    public void Expands_chained_edges()
    {
        Assert.True(MermaidFlowchartParser.TryParse("graph TD; A --> B --> C", out var chart));
        Assert.Equal(3, chart.Nodes.Count);
        Assert.Equal(2, chart.Edges.Count);
        Assert.Contains(chart.Edges, e => e.SourceId == "A" && e.TargetId == "B");
        Assert.Contains(chart.Edges, e => e.SourceId == "B" && e.TargetId == "C");
    }

    [Fact]
    public void Ignores_comments_directives_and_subgraph_wrappers()
    {
        const string src = """
            flowchart TD
                %% this is a comment
                subgraph Box
                A --> B
                end
                classDef foo fill:#fff
                A:::foo --> C
            """;
        Assert.True(MermaidFlowchartParser.TryParse(src, out var chart));
        Assert.Equal(3, chart.Nodes.Count); // A, B, C
        Assert.Contains(chart.Edges, e => e.SourceId == "A" && e.TargetId == "B");
        Assert.Contains(chart.Edges, e => e.SourceId == "A" && e.TargetId == "C");
    }

    [Fact]
    public void Rejects_non_flowchart_diagrams()
    {
        Assert.False(MermaidFlowchartParser.TryParse("sequenceDiagram\n  Alice->>Bob: Hi", out _));
        Assert.False(MermaidFlowchartParser.TryParse("", out _));
        Assert.False(MermaidFlowchartParser.TryParse("   ", out _));
    }

    private static MermaidNode Single(MermaidFlowchart chart, string id) =>
        Assert.Single(chart.Nodes, n => n.Id == id);

    private static MermaidEdge Edge(MermaidFlowchart chart, string source, string target) =>
        Assert.Single(chart.Edges, e => e.SourceId == source && e.TargetId == target);
}
