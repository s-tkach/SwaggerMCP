using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerMcp.Configuration;
using SwaggerMcp.Embeddings;
using SwaggerMcp.Indexing;
using SwaggerMcp.Models;
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
        var store = CreateStore(options);
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

    [Fact]
    public async Task GetEndpoints_FiltersTagsByExactJsonValue()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var options = Options.Create(new SwaggerMcpOptions { DatabasePath = databasePath });
        var store = CreateStore(options);
        var embedder = new HashingEmbedder();
        var document = new EndpointDocument(
            "tag-test",
            "https://tag-test.local/swagger.json",
            null,
            "Tag Test",
            "v1",
            "hash",
            [
                CreateEndpoint("/pets", "pet"),
                CreateEndpoint("/petty-cash", "petty")
            ]);

        var embeddings = document.Endpoints.ToDictionary(
            endpoint => endpoint,
            endpoint => embedder.EmbedAsync(endpoint.EmbeddingText).AsTask().GetAwaiter().GetResult());

        await store.UpsertDocumentAsync(document, embeddings);

        var endpoints = await store.GetEndpointsAsync("tag-test", "pet", null);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("/pets", endpoint.Path);
    }

    [Fact]
    public async Task UpsertDocument_RemovesEndpointsMissingFromLatestDocument()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var options = Options.Create(new SwaggerMcpOptions { DatabasePath = databasePath });
        var store = CreateStore(options);
        var embedder = new HashingEmbedder();
        var original = new EndpointDocument(
            "petstore",
            "https://petstore.local/swagger.json",
            null,
            "Petstore",
            "v1",
            "hash-1",
            [
                CreateEndpoint("/pets", "pets"),
                CreateEndpoint("/pets/{id}", "pets")
            ]);
        var updated = original with
        {
            SpecHash = "hash-2",
            Endpoints = [CreateEndpoint("/pets", "pets")]
        };

        await store.UpsertDocumentAsync(original, CreateEmbeddings(original, embedder));
        var refresh = await store.UpsertDocumentAsync(updated, CreateEmbeddings(updated, embedder));

        var endpoints = await store.GetEndpointsAsync("petstore", null, null);
        Assert.Equal(1, refresh.Removed);
        var endpoint = Assert.Single(endpoints);
        Assert.Equal("/pets", endpoint.Path);
    }

    private static SqliteSwaggerStore CreateStore(IOptions<SwaggerMcpOptions> options) =>
        new(
            options,
            new SqliteSchemaInitializer(NullLogger<SqliteSchemaInitializer>.Instance),
            new SqliteVectorSearch());

    private static EndpointChunk CreateEndpoint(string path, string tag) =>
        new(
            "GET",
            path,
            null,
            $"Get {tag}",
            null,
            [tag],
            "[]",
            null,
            "{}",
            $"responses: 200:{tag}",
            $"GET {path}\ntags: {tag}");

    private static IReadOnlyDictionary<EndpointChunk, float[]> CreateEmbeddings(EndpointDocument document, HashingEmbedder embedder) =>
        document.Endpoints.ToDictionary(
            endpoint => endpoint,
            endpoint => embedder.EmbedAsync(endpoint.EmbeddingText).AsTask().GetAwaiter().GetResult());
}
