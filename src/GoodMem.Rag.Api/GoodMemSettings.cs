namespace GoodMem.Rag.Api;

public sealed record GoodMemSettings(
    string BaseUrl,
    string ApiKey,
    bool VerifySsl,
    string? EmbedderId,
    string? LlmId,
    string? RerankerId,
    string? OpenAiApiKey,
    string? VoyageApiKey
)
{
    public static GoodMemSettings FromEnvironment()
    {
        var baseUrl = Required("GOODMEM_BASE_URL").TrimEnd('/');
        if (
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new InvalidOperationException(
                "Environment variable GOODMEM_BASE_URL must be an absolute HTTP(S) URL."
            );
        }

        var verifySsl = Boolean("GOODMEM_VERIFY_SSL", defaultValue: true);
        if (!verifySsl && !uri.IsLoopback)
        {
            throw new InvalidOperationException(
                "GOODMEM_VERIFY_SSL=false is allowed only for a loopback URL. "
                    + "Trust the server certificate for remote deployments."
            );
        }

        return new GoodMemSettings(
            BaseUrl: baseUrl,
            ApiKey: Required("GOODMEM_API_KEY"),
            VerifySsl: verifySsl,
            EmbedderId: Optional("GOODMEM_EMBEDDER_ID"),
            LlmId: Optional("GOODMEM_LLM_ID"),
            RerankerId: Optional("GOODMEM_RERANKER_ID"),
            OpenAiApiKey: Optional("OPENAI_API_KEY"),
            VoyageApiKey: Optional("VOYAGE_API_KEY")
        );
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is required.");

    private static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value
        && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool Boolean(string name, bool defaultValue)
    {
        var value = Optional(name);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Environment variable {name} must be 'true' or 'false'."
            );
    }
}
