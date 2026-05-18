using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;


namespace ReviewScope.Analysis;

// --- Symbol scope service (for function extraction) ---

public sealed class SymbolScopeService : ISymbolScopeService
{
    public async Task<(int StartLine, int EndLine, string SymbolName, string ContainingType)?> GetSymbolScopeAsync(
        string filePath, int line, int column, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return null;

        string text = await File.ReadAllTextAsync(filePath, cancellationToken);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await tree.GetRootAsync(cancellationToken);

        // Convert 1-based line/col to text offset
        int targetLine = Math.Max(0, line - 1);
        var lineSpans = text.Split('\n');
        int offset = 0;
        for (int i = 0; i < targetLine && i < lineSpans.Length; i++)
            offset += lineSpans[i].Length + 1;
        offset += Math.Max(0, column - 1);
        offset = Math.Min(offset, text.Length - 1);

        SyntaxToken token = root.FindToken(offset);

        // Walk up to find the method/constructor/property scope
        SyntaxNode? scope = token.Parent;
        while (scope is not null)
        {
            if (scope is MethodDeclarationSyntax or ConstructorDeclarationSyntax
                or PropertyDeclarationSyntax or LocalFunctionStatementSyntax
                or AccessorDeclarationSyntax)
            {
                break;
            }
            scope = scope.Parent;
        }

        if (scope is null) return null;

        var lineSpan = tree.GetLineSpan(scope.Span, cancellationToken).Span;
        int startLine = lineSpan.Start.Line + 1;
        int endLine = lineSpan.End.Line + 1;

        string symbolName = scope switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            LocalFunctionStatementSyntax lf => lf.Identifier.Text,
            _ => "?"
        };

        string containingType = GetContainingTypeName(scope);
        return (startLine, endLine, symbolName, containingType);
    }

    private static string GetContainingTypeName(SyntaxNode node)
    {
        SyntaxNode? parent = node.Parent;
        while (parent is not null)
        {
            if (parent is BaseTypeDeclarationSyntax typeDecl)
                return typeDecl.Identifier.Text;
            parent = parent.Parent;
        }
        return string.Empty;
    }
}
