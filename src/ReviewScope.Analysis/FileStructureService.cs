using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;


namespace ReviewScope.Analysis;

// --- File structure service ---

public sealed class FileStructureService : IFileStructureService
{
    public async Task<FileStructureInfo?> GetFileStructureAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return null;

        string text = await File.ReadAllTextAsync(filePath, cancellationToken);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await tree.GetRootAsync(cancellationToken);

        var types = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Parent is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .Select(t => BuildTypeInfo(t, tree, cancellationToken))
            .OrderBy(t => t.StartLine)
            .ToList();

        return new FileStructureInfo(filePath, types);
    }

    private static TypeStructureInfo BuildTypeInfo(BaseTypeDeclarationSyntax typeDecl, SyntaxTree tree, CancellationToken ct)
    {
        var span = tree.GetLineSpan(typeDecl.Span, ct).Span;
        var members = typeDecl is TypeDeclarationSyntax td ? td.Members : new SyntaxList<MemberDeclarationSyntax>();
        var methods = members
            .OfType<BaseMethodDeclarationSyntax>()
            .Select(m => BuildMethodInfo(m, tree, ct))
            .OrderBy(m => m.StartLine)
            .ToList();

        return new TypeStructureInfo(
            GetTypeName(typeDecl), GetTypeKind(typeDecl),
            span.Start.Line + 1, span.Start.Character + 1,
            span.End.Line + 1, span.End.Character + 1,
            methods);
    }

    private static MethodStructureInfo BuildMethodInfo(BaseMethodDeclarationSyntax methodDecl, SyntaxTree tree, CancellationToken ct)
    {
        var span = tree.GetLineSpan(methodDecl.Span, ct).Span;
        return methodDecl switch
        {
            MethodDeclarationSyntax m => new MethodStructureInfo(
                m.Identifier.Text, "method",
                BuildSig(m.Identifier.Text, m.ParameterList.Parameters),
                span.Start.Line + 1, span.Start.Character + 1,
                span.End.Line + 1, span.End.Character + 1),
            ConstructorDeclarationSyntax c => new MethodStructureInfo(
                c.Identifier.Text, "constructor",
                BuildSig(c.Identifier.Text, c.ParameterList.Parameters),
                span.Start.Line + 1, span.Start.Character + 1,
                span.End.Line + 1, span.End.Character + 1),
            _ => new MethodStructureInfo("?", "method", "?()",
                span.Start.Line + 1, span.Start.Character + 1,
                span.End.Line + 1, span.End.Character + 1)
        };
    }

    private static string BuildSig(string name, SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        string paramText = string.Join(", ", parameters.Select(p =>
            $"{p.Type?.ToString() ?? "?"} {p.Identifier.Text}".Trim()));
        return $"{name}({paramText})";
    }

    private static string GetTypeName(BaseTypeDeclarationSyntax t) => t.Identifier.Text;
    private static string GetTypeKind(BaseTypeDeclarationSyntax t) => t switch
    {
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        RecordDeclarationSyntax => "record",
        _ => "type"
    };
}
