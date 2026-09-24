using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Goodmem.Client;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GoodMem.Rag.Api.Tests;

public sealed class ProviderSetupTests
{
    private const string EmbedderId = "11111111-1111-4111-8111-111111111111";
    private const string LlmId = "22222222-2222-4222-8222-222222222222";
    private const string RerankerId = "33333333-3333-4333-8333-333333333333";
    private const string SpaceId = "44444444-4444-4444-8444-444444444444";

    [Fact]
    public async Task SetupReusesSavedProvidersAndDoesNotPersistCredentials()
    {
        using var fixture = new Fixture();
        var service = fixture.Service(Settings() with
        {
            OpenAiApiKey = "openai-test-secret",
            VoyageApiKey = "voyage-test-secret",
        });

        var setup = await service.SetupAsync(CancellationToken.None);

        Assert.Equal(new[] { "/v1/embedders", "/v1/llms", "/v1/rerankers", "/v1/spaces" },
            fixture.Handler.Requests.Select(r => r.Path));
        Assert.Equal(EmbedderId, fixture.Request("/v1/spaces")["spaceEmbedders"]![0]!["embedderId"]!.GetValue<string>());
        Assert.Equal(RerankerId, setup.RerankerId);

        var requestCount = fixture.Handler.Requests.Count;
        var again = await service.SetupAsync(CancellationToken.None);
        Assert.False(again.Created);
        Assert.Equal(setup.SpaceId, again.SpaceId);
        Assert.Equal(requestCount, fixture.Handler.Requests.Count);
        var saved = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "App_Data", "goodmem-state.json"));
        Assert.DoesNotContain("secret", saved);
    }

    [Fact]
    public async Task ExplicitProviderIdsOverrideProviderKeys()
    {
        using var fixture = new Fixture();
        var setup = await fixture.Service(Settings() with
        {
            EmbedderId = EmbedderId,
            LlmId = LlmId,
            RerankerId = RerankerId,
            OpenAiApiKey = "openai-test-secret",
            VoyageApiKey = "voyage-test-secret",
        }).SetupAsync(CancellationToken.None);

        Assert.Equal("/v1/spaces", Assert.Single(fixture.Handler.Requests).Path);
        Assert.Equal(EmbedderId, setup.EmbedderId);
        Assert.Equal(LlmId, setup.LlmId);
        Assert.Equal(RerankerId, setup.RerankerId);
    }

    [Fact]
    public async Task VoyageFillsOnlyMissingRerankerId()
    {
        using var fixture = new Fixture();
        await fixture.Service(Settings() with
        {
            EmbedderId = EmbedderId,
            LlmId = LlmId,
            VoyageApiKey = "voyage-test-secret",
        }).SetupAsync(CancellationToken.None);

        Assert.Equal(new[] { "/v1/rerankers", "/v1/spaces" }, fixture.Handler.Requests.Select(r => r.Path));
        Assert.Equal("rerank-2.5", fixture.Request("/v1/rerankers")["modelIdentifier"]!.GetValue<string>());
        Assert.Equal("voyage-test-secret", Secret(fixture.Request("/v1/rerankers")));
    }

    [Fact]
    public async Task ExistingStateCanAttachAVoyageRerankerWithoutRecreatingResources()
    {
        using var fixture = new Fixture();
        await fixture.State.SaveAsync(new SampleState(EmbedderId, LlmId, null, SpaceId, []), CancellationToken.None);
        var service = fixture.Service(Settings() with { VoyageApiKey = "voyage-test-secret" });

        var setup = await service.SetupAsync(CancellationToken.None);
        var again = await service.SetupAsync(CancellationToken.None);

        Assert.False(setup.Created);
        Assert.Equal(SpaceId, setup.SpaceId);
        Assert.Equal(RerankerId, setup.RerankerId);
        Assert.Equal(RerankerId, again.RerankerId);
        Assert.Equal("/v1/rerankers", Assert.Single(fixture.Handler.Requests).Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAiCreatesProvidersWithOptionalVoyage(bool withVoyage)
    {
        using var fixture = new Fixture();
        var setup = await fixture.Service(Settings() with
        {
            OpenAiApiKey = "openai-test-secret",
            VoyageApiKey = withVoyage ? "voyage-test-secret" : null,
        }).SetupAsync(CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1", fixture.Request("/v1/embedders")["endpointUrl"]!.GetValue<string>());
        Assert.Equal("text-embedding-3-large", fixture.Request("/v1/embedders")["modelIdentifier"]!.GetValue<string>());
        Assert.Equal("https://api.openai.com/v1", fixture.Request("/v1/llms")["endpointUrl"]!.GetValue<string>());
        Assert.Equal("gpt-5.1", fixture.Request("/v1/llms")["modelIdentifier"]!.GetValue<string>());
        Assert.Equal("openai-test-secret", Secret(fixture.Request("/v1/embedders")));
        Assert.Equal("openai-test-secret", Secret(fixture.Request("/v1/llms")));
        if (withVoyage)
        {
            Assert.Equal("VOYAGE", fixture.Request("/v1/rerankers")["providerType"]!.GetValue<string>());
            Assert.Equal("voyage-test-secret", Secret(fixture.Request("/v1/rerankers")));
            Assert.Equal(RerankerId, setup.RerankerId);
        }
        else
        {
            Assert.Null(setup.RerankerId);
            Assert.DoesNotContain(fixture.Handler.Requests, r => r.Path == "/v1/rerankers");
        }
    }

    [Fact]
    public async Task MissingProviderCredentialsFailBeforeAnyRequests()
    {
        using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service(Settings()).SetupAsync(CancellationToken.None));
        Assert.Contains("OPENAI_API_KEY", error.Message);
        Assert.Empty(fixture.Handler.Requests);
    }

    private static GoodMemSettings Settings() => new("https://goodmem.test", "gm-test-key", true,
        null, null, null, null, null);

    private static string Secret(JsonObject body) => body["credentials"]!["apiKey"]!["inlineSecret"]!.GetValue<string>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RerankingRequestsFailBeforeRetrievalWhenNoRerankerIsConfigured(bool compare)
    {
        using var fixture = new Fixture();
        await fixture.State.SaveAsync(new SampleState(EmbedderId, LlmId, null, SpaceId, []), CancellationToken.None);
        var service = fixture.Service(Settings());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (compare)
            {
                await service.CompareAsync(new CompareRequest("test", "Refund policy?"), CancellationToken.None);
            }
            else
            {
                await service.ChatAsync(new ChatRequest("test", "Refund policy?", true), CancellationToken.None);
            }
        });

        Assert.Contains("Reranking is unavailable", error.Message);
        Assert.Empty(fixture.Handler.Requests);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("goodmem-provider-test-").FullName;
        public RecordingHandler Handler { get; } = new();
        private readonly HttpClient _http;
        private readonly GoodmemClient _client;
        private readonly TestEnvironment _environment;
        public SampleStateStore State { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "SeedData"));
            _environment = new TestEnvironment { ContentRootPath = Root };
            State = new SampleStateStore(_environment);
            _http = new HttpClient(Handler);
            _client = new GoodmemClient(new GoodmemClientOptions
            {
                BaseUrl = "https://goodmem.test",
                ApiKey = "gm-test-key",
                HttpClient = _http,
            });
        }

        public GoodMemRagService Service(GoodMemSettings settings) => new(_client, settings, State, _environment);
        public JsonObject Request(string path) => Handler.Requests.Single(r => r.Path == path).Body;

        public void Dispose()
        {
            _client.Dispose();
            _http.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "GoodMem.Rag.Api.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Path, JsonObject Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("goodmem.test", request.RequestUri!.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("gm-test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var path = request.RequestUri.AbsolutePath;
            Requests.Add((path, body));
            var response = body.DeepClone().AsObject();
            response.Remove("credentials");
            response["labels"] = new JsonObject();
            response["supportedModalities"] ??= new JsonArray("TEXT");
            response["ownerId"] = "test-user";
            response["createdById"] = "test-user";
            response["updatedById"] = "test-user";
            response["createdAt"] = 0;
            response["updatedAt"] = 0;
            switch (path)
            {
                case "/v1/embedders": response["embedderId"] = EmbedderId; break;
                case "/v1/llms":
                    response["llmId"] = LlmId;
                    response["capabilities"] = new JsonObject();
                    response = new JsonObject { ["llm"] = response };
                    break;
                case "/v1/rerankers": response["rerankerId"] = RerankerId; break;
                case "/v1/spaces":
                    response["spaceId"] = SpaceId;
                    response["spaceEmbedders"] = new JsonArray();
                    break;
                default: throw new InvalidOperationException("Unexpected test request: " + path);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
