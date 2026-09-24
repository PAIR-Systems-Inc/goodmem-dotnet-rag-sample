using System.Text.Json;
using Goodmem.Client.Models;

namespace GoodMem.Rag.Api;

internal sealed class RetrievalResponseBuilder
{
    private readonly Dictionary<string, Memory> _memories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stages = new(StringComparer.Ordinal);
    private readonly List<ChunkReference> _chunks = [];
    private AbstractReply? _reply;

    public void Add(RetrieveMemoryEvent evt)
    {
        if (evt.Status is { Code: "RERANKING_FAILED" or "SUMMARIZATION_FAILED" } status)
        {
            // Provider error messages can contain upstream details; expose only the status code.
            throw new InvalidOperationException(
                $"GoodMem retrieval failed ({status.Code}). Check the server's provider configuration."
            );
        }

        if (evt.ResultSetBoundary is { Kind: "BEGIN" } boundary)
        {
            _stages[boundary.ResultSetId] = boundary.StageName;
        }

        if (evt.AbstractReply is { Text.Length: > 0 } reply)
        {
            _reply = reply;
        }

        if (evt.MemoryDefinition is { } definition)
        {
            _memories[definition.MemoryId] = definition;
        }

        if (evt.RetrievedItem?.Chunk is { } chunk)
        {
            _chunks.Add(chunk);
        }

        if (evt.RetrievedItem?.Memory is { } memory)
        {
            _memories[memory.MemoryId] = memory;
        }
    }

    public ChatResponse Build(string sessionId, bool useReranker)
    {
        if (_reply is null || string.IsNullOrWhiteSpace(_reply.Text))
        {
            throw new InvalidOperationException("GoodMem returned no generated answer.");
        }

        if (_reply.ResultSetId is not { } resultSetId)
        {
            throw new InvalidOperationException("GoodMem returned an answer without a result set.");
        }

        var reranked = _stages.TryGetValue(resultSetId, out var stage) && stage == "rerank";
        if (useReranker && !reranked)
        {
            throw new InvalidOperationException("GoodMem did not return the requested reranked result set.");
        }

        // Scores have different meanings across retrieval stages. Keep the server's order,
        // and cite only the result set used to generate this answer.
        var citations = _chunks
            .Where(chunk => chunk.ResultSetId == resultSetId)
            .GroupBy(chunk => chunk.Chunk.MemoryId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(chunk =>
            {
                _memories.TryGetValue(chunk.Chunk.MemoryId, out var memory);
                return new Citation(
                    chunk.Chunk.MemoryId,
                    MetadataText(memory, "title") ?? "Untitled memory",
                    MetadataText(memory, "source") ?? "GoodMem",
                    chunk.Chunk.ChunkText,
                    chunk.RelevanceScore
                );
            })
            .ToArray();

        return new ChatResponse(sessionId, _reply.Text, reranked, citations);
    }

    private static string? MetadataText(Memory? memory, string key)
    {
        if (memory?.Metadata is null || !memory.Metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => value?.ToString(),
        };
    }
}
