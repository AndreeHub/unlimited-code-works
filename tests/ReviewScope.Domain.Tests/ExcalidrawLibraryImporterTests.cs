using System.Linq;
using ReviewScope.Canvas;
using ReviewScope.Domain;
using Xunit;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Domain.Tests;

public class ExcalidrawLibraryImporterTests
{
    private const string LibJson = """
    {
      "type": "excalidrawlib",
      "version": 2,
      "libraryItems": [
        {
          "id": "item-1",
          "elements": [
            { "type": "rectangle", "x": 100, "y": 100, "width": 80, "height": 40,
              "strokeColor": "#1e1e1e", "backgroundColor": "#a5d8ff",
              "fillStyle": "solid", "strokeWidth": 2, "strokeStyle": "solid",
              "opacity": 100, "roundness": { "type": 3 } },
            { "type": "ellipse", "x": 100, "y": 200, "width": 60, "height": 60,
              "strokeColor": "#e03131", "backgroundColor": "transparent",
              "fillStyle": "hachure", "strokeWidth": 1, "strokeStyle": "dashed",
              "opacity": 80 },
            { "type": "diamond", "x": 300, "y": 100, "width": 50, "height": 50,
              "strokeColor": "#2f9e44", "backgroundColor": "transparent" },
            { "type": "arrow", "x": 100, "y": 300, "width": 100, "height": 0,
              "strokeColor": "#1971c2", "strokeWidth": 2,
              "points": [[0, 0], [100, 0]] },
            { "type": "freedraw", "x": 0, "y": 0, "width": 10, "height": 10,
              "points": [[0, 0], [5, 5], [10, 10]] }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Import_MapsCleanShapes_AndFreedraw()
    {
        var result = ExcalidrawLibraryImporter.Import(LibJson, new WpfPoint(0, 0));

        // rect, ellipse, diamond, arrow, freedraw all map (Phase 3 added freedraw).
        Assert.Equal(5, result.Blocks.Count);
        Assert.Equal(0, result.SkippedCount);

        Assert.Contains(result.Blocks, b => b.ShapeType == "rectangle");
        Assert.Contains(result.Blocks, b => b.ShapeType == "oval");
        Assert.Contains(result.Blocks, b => b.ShapeType == "diamond");
        Assert.Contains(result.Blocks, b => b.ShapeType == "arrow");
        Assert.Contains(result.Blocks, b => b.ShapeType == "freedraw");
        Assert.All(result.Blocks, b => Assert.Equal(BlockKind.Shape, b.Kind));
    }

    [Fact]
    public void Import_Image_IsStillSkipped()
    {
        const string withImage = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "type": "rectangle", "x": 0, "y": 0, "width": 10, "height": 10 },
          { "type": "image", "x": 0, "y": 0, "width": 10, "height": 10, "fileId": "abc" } ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(withImage, new WpfPoint(0, 0));
        Assert.Single(result.Blocks);
        Assert.Equal(1, result.SkippedTypes.GetValueOrDefault("image"));
    }

    [Fact]
    public void Import_TranslatesCollectionTopLeftToDropPoint()
    {
        var result = ExcalidrawLibraryImporter.Import(LibJson, new WpfPoint(1000, 2000));

        // The collection's min X is 100 and min Y is 100; after translation those become the drop point.
        Assert.Equal(1000, result.Blocks.Min(b => b.X), precision: 3);
        Assert.Equal(2000, result.Blocks.Min(b => b.Y), precision: 3);
    }

    [Fact]
    public void Import_MapsStyleFields()
    {
        var result = ExcalidrawLibraryImporter.Import(LibJson, new WpfPoint(0, 0));

        var rect = result.Blocks.First(b => b.ShapeType == "rectangle");
        Assert.Equal("#a5d8ff", rect.Style!.Fill);
        Assert.Equal("solid", rect.Style.FillStyle);
        Assert.True(rect.Style.CornerRadius > 0); // roundness present
        Assert.Equal(2, rect.Style.StrokeWidth, precision: 3);

        var ellipse = result.Blocks.First(b => b.ShapeType == "oval");
        Assert.Equal("#00FFFFFF", ellipse.Style!.Fill); // transparent
        Assert.True(ellipse.Style.Dashed);
        Assert.Equal(0.8, ellipse.Style.Opacity, precision: 3);
    }

    [Fact]
    public void Import_EncodesArrowVertices()
    {
        var result = ExcalidrawLibraryImporter.Import(LibJson, new WpfPoint(0, 0));
        var arrow = result.Blocks.First(b => b.ShapeType == "arrow");

        Assert.NotNull(arrow.Body);
        // Body uses the "points:x,y;x,y" format the interactive ShapeTool produces.
        Assert.StartsWith("points:", arrow.Body);
        int pairCount = arrow.Body!["points:".Length..].Split(';', System.StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(2, pairCount);
    }

    [Fact]
    public void Import_MultiElementItem_SharesOneGroupKey()
    {
        var result = ExcalidrawLibraryImporter.Import(LibJson, new WpfPoint(0, 0));

        var groupKeys = result.Blocks.Select(b => b.GroupKey).Distinct().ToList();
        Assert.Single(groupKeys);
        Assert.NotNull(groupKeys[0]); // 4 supported elements => one shared logical group
        Assert.All(result.Blocks, b => Assert.Equal(groupKeys[0], b.GroupKey));
    }

    [Fact]
    public void Import_DistinctItems_GetDistinctGroups_AndSingletonsAreUngrouped()
    {
        const string twoItems = """
        {
          "type": "excalidrawlib", "version": 2,
          "libraryItems": [
            { "id": "a", "elements": [
              { "type": "rectangle", "x": 0, "y": 0, "width": 10, "height": 10 },
              { "type": "ellipse", "x": 20, "y": 0, "width": 10, "height": 10 } ] },
            { "id": "b", "elements": [
              { "type": "diamond", "x": 100, "y": 0, "width": 10, "height": 10 } ] }
          ]
        }
        """;
        var result = ExcalidrawLibraryImporter.Import(twoItems, new WpfPoint(0, 0));

        var multi = result.Blocks.Where(b => b.ShapeType is "rectangle" or "oval").ToList();
        var single = result.Blocks.Single(b => b.ShapeType == "diamond");

        Assert.Equal(2, multi.Count);
        Assert.NotNull(multi[0].GroupKey);
        Assert.Equal(multi[0].GroupKey, multi[1].GroupKey);   // item A shares a group
        Assert.Null(single.GroupKey);                          // item B is a singleton, ungrouped
        Assert.NotEqual(multi[0].GroupKey, single.GroupKey);   // different items, different group
    }

    [Fact]
    public void ImportItems_ReturnsPerItemGroups_NormalizedToOrigin()
    {
        const string twoItems = """
        {
          "type": "excalidrawlib", "version": 2,
          "libraryItems": [
            { "id": "a", "name": "Flow", "elements": [
              { "type": "rectangle", "x": 100, "y": 100, "width": 80, "height": 40 },
              { "type": "ellipse", "x": 220, "y": 100, "width": 60, "height": 40 } ] },
            { "id": "b", "elements": [
              { "type": "diamond", "x": 500, "y": 500, "width": 50, "height": 50 } ] }
          ]
        }
        """;
        var items = ExcalidrawLibraryImporter.ImportItems(twoItems);

        Assert.Equal(2, items.Count);
        Assert.Equal("Flow", items[0].Name);          // name carried through
        Assert.Equal("Item 2", items[1].Name);        // fallback name for unnamed item
        // Each item is normalized so its top-left sits at (0,0).
        Assert.Equal(0, items[0].Blocks.Min(b => b.X), precision: 3);
        Assert.Equal(0, items[0].Blocks.Min(b => b.Y), precision: 3);
        Assert.Equal(0, items[1].Blocks.Min(b => b.X), precision: 3);
        Assert.Equal(2, items[0].Blocks.Count);
        Assert.Single(items[1].Blocks);
    }

    [Fact]
    public void Import_EmptyOrGarbage_ReturnsEmpty()
    {
        Assert.Empty(ExcalidrawLibraryImporter.Import("", new WpfPoint(0, 0)).Blocks);
        Assert.Empty(ExcalidrawLibraryImporter.Import("not json", new WpfPoint(0, 0)).Blocks);
        Assert.Empty(ExcalidrawLibraryImporter.Import("{}", new WpfPoint(0, 0)).Blocks);
    }

    [Fact]
    public void Import_BoundText_BecomesContainerLabel()
    {
        const string boundText = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "id": "box1", "type": "rectangle", "x": 0, "y": 0, "width": 120, "height": 60,
            "strokeColor": "#1e1e1e", "backgroundColor": "transparent" },
          { "id": "txt1", "type": "text", "containerId": "box1", "x": 20, "y": 20,
            "width": 80, "height": 20, "text": "Server", "strokeColor": "#e03131",
            "fontSize": 18, "textAlign": "center", "verticalAlign": "middle" }
        ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(boundText, new WpfPoint(0, 0));

        // The text element merges into the rectangle instead of becoming a standalone box.
        var rect = Assert.Single(result.Blocks);
        Assert.Equal("rectangle", rect.ShapeType);
        Assert.Equal("Server", rect.Body);
        Assert.Equal("#e03131", rect.Style!.Text);
        Assert.Equal(18, rect.Style.FontSize, precision: 3);
        Assert.Equal("Center", rect.Style.TextAlign);
        Assert.Equal("Middle", rect.Style.VerticalAlign);
    }

    [Fact]
    public void Import_UnboundText_StaysStandalone()
    {
        const string freeText = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "id": "t", "type": "text", "x": 0, "y": 0, "width": 80, "height": 20, "text": "Hello" }
        ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(freeText, new WpfPoint(0, 0));
        var text = Assert.Single(result.Blocks);
        Assert.Equal("Hello", text.Body);
        Assert.Equal("#00FFFFFF", text.Style!.Stroke); // invisible carrier shape
    }

    [Fact]
    public void Import_DottedStrokeStyle_MapsToDotted()
    {
        const string dotted = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "type": "rectangle", "x": 0, "y": 0, "width": 10, "height": 10, "strokeStyle": "dotted" },
          { "type": "rectangle", "x": 20, "y": 0, "width": 10, "height": 10, "strokeStyle": "dashed" }
        ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(dotted, new WpfPoint(0, 0));

        var styles = result.Blocks.Select(b => b.Style!).OrderBy(s => s.Dashed).ToList();
        Assert.True(styles[0].Dotted);
        Assert.False(styles[0].Dashed);
        Assert.True(styles[1].Dashed);
        Assert.False(styles[1].Dotted);
    }

    [Fact]
    public void Import_CrossHatchFill_IsPreserved()
    {
        const string cross = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "type": "rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
            "backgroundColor": "#ffec99", "fillStyle": "cross-hatch" }
        ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(cross, new WpfPoint(0, 0));
        Assert.Equal("cross-hatch", Assert.Single(result.Blocks).Style!.FillStyle);
    }

    [Fact]
    public void Import_HexColors_ShorthandAndAlphaAreNormalized()
    {
        const string colors = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "type": "rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
            "strokeColor": "#abc", "backgroundColor": "#11223344" }
        ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(colors, new WpfPoint(0, 0));
        var style = Assert.Single(result.Blocks).Style!;
        Assert.Equal("#aabbcc", style.Stroke);   // #rgb shorthand expanded
        Assert.Equal("#44112233", style.Fill);   // RGBA reordered to ARGB
    }

    [Fact]
    public void Import_RotatedClosedShape_CarriesRotationDegrees()
    {
        const string rotated = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "type": "rectangle", "x": 0, "y": 0, "width": 100, "height": 50, "angle": 1.5707963267948966 }
        ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(rotated, new WpfPoint(0, 0));
        var rect = Assert.Single(result.Blocks);
        Assert.Equal(90, rect.Style!.Rotation, precision: 3);
        // The unrotated frame is preserved (Excalidraw semantics).
        Assert.Equal(100, rect.Width, precision: 3);
        Assert.Equal(50, rect.Height, precision: 3);
    }

    [Fact]
    public void Import_RotatedLine_BakesAngleIntoVertices()
    {
        // Horizontal 100px line rotated 90° around its center becomes vertical.
        const string rotated = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "type": "line", "x": 0, "y": 0, "width": 100, "height": 0,
            "angle": 1.5707963267948966, "points": [[0, 0], [100, 0]] }
        ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(rotated, new WpfPoint(0, 0));
        var line = Assert.Single(result.Blocks);
        Assert.Equal(0, line.Style!.Rotation, precision: 3); // no style rotation: baked into points
        Assert.True(line.Height > line.Width); // now vertical
        Assert.Equal(100, line.Height, precision: 1);
    }

    [Fact]
    public void Import_EmbeddedImage_DecodesToAssetDir()
    {
        // 1x1 red PNG.
        const string scene = """
        { "type": "excalidraw", "version": 2,
          "elements": [
            { "id": "img1", "type": "image", "x": 10, "y": 20, "width": 64, "height": 32, "fileId": "file-abc" }
          ],
          "files": {
            "file-abc": { "mimeType": "image/png",
              "dataURL": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==" }
          }
        }
        """;
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rs-img-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var result = ExcalidrawLibraryImporter.Import(scene, new WpfPoint(0, 0), dir);
            var img = Assert.Single(result.Blocks);
            Assert.Equal(BlockKind.Image, img.Kind);
            Assert.Equal("image-raw", img.ShapeType);
            Assert.NotNull(img.Source?.AssetPath);
            Assert.True(System.IO.File.Exists(img.Source!.AssetPath));
            Assert.Equal(64, img.Width, precision: 3);
            Assert.Equal(32, img.Height, precision: 3);
        }
        finally
        {
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_EmbeddedImage_WithoutAssetDir_IsSkipped()
    {
        const string scene = """
        { "type": "excalidraw", "version": 2,
          "elements": [
            { "id": "img1", "type": "image", "x": 0, "y": 0, "width": 10, "height": 10, "fileId": "file-abc" }
          ],
          "files": { "file-abc": { "mimeType": "image/png", "dataURL": "data:image/png;base64,AAAA" } }
        }
        """;
        var result = ExcalidrawLibraryImporter.Import(scene, new WpfPoint(0, 0));
        Assert.Empty(result.Blocks);
        Assert.Equal(1, result.SkippedTypes.GetValueOrDefault("image"));
    }

    [Fact]
    public void Import_RoundedPolyline_MarksInteriorVerticesCurved()
    {
        const string curvy = """
        { "type": "excalidrawlib", "version": 2, "libraryItems": [ { "id": "a", "elements": [
          { "type": "line", "x": 0, "y": 0, "width": 100, "height": 100,
            "roundness": { "type": 2 },
            "points": [[0, 0], [50, 0], [100, 50], [100, 100]] }
        ] } ] }
        """;
        var result = ExcalidrawLibraryImporter.Import(curvy, new WpfPoint(0, 0));
        var line = Assert.Single(result.Blocks);

        // Interior vertices carry the ",c" curved marker; endpoints stay sharp.
        var pairs = line.Body!["points:".Length..].Split(';');
        Assert.Equal(4, pairs.Length);
        Assert.DoesNotContain(",c", pairs[0]);
        Assert.EndsWith(",c", pairs[1]);
        Assert.EndsWith(",c", pairs[2]);
        Assert.DoesNotContain(",c", pairs[3]);
    }
}
