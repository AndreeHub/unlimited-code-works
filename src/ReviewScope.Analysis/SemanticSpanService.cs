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
        if (token.IsKeyword() || token.IsContextualKeyword() || IsContextualKeywordText(token.Text))
            return SemanticTokenKind.Keyword;
        if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringStartToken))
            return SemanticTokenKind.String;
        if (token.IsKind(SyntaxKind.NumericLiteralToken)) return SemanticTokenKind.Number;
        if (token.IsKind(SyntaxKind.SingleLineCommentTrivia) || token.IsKind(SyntaxKind.MultiLineCommentTrivia))
            return SemanticTokenKind.Comment;

        if (token.IsKind(SyntaxKind.IdentifierToken))
        {
            if (IsFunctionIdentifier(token))
                return SemanticTokenKind.Function;

            if (IsTypeIdentifier(token))
                return SemanticTokenKind.Type;

            if (IsPropertyIdentifier(token))
                return SemanticTokenKind.Property;

            if (IsFieldIdentifier(token))
                return SemanticTokenKind.Field;

            return SemanticTokenKind.Plain;
        }

        return SemanticTokenKind.Plain;
    }

    private static bool IsFunctionIdentifier(SyntaxToken token)
    {
        if (token.Parent is MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax)
            return true;

        if (token.Parent is IdentifierNameSyntax identifier)
        {
            if (identifier.Parent is InvocationExpressionSyntax invocation && invocation.Expression == identifier)
                return true;

            if (identifier.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name == identifier
                && memberAccess.Parent is InvocationExpressionSyntax)
                return true;
        }

        if (token.Parent is GenericNameSyntax generic)
        {
            if (generic.Parent is InvocationExpressionSyntax invocation && invocation.Expression == generic)
                return true;

            if (generic.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name == generic
                && memberAccess.Parent is InvocationExpressionSyntax)
                return true;
        }

        return false;
    }

    private static bool IsTypeIdentifier(SyntaxToken token)
    {
        if (token.Parent is BaseTypeDeclarationSyntax or TypeParameterSyntax)
            return true;

        if (token.Parent is IdentifierNameSyntax identifier)
        {
            if (identifier.Parent is ObjectCreationExpressionSyntax objectCreation && objectCreation.Type == identifier)
                return true;
            if (identifier.Parent is VariableDeclarationSyntax variableDeclaration && variableDeclaration.Type == identifier)
                return true;
            if (identifier.Parent is ParameterSyntax parameter && parameter.Type == identifier)
                return true;
            if (identifier.Parent is PropertyDeclarationSyntax property && property.Type == identifier)
                return true;
            if (identifier.Parent is CastExpressionSyntax cast && cast.Type == identifier)
                return true;
            if (identifier.Parent is NullableTypeSyntax nullable && nullable.ElementType == identifier)
                return true;
            if (identifier.Parent is QualifiedNameSyntax qualified && qualified.Right == identifier)
                return true;

            return IsPascalCase(token.Text)
                && identifier.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Expression == identifier;
        }

        if (token.Parent is GenericNameSyntax generic)
        {
            if (generic.Parent is ObjectCreationExpressionSyntax objectCreation && objectCreation.Type == generic)
                return true;
            if (generic.Parent is VariableDeclarationSyntax variableDeclaration && variableDeclaration.Type == generic)
                return true;
            if (generic.Parent is ParameterSyntax parameter && parameter.Type == generic)
                return true;
            if (generic.Parent is PropertyDeclarationSyntax property && property.Type == generic)
                return true;
            if (generic.Parent is CastExpressionSyntax cast && cast.Type == generic)
                return true;
            if (generic.Parent is NullableTypeSyntax nullable && nullable.ElementType == generic)
                return true;

            return IsPascalCase(token.Text);
        }

        return false;
    }

    private static bool IsPropertyIdentifier(SyntaxToken token)
    {
        if (token.Parent is PropertyDeclarationSyntax)
            return true;

        return token.Parent is IdentifierNameSyntax identifier
            && identifier.Parent is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name == identifier
            && memberAccess.Parent is not InvocationExpressionSyntax;
    }

    private static bool IsFieldIdentifier(SyntaxToken token) =>
        token.Parent is VariableDeclaratorSyntax variable
        && variable.Parent?.Parent is FieldDeclarationSyntax;

    private static bool IsPascalCase(string text) =>
        !string.IsNullOrEmpty(text) && char.IsUpper(text[0]);

    private static bool IsContextualKeywordText(string text) => text is
        "var" or "async" or "await" or "yield" or "nameof" or "record" or "init" or "required" or
        "partial" or "where" or "from" or "select" or "group" or "into" or "orderby" or "join" or
        "let" or "on" or "equals" or "by" or "ascending" or "descending" or "global" or "unmanaged";
}
