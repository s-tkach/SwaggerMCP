using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using SwaggerMcp.Json;
using SwaggerMcp.Models;

namespace SwaggerMcp.Indexing;

public sealed class OpenApiChunker(SchemaSummarizer schemaSummarizer)
{
    public EndpointDocument Chunk(string apiName, FetchedSwagger swagger)
    {
        var document = new OpenApiStringReader().Read(swagger.Json, out var diagnostic);
        if (diagnostic.Errors.Count > 0)
        {
            var errors = string.Join("; ", diagnostic.Errors.Select(error => error.Message));
            throw new InvalidOperationException($"OpenAPI document '{apiName}' has parse errors: {errors}");
        }

        var endpoints = new List<EndpointChunk>();
        foreach (var (path, pathItem) in document.Paths)
        {
            foreach (var (operationType, operation) in pathItem.Operations)
            {
                var verb = operationType.ToString().ToUpperInvariant();
                var schemaSummary = schemaSummarizer.Summarize(
                    pathItem.Parameters,
                    operation.Parameters,
                    operation.RequestBody,
                    operation.Responses);
                var embeddingText = BuildEmbeddingText(verb, path, operation, schemaSummary.Parameters, schemaSummary.Text);

                endpoints.Add(new EndpointChunk(
                    verb,
                    path,
                    operation.OperationId,
                    operation.Summary,
                    operation.Description,
                    operation.Tags.Select(tag => tag.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
                    JsonSerializer.Serialize(schemaSummary.Parameters, JsonDefaults.Web),
                    schemaSummary.RequestBody is null ? null : JsonSerializer.Serialize(schemaSummary.RequestBody, JsonDefaults.Web),
                    JsonSerializer.Serialize(schemaSummary.Responses, JsonDefaults.Web),
                    schemaSummary.Text,
                    embeddingText));
            }
        }

        return new EndpointDocument(
            apiName,
            swagger.Url,
            document.Servers.FirstOrDefault()?.Url,
            document.Info.Title,
            document.Info.Version,
            swagger.Hash,
            endpoints);
    }

    private static string BuildEmbeddingText(
        string verb,
        string path,
        OpenApiOperation operation,
        IReadOnlyList<ParameterShape> parameters,
        string schemaSummary)
    {
        return string.Join('\n', new[]
        {
            $"{verb} {path}",
            operation.OperationId ?? string.Empty,
            operation.Summary ?? string.Empty,
            operation.Description ?? string.Empty,
            operation.Tags.Count > 0 ? $"tags: {string.Join(", ", operation.Tags.Select(tag => tag.Name))}" : string.Empty,
            parameters.Count > 0 ? $"params: {string.Join(", ", parameters.Select(parameter => parameter.Name))}" : string.Empty,
            schemaSummary
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}
