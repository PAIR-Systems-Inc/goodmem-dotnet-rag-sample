using System.Text.Json;

namespace GoodMem.Rag.Api;

public sealed record SampleState(
    string EmbedderId,
    string LlmId,
    string? RerankerId,
    string SpaceId,
    IReadOnlyList<string> MemoryIds
);

public sealed class SampleStateStore(IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path = Path.Combine(
        environment.ContentRootPath,
        "App_Data",
        "goodmem-state.json"
    );

    public async Task<SampleState?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<SampleState>(
            stream,
            JsonOptions,
            cancellationToken
        );
    }

    public async Task SaveAsync(SampleState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = $"{_path}.tmp";

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }
}
