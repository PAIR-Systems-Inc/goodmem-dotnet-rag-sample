using System.Text.Json;
using Goodmem.Client.Models;
using Xunit;

namespace GoodMem.Rag.Api.Tests;

public sealed class RetrievalResponseBuilderTests
{
    [Fact]
    public void BaselinePreservesServerOrderAndFirstChunkPerMemory()
    {
        var builder = new RetrievalResponseBuilder();
        builder.Add(Chunk("original", "billing", -0.9));
        builder.Add(Chunk("original", "support", -0.2));
        builder.Add(Chunk("original", "billing", -0.1));
        builder.Add(Reply("original"));

        var response = builder.Build("session", useReranker: false);

        Assert.Equal(new[] { "billing", "support" }, response.Citations.Select(c => c.MemoryId));
        Assert.Equal(-0.9, response.Citations[0].Relevance);
        Assert.False(response.Reranked);
    }

    [Fact]
    public void RerankedCitationsUseOnlyTheAnswersResultSet()
    {
        var builder = new RetrievalResponseBuilder();
        builder.Add(Chunk("original", "excluded", 0.99));
        builder.Add(Chunk("original", "billing", 0.98));
        builder.Add(Boundary("reranked", "rerank"));
        builder.Add(Chunk("reranked", "billing", 0.8));
        builder.Add(Chunk("reranked", "support", 0.1));
        builder.Add(new RetrieveMemoryEvent
        {
            ResultSetBoundary = new ResultSetBoundary
            {
                ResultSetId = "reranked",
                Kind = "END",
                StageName = "",
            },
        });
        builder.Add(Reply("reranked"));

        var response = builder.Build("session", useReranker: true);

        Assert.Equal(new[] { "billing", "support" }, response.Citations.Select(c => c.MemoryId));
        Assert.Equal(0.8, response.Citations[0].Relevance);
        Assert.True(response.Reranked);
    }

    [Theory]
    [InlineData("RERANKING_FAILED")]
    [InlineData("SUMMARIZATION_FAILED")]
    public void ProviderFailureCannotBecomeASuccessfulChat(string code)
    {
        var builder = new RetrievalResponseBuilder();
        var error = Assert.Throws<InvalidOperationException>(() => builder.Add(
            new RetrieveMemoryEvent
            {
                Status = new GoodMemStatus { Code = code, Message = "sensitive upstream detail" },
            }
        ));

        Assert.Contains(code, error.Message);
        Assert.DoesNotContain("sensitive upstream detail", error.Message);
    }

    [Fact]
    public void MissingAnswerCannotBeStoredAsAChatTurn()
    {
        var builder = new RetrievalResponseBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.Build("session", false));
    }

    [Fact]
    public void RerankFallbackCannotBeLabelledReranked()
    {
        var builder = new RetrievalResponseBuilder();
        builder.Add(Boundary("original", "retrieve"));
        builder.Add(Reply("original"));
        Assert.Throws<InvalidOperationException>(() => builder.Build("session", true));
    }

    private static RetrieveMemoryEvent Reply(string resultSetId) => new()
    {
        AbstractReply = new AbstractReply
        {
            Text = "An answer.",
            ResultSetId = resultSetId,
            RelevanceScore = 1,
        },
    };

    private static RetrieveMemoryEvent Boundary(string resultSetId, string stage) => new()
    {
        ResultSetBoundary = new ResultSetBoundary
        {
            ResultSetId = resultSetId,
            Kind = "BEGIN",
            StageName = stage,
        },
    };

    private static RetrieveMemoryEvent Chunk(string resultSetId, string memoryId, double score) =>
        JsonSerializer.Deserialize<RetrieveMemoryEvent>(JsonSerializer.Serialize(new
        {
            retrievedItem = new
            {
                chunk = new
                {
                    resultSetId,
                    memoryIndex = 0,
                    relevanceScore = score,
                    chunk = new
                    {
                        chunkId = memoryId + "-chunk",
                        memoryId,
                        chunkSequenceNumber = 0,
                        chunkText = "Excerpt " + score,
                        vectorStatus = "COMPLETED",
                        createdAt = 0,
                        updatedAt = 0,
                        createdById = "test-user",
                        updatedById = "test-user",
                    },
                },
            },
        }))!;
}
