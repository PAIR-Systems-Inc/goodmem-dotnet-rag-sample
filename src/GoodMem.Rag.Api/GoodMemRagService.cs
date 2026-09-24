using Goodmem.Client;
using Goodmem.Client.Api;
using Goodmem.Client.Models;

namespace GoodMem.Rag.Api;

public sealed class GoodMemRagService(
    GoodmemClient client,
    GoodMemSettings settings,
    SampleStateStore stateStore,
    IHostEnvironment environment
)
{
    private const string ChatPostProcessor =
        "com.goodmem.retrieval.postprocess.ChatPostProcessorFactory";

    public async Task<SetupResponse> SetupAsync(CancellationToken cancellationToken)
    {
        var existing = await stateStore.LoadAsync(cancellationToken);
        if (existing is not null)
        {
            if (existing.RerankerId is null)
            {
                var configuredRerankerId = await ResolveRerankerIdAsync(cancellationToken);
                if (configuredRerankerId is not null)
                {
                    existing = existing with { RerankerId = configuredRerankerId };
                    await stateStore.SaveAsync(existing, cancellationToken);
                }
            }

            return ToResponse(existing, created: false);
        }

        var embedderId = settings.EmbedderId;
        if (embedderId is null)
        {
            var embedder = await client.Embedders.CreateAsync(
                new EmbedderCreationRequest
                {
                    DisplayName = "GoodMem .NET sample embedder",
                    ModelIdentifier = "text-embedding-3-large",
                },
                RequireGenerationKey("an embedder"),
                cancellationToken
            );
            embedderId = embedder.EmbedderId;
        }

        var llmId = settings.LlmId;
        if (llmId is null)
        {
            var llm = await client.Llms.CreateAsync(
                new LlmCreationRequest
                {
                    DisplayName = "GoodMem .NET sample LLM",
                    ModelIdentifier = "gpt-5.1",
                },
                RequireGenerationKey("an LLM"),
                cancellationToken
            );
            llmId = llm.Llm.LlmId;
        }

        var rerankerId = await ResolveRerankerIdAsync(cancellationToken);

        var space = await client.Spaces.CreateAsync(
            new SpaceCreationRequest
            {
                Name = $"GoodMem .NET RAG sample {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}",
                SpaceEmbedders =
                [
                    new SpaceEmbedderConfig
                    {
                        EmbedderId = embedderId,
                        DefaultRetrievalWeight = 1,
                    },
                ],
            },
            cancellationToken
        );

        var memoryIds = new List<string>();
        var seedDirectory = Path.Combine(environment.ContentRootPath, "SeedData");
        foreach (var path in Directory.EnumerateFiles(seedDirectory, "*.md").Order())
        {
            var fileName = Path.GetFileName(path);
            var memory = await client.Memories.CreateFromFileAsync(
                space.SpaceId,
                path,
                new JsonMemoryCreationRequest
                {
                    SpaceId = space.SpaceId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["kind"] = "document",
                        ["title"] = Path.GetFileNameWithoutExtension(path),
                        ["source"] = fileName,
                    },
                },
                cancellationToken
            );

            await WaitUntilProcessedAsync(memory.MemoryId, cancellationToken);
            memoryIds.Add(memory.MemoryId);
        }

        var state = new SampleState(
            embedderId,
            llmId,
            rerankerId,
            space.SpaceId,
            memoryIds
        );
        await stateStore.SaveAsync(state, cancellationToken);

        return ToResponse(state, created: true);
    }

    public async Task<ChatResponse> ChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken
    )
    {
        var state = await RequireStateAsync(cancellationToken);
        var response = await RetrieveAsync(
            state,
            request.SessionId,
            request.Message,
            request.UseReranker,
            cancellationToken
        );
        await StoreTurnAsync(
            state,
            request.SessionId,
            request.Message,
            response.Answer,
            cancellationToken
        );
        return response;
    }

    public async Task<CompareResponse> CompareAsync(
        CompareRequest request,
        CancellationToken cancellationToken
    )
    {
        var state = await RequireStateAsync(cancellationToken);
        EnsureReranker(state);

        var baseline = await RetrieveAsync(
            state,
            request.SessionId,
            request.Message,
            useReranker: false,
            cancellationToken
        );
        var reranked = await RetrieveAsync(
            state,
            request.SessionId,
            request.Message,
            useReranker: true,
            cancellationToken
        );

        // Both retrievals see identical history; persist only the selected reranked answer.
        await StoreTurnAsync(
            state,
            request.SessionId,
            request.Message,
            reranked.Answer,
            cancellationToken
        );

        return new CompareResponse(request.SessionId, baseline, reranked);
    }

    private async Task<ChatResponse> RetrieveAsync(
        SampleState state,
        string sessionId,
        string message,
        bool useReranker,
        CancellationToken cancellationToken
    )
    {
        if (useReranker)
        {
            EnsureReranker(state);
        }

        var postProcessorConfig = new Dictionary<string, object>
        {
            ["llm_id"] = state.LlmId,
            ["gen_token_budget"] = 1024,
            ["max_results"] = 8,
            ["chronological_resort"] = false,
        };
        if (useReranker)
        {
            postProcessorConfig["reranker_id"] = state.RerankerId!;
        }

        var retrieveRequest = new RetrieveMemoryRequest
        {
            Message = message,
            SpaceKeys =
            [
                new SpaceKey
                {
                    SpaceId = state.SpaceId,
                    Filter = BuildSessionFilter(sessionId),
                },
            ],
            RequestedSize = 12,
            FetchMemory = true,
            FetchMemoryContent = false,
            PostProcessor = new PostProcessor
            {
                Name = ChatPostProcessor,
                Config = postProcessorConfig,
            },
        };

        var response = new RetrievalResponseBuilder();

        await foreach (
            var evt in client.Memories.RetrieveRawAsync(retrieveRequest, cancellationToken)
        )
        {
            response.Add(evt);
        }

        return response.Build(sessionId, useReranker);
    }

    private async Task StoreTurnAsync(
        SampleState state,
        string sessionId,
        string message,
        string answer,
        CancellationToken cancellationToken
    )
    {
        var turn = await client.Memories.CreateAsync(
            new JsonMemoryCreationRequest
            {
                SpaceId = state.SpaceId,
                OriginalContent = $"User: {message}\nAssistant: {answer}",
                ContentType = "text/plain",
                Metadata = new Dictionary<string, object>
                {
                    ["kind"] = "conversation",
                    ["session_id"] = sessionId,
                    ["title"] = $"Conversation {sessionId}",
                    ["source"] = "chat",
                },
            },
            cancellationToken
        );
        await WaitUntilProcessedAsync(turn.MemoryId, cancellationToken);
    }

    private async Task<SampleState> RequireStateAsync(CancellationToken cancellationToken) =>
        await stateStore.LoadAsync(cancellationToken)
        ?? throw new InvalidOperationException("Call POST /api/setup before chatting.");

    private static void EnsureReranker(SampleState state)
    {
        if (state.RerankerId is null)
        {
            throw new InvalidOperationException(
                "Reranking is unavailable. Set GOODMEM_RERANKER_ID or VOYAGE_API_KEY, "
                    + "then call POST /api/setup again."
            );
        }
    }

    private string RequireGenerationKey(string resource) =>
        settings.OpenAiApiKey
        ?? throw new InvalidOperationException(
            $"OPENAI_API_KEY is required to create {resource}. "
                + "Alternatively, configure it in the GoodMem console and set its resource ID."
        );

    private async Task<string?> ResolveRerankerIdAsync(CancellationToken cancellationToken)
    {
        if (settings.RerankerId is not null)
        {
            return settings.RerankerId;
        }

        var apiKey = settings.VoyageApiKey;
        if (apiKey is null)
        {
            return null;
        }

        var reranker = await client.Rerankers.CreateAsync(
            new RerankerCreationRequest
            {
                DisplayName = "GoodMem .NET sample reranker",
                ModelIdentifier = "rerank-2.5",
            },
            apiKey,
            cancellationToken
        );
        return reranker.RerankerId;
    }

    private async Task WaitUntilProcessedAsync(
        string memoryId,
        CancellationToken cancellationToken
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var memory = await client.Memories.GetAsync(
                memoryId,
                new MemoriesGetOptions(),
                cancellationToken
            );
            switch (memory.ProcessingStatus)
            {
                case "COMPLETED":
                    return;
                case "FAILED":
                    throw new InvalidOperationException($"GoodMem failed to process memory {memoryId}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException($"GoodMem did not finish processing memory {memoryId}.");
    }

    private static string BuildSessionFilter(string sessionId) =>
        $"CAST(val('$.kind') AS TEXT) = 'document' OR "
        + $"CAST(val('$.session_id') AS TEXT) = '{sessionId}'";

    private static SetupResponse ToResponse(SampleState state, bool created) =>
        new(
            created,
            state.SpaceId,
            state.EmbedderId,
            state.LlmId,
            state.RerankerId,
            state.MemoryIds
        );
}
