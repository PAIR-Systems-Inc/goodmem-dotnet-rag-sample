using Goodmem.Client;
using GoodMem.Rag.Api;

var builder = WebApplication.CreateBuilder(args);

var settings = GoodMemSettings.FromEnvironment();
builder.Services.AddSingleton(settings);
if (!settings.VerifySsl)
{
    builder.Services.AddSingleton(
        new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            },
            disposeHandler: true
        )
        {
            Timeout = TimeSpan.FromMinutes(3),
        }
    );
}

builder.Services.AddSingleton(serviceProvider =>
    new GoodmemClient(
        new GoodmemClientOptions
        {
            BaseUrl = settings.BaseUrl,
            ApiKey = settings.ApiKey,
            Timeout = settings.VerifySsl ? TimeSpan.FromMinutes(3) : null,
            HttpClient = settings.VerifySsl
                ? null
                : serviceProvider.GetRequiredService<HttpClient>(),
        }
    )
);
builder.Services.AddSingleton<SampleStateStore>();
builder.Services.AddSingleton<GoodMemRagService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost(
    "/api/setup",
    async (GoodMemRagService rag, CancellationToken cancellationToken) =>
        Results.Ok(await rag.SetupAsync(cancellationToken))
);

app.MapPost(
    "/api/chat",
    async (ChatRequest request, GoodMemRagService rag, CancellationToken cancellationToken) =>
    {
        if (ValidateRequest(request.SessionId, request.Message) is { } error)
        {
            return error;
        }

        return Results.Ok(await rag.ChatAsync(request, cancellationToken));
    }
);

app.MapPost(
    "/api/compare",
    async (CompareRequest request, GoodMemRagService rag, CancellationToken cancellationToken) =>
    {
        if (ValidateRequest(request.SessionId, request.Message) is { } error)
        {
            return error;
        }

        return Results.Ok(await rag.CompareAsync(request, cancellationToken));
    }
);

app.Run();

static IResult? ValidateRequest(string? sessionId, string? message)
{
    var errors = new Dictionary<string, string[]>();
    if (!ChatRequest.IsValidSessionId(sessionId))
    {
        errors[nameof(ChatRequest.SessionId)] =
        ["Use 1-64 letters, numbers, underscores, or hyphens."];
    }

    if (string.IsNullOrWhiteSpace(message))
    {
        errors[nameof(ChatRequest.Message)] = ["Message is required."];
    }

    return errors.Count == 0 ? null : Results.ValidationProblem(errors);
}
