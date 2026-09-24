using System.Text.RegularExpressions;

namespace GoodMem.Rag.Api;

public sealed record ChatRequest(string SessionId, string Message, bool UseReranker = false)
{
    private static readonly Regex SessionIdPattern = new(
        "^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
    );

    public static bool IsValidSessionId(string? value) =>
        value is not null && SessionIdPattern.IsMatch(value);
}

public sealed record CompareRequest(string SessionId, string Message);

public sealed record Citation(
    string MemoryId,
    string Title,
    string Source,
    string? Excerpt,
    double? Relevance
);

public sealed record ChatResponse(
    string SessionId,
    string Answer,
    bool Reranked,
    IReadOnlyList<Citation> Citations
);

public sealed record CompareResponse(
    string SessionId,
    ChatResponse Baseline,
    ChatResponse Reranked
);

public sealed record SetupResponse(
    bool Created,
    string SpaceId,
    string EmbedderId,
    string LlmId,
    string? RerankerId,
    IReadOnlyList<string> MemoryIds
);
