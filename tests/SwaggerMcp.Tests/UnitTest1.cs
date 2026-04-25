using SwaggerMcp.Indexing;
using SwaggerMcp.Tests.Fixtures;

namespace SwaggerMcp.Tests;

public class OpenApiChunkerTests
{
    [Fact]
    public void Chunk_CreatesOneRecordPerOperation()
    {
        var chunker = new OpenApiChunker();
        var fetched = new FetchedSwagger("https://petstore.local/swagger/v1/swagger.json", PetstoreSwagger.Json, "hash");

        var document = chunker.Chunk("petstore", fetched);

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
        var chunker = new OpenApiChunker();
        var fetched = new FetchedSwagger("https://petstore.local/swagger/v1/swagger.json", PetstoreSwagger.Json, "hash");

        var document = chunker.Chunk("petstore", fetched);
        var post = Assert.Single(document.Endpoints, endpoint => endpoint.Verb == "POST");

        Assert.Contains("request:", post.SchemaSummary);
        Assert.Contains("name:string", post.SchemaSummary);
        Assert.Contains("Create a pet", post.EmbeddingText);
    }

    [Fact]
    public void Chunk_OperationParametersOverridePathParameters()
    {
        var chunker = new OpenApiChunker();
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

        var document = chunker.Chunk("pets", fetched);
        var endpoint = Assert.Single(document.Endpoints);

        Assert.Contains("path.id:integer", endpoint.SchemaSummary);
        Assert.DoesNotContain("path.id:string", endpoint.SchemaSummary);
    }
}
