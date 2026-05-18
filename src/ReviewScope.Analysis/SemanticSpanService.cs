using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;


namespace ReviewScope.Analysis;

// --- Semantic token (syntax highlighting) service ---

public sealed class SemanticSpanService : ISemanticSpanService
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly SemanticCache _cache;

    public SemanticSpanService(WorkspaceManager workspaceManager, SemanticCache cache)
    {
        _workspaceManager = workspaceManager;
        _cache = cache;
    }

    public async Task<IReadOnlyList<SemanticTokenSpan>> GetTokenSpansAsync(string filePath, int startLine, int endLine, CancellationToken cancellationToken)
    {
        string cacheKey = $"{filePath}:{startLine}:{endLine}";
        if (_cache.TryGetSpans(cacheKey, out var cached)) return cached;

        if (!File.Exists(filePath)) return Array.Empty<SemanticTokenSpan>();

        string text = await File.ReadAllTextAsync(filePath, cancellationToken);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, path: filePath, cancellationToken: cancellationToken);
        SyntaxNode root = await tree.GetRootAsync(cancellationToken);

        var tokens = root.DescendantTokens()
            .Where(t =>
            {
                var pos = tree.GetLineSpan(t.Span, cancellationToken).Span;
                int line = pos.Start.Line + 1;
                return line >= startLine && line <= endLine && !t.IsKind(SyntaxKind.None);
            })
            .Select(t => BuildSpan(t, tree, cancellationToken))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        tokens.AddRange(root.DescendantTrivia(descendIntoTrivia: true)
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                     || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                     || t.IsKind(SyntaxKind.DocumentationCommentExteriorTrivia)
                     || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                     || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(t => BuildTriviaSpan(t, tree, cancellationToken))
            .Where(s => s is not null)
            .Select(s => s!)
            .Where(s => s.Line >= startLine && s.Line <= endLine));

        tokens = tokens
            .OrderBy(t => t.Line)
            .ThenBy(t => t.Column)
            .ToList();

        _cache.StoreSpans(cacheKey, tokens);
        return tokens;
    }

    private static SemanticTokenSpan? BuildTriviaSpan(SyntaxTrivia trivia, SyntaxTree tree, CancellationToken ct)
    {
        if (trivia.Span.Length == 0) return null;
        var lineSpan = tree.GetLineSpan(trivia.Span, ct).Span;
        int line = lineSpan.Start.Line + 1;
        int col = lineSpan.Start.Character + 1;
        return new SemanticTokenSpan(line, col, trivia.Span.Length, SemanticTokenKind.Comment, false, trivia.ToString());
    }

    private static SemanticTokenSpan? BuildSpan(SyntaxToken token, SyntaxTree tree, CancellationToken ct)
    {
        if (token.Span.Length == 0) return null;
        var lineSpan = tree.GetLineSpan(token.Span, ct).Span;
        int line = lineSpan.Start.Line + 1;
        int col = lineSpan.Start.Character + 1;
        int len = token.Span.Length;
        string text = token.Text;
        SemanticTokenKind kind = ClassifyToken(token);
        bool isSymbolCandidate = token.IsKind(SyntaxKind.IdentifierToken);
        return new SemanticTokenSpan(line, col, len, kind, isSymbolCandidate, text);
    }

    private static SemanticTokenKind ClassifyToken(SyntaxToken token)
    {
        if (token.IsKeyword() || token.IsContextualKeyword()) return SemanticTokenKind.Keyword;
        if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringStartToken))
            return SemanticTokenKind.String;
        if (token.IsKind(SyntaxKind.NumericLiteralToken)) return SemanticTokenKind.Number;
        if (token.IsKind(SyntaxKind.SingleLineCommentTrivia) || token.IsKind(SyntaxKind.MultiLineCommentTrivia))
            return SemanticTokenKind.Comment;

        if (token.IsKind(SyntaxKind.IdentifierToken))
        {
            if (token.Parent is BaseTypeDeclarationSyntax or TypeParameterSyntax) return SemanticTokenKind.Type;
            if (token.Parent is MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax)
                return SemanticTokenKind.Function;
            if (token.Parent is PropertyDeclarationSyntax) return SemanticTokenKind.Property;
            if (token.Parent is VariableDeclaratorSyntax vd && vd.Parent?.Parent is FieldDeclarationSyntax)
                return SemanticTokenKind.Field;
            return SemanticTokenKind.Plain;
        }

        return SemanticTokenKind.Plain;
    }
}
