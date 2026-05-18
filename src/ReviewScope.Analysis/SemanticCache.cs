using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;


namespace ReviewScope.Analysis;

// --- Semantic cache ---

public sealed class SemanticCache
{
    private readonly Dictionary<string, IReadOnlyList<SemanticTokenSpan>> _spanCache = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetSpans(string key, out IReadOnlyList<SemanticTokenSpan> result) =>
        _spanCache.TryGetValue(key, out result!);
    public void StoreSpans(string key, IReadOnlyList<SemanticTokenSpan> result) => _spanCache[key] = result;

    public void Clear() => _spanCache.Clear();
}
