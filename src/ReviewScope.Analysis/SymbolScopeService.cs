using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;


namespace ReviewScope.Analysis;

// --- Symbol scope service (for function extraction) ---

public sealed class SymbolScopeService : ISymbolScopeService
{
    private readonly WorkspaceManager _workspaceManager;

    public SymbolScopeService(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

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

    public async Task<(string FilePath, int StartLine, int EndLine, string SymbolName, string ContainingType)?> GetFunctionDefinitionScopeAsync(
        string filePath, int line, int column, CancellationToken cancellationToken)
    {
        var session = _workspaceManager.CurrentSession;
        if (session?.Solution is null || !session.TryGetDocumentId(filePath, out var documentId))
            return null;

        var solution = session.Solution;
        var document = solution.GetDocument(documentId);
        if (document is null)
            return null;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root is null || semanticModel is null)
            return null;

        int offset = GetOffset(text.ToString(), line, column);
        SyntaxToken token = root.FindToken(offset);
        SyntaxNode? symbolNode = GetSymbolNode(token);
        if (symbolNode is null)
            return null;

        ISymbol? symbol = semanticModel.GetSymbolInfo(symbolNode, cancellationToken).Symbol
            ?? semanticModel.GetDeclaredSymbol(symbolNode, cancellationToken);
        symbol = NormalizeFunctionSymbol(symbol);
        if (symbol is not IMethodSymbol)
            return null;

        var sourceDefinition = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, cancellationToken)
            ?? symbol;

        foreach (var syntaxReference in sourceDefinition.DeclaringSyntaxReferences)
        {
            SyntaxNode declaration = await syntaxReference.GetSyntaxAsync(cancellationToken);
            SyntaxNode? scope = GetFunctionScopeNode(declaration);
            if (scope is null)
                continue;

            SyntaxTree tree = scope.SyntaxTree;
            string? definitionPath = tree.FilePath;
            if (string.IsNullOrWhiteSpace(definitionPath) || !File.Exists(definitionPath))
                continue;

            var lineSpan = tree.GetLineSpan(scope.Span, cancellationToken).Span;
            string symbolName = GetScopeSymbolName(scope);
            string containingType = GetContainingTypeName(scope);
            return (definitionPath, lineSpan.Start.Line + 1, lineSpan.End.Line + 1, symbolName, containingType);
        }

        return null;
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

    private static int GetOffset(string text, int line, int column)
    {
        int targetLine = Math.Max(0, line - 1);
        var lineSpans = text.Split('\n');
        int offset = 0;
        for (int i = 0; i < targetLine && i < lineSpans.Length; i++)
            offset += lineSpans[i].Length + 1;
        offset += Math.Max(0, column - 1);
        return Math.Clamp(offset, 0, Math.Max(0, text.Length - 1));
    }

    private static SyntaxNode? GetSymbolNode(SyntaxToken token)
    {
        SyntaxNode? node = token.Parent;
        if (node is GenericNameSyntax genericName)
            return genericName;
        if (node is IdentifierNameSyntax identifierName)
            return identifierName;
        if (node is MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax)
            return node;
        return node;
    }

    private static ISymbol? NormalizeFunctionSymbol(ISymbol? symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: not null } reduced)
            return reduced.ReducedFrom;
        if (symbol is IMethodSymbol method)
            return method;
        return null;
    }

    private static SyntaxNode? GetFunctionScopeNode(SyntaxNode declaration)
    {
        SyntaxNode? scope = declaration;
        while (scope is not null)
        {
            if (scope is MethodDeclarationSyntax or ConstructorDeclarationSyntax
                or LocalFunctionStatementSyntax or AccessorDeclarationSyntax)
            {
                return scope;
            }
            scope = scope.Parent;
        }

        return null;
    }

    private static string GetScopeSymbolName(SyntaxNode scope) => scope switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        LocalFunctionStatementSyntax lf => lf.Identifier.Text,
        AccessorDeclarationSyntax a => a.Keyword.Text,
        _ => "?"
    };
}
