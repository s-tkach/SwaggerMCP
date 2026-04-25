using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwaggerMcp.Configuration;
using SwaggerMcp.Embeddings;
using SwaggerMcp.Models;
using SwaggerMcp.Storage;

namespace SwaggerMcp.Indexing;

public sealed class SwaggerIndexingService(
    IOptions<SwaggerMcpOptions> options,
    SwaggerFetcher fetcher,
    OpenApiChunker chunker,
    IEmbedder embedder,
    ISwaggerStore store,
    ILogger<SwaggerIndexingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);

        if (!options.Value.RefreshOnStartup)
        {
            return;
        }

        foreach (var source in options.Value.Sources)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var result = await RefreshAsync(source.Name, stoppingToken);
            if (result.Error is not null)
            {
                logger.LogWarning("Failed to index {ApiName}: {Error}", source.Name, result.Error);
            }
        }
    }

    public async Task<IReadOnlyList<RefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        using var throttler = new SemaphoreSlim(4);
        var tasks = options.Value.Sources.Select(async source =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                return await RefreshAsync(source.Name, cancellationToken);
            }
            finally
            {
                throttler.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<RefreshResult> RefreshAsync(string apiName, CancellationToken cancellationToken = default)
    {
        var source = options.Value.Sources.FirstOrDefault(
            configured => string.Equals(configured.Name, apiName, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            return new RefreshResult(apiName, false, 0, 0, 0, $"API source '{apiName}' is not configured.");
        }

        try
        {
            var fetched = await fetcher.FetchAsync(source.Url, cancellationToken);
            var existingHash = await store.GetSpecHashAsync(source.Name, cancellationToken);
            if (string.Equals(existingHash, fetched.Hash, StringComparison.Ordinal))
            {
                return new RefreshResult(source.Name, false, 0, 0, 0, null);
            }

            var document = chunker.Chunk(source.Name, fetched);
            var embeddings = new Dictionary<EndpointChunk, float[]>();
            foreach (var endpoint in document.Endpoints)
            {
                embeddings[endpoint] = await embedder.EmbedAsync(endpoint.EmbeddingText, cancellationToken);
            }

            return await store.UpsertDocumentAsync(document, embeddings, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh swagger source {ApiName}", apiName);
            return new RefreshResult(apiName, false, 0, 0, 0, ex.Message);
        }
    }
}
