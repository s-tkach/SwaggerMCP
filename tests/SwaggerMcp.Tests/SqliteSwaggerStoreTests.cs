using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerMcp.Configuration;
using SwaggerMcp.Embeddings;
using SwaggerMcp.Indexing;
using SwaggerMcp.Storage;
using SwaggerMcp.Tests.Fixtures;

namespace SwaggerMcp.Tests;

public sealed class SqliteSwaggerStoreTests
{
    [Fact]
    public async Task Store_CanUpsertAndSearchEndpoint()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var options = Options.Create(new SwaggerMcpOptions { DatabasePath = databasePath });
        var store = new SqliteSwaggerStore(options, NullLogger<SqliteSwaggerStore>.Instance);
        var chunker = new OpenApiChunker();
        var embedder = new HashingEmbedder();
        var document = chunker.Chunk(
            "petstore",
            new FetchedSwagger("https://petstore.local/swagger/v1/swagger.json", PetstoreSwagger.Json, "hash"));

        await store.InitializeAsync();
        var embeddings = document.Endpoints.ToDictionary(
            endpoint => endpoint,
            endpoint => embedder.EmbedAsync(endpoint.EmbeddingText).AsTask().GetAwaiter().GetResult());

        var refresh = await store.UpsertDocumentAsync(document, embeddings);
        var searchVector = await embedder.EmbedAsync("create a pet");
        var results = await store.SearchEndpointsAsync(searchVector, null, "POST", 5);

        Assert.True(refresh.Refreshed);
        Assert.Equal(2, refresh.Added);
        var result = Assert.Single(results);
        Assert.Equal("petstore", result.ApiName);
        Assert.Equal("POST", result.Verb);
        Assert.Equal("/pets", result.Path);
    }
}
