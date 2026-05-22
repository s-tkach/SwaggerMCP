using Microsoft.Extensions.Logging.Abstractions;
using McpSwaggerKnowledge.Indexing;
using McpSwaggerKnowledge.Tests.Fixtures;

namespace McpSwaggerKnowledge.Tests;

public sealed class OpenApiChunkerTests
{
    [Fact]
    public void Chunk_CreatesOneRecordPerOperation()
    {
        var chunker = CreateChunker();
        var fetched = new FetchedSwagger("https://petstore.local/swagger/v1/swagger.json", PetstoreSwagger.Json, "hash");

        var document = chunker.Chunk(fetched);

        Assert.Equal("Petstore", document.ApiName);
        Assert.Equal("Petstore", document.Title);
        Assert.Equal("v1", document.Version);
        Assert.Equal("https://petstore.local", document.BaseUrl);
        Assert.Equal(2, document.Endpoints.Count);
        Assert.Contains(document.Endpoints, endpoint => endpoint.Verb == "GET" && endpoint.Path == "/pets");
        Assert.Contains(document.Endpoints, endpoint => endpoint.Verb == "POST" && endpoint.Path == "/pets");
    }

    [Fact]
    public void Chunk_SummarizesSchemasForEmbeddings()
    {
        var chunker = CreateChunker();
        var fetched = new FetchedSwagger("https://petstore.local/swagger/v1/swagger.json", PetstoreSwagger.Json, "hash");

        var document = chunker.Chunk(fetched);
        var post = Assert.Single(document.Endpoints, endpoint => endpoint.Verb == "POST");

        Assert.Contains("request:", post.SchemaSummary);
        Assert.Contains("name:string", post.SchemaSummary);
        Assert.Contains("Create a pet", post.EmbeddingText);
    }

    [Fact]
    public void Chunk_OperationParametersOverridePathParameters()
    {
        var chunker = CreateChunker();
        var fetched = new FetchedSwagger("https://pets.local/swagger.json", """
            {
              "openapi": "3.0.1",
              "info": { "title": "Pets", "version": "v1" },
              "paths": {
                "/pets/{id}": {
                  "parameters": [
                    { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                  ],
                  "get": {
                    "parameters": [
                      { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } }
                    ],
                    "responses": { "200": { "description": "OK" } }
                  }
                }
              }
            }
            """, "hash");

        var document = chunker.Chunk(fetched);
        var endpoint = Assert.Single(document.Endpoints);

        Assert.Equal("Pets", document.ApiName);
        Assert.Contains("path.id:integer", endpoint.SchemaSummary);
        Assert.DoesNotContain("path.id:string", endpoint.SchemaSummary);
    }

    [Fact]
    public void Chunk_PathSignatureCollision_LogsWarningAndContinues()
    {
        var chunker = CreateChunker();
        var fetched = new FetchedSwagger("https://pets.local/swagger.json", """
            {
              "openapi": "3.0.1",
              "info": { "title": "Pets", "version": "v1" },
              "paths": {
                "/pets/{petId}": {
                  "get": { "operationId": "getByPetId", "responses": { "200": { "description": "OK" } } }
                },
                "/pets/{id}": {
                  "get": { "operationId": "getById", "responses": { "200": { "description": "OK" } } }
                }
              }
            }
            """, "hash");

        var document = chunker.Chunk(fetched);

        Assert.NotEmpty(document.Endpoints);
    }

    private static OpenApiChunker CreateChunker() => new(new SchemaSummarizer(), NullLogger<OpenApiChunker>.Instance);
}
