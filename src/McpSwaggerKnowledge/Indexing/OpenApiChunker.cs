using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using McpSwaggerKnowledge.Json;
using McpSwaggerKnowledge.Models;

namespace McpSwaggerKnowledge.Indexing;

public sealed class OpenApiChunker(SchemaSummarizer schemaSummarizer, ILogger<OpenApiChunker> logger)
{
    public EndpointDocument Chunk(FetchedSwagger swagger)
    {
        var document = new OpenApiStringReader().Read(swagger.Json, out var diagnostic);
        var apiName = document.Info.Title;

        var pathSignatureErrors = diagnostic.Errors
            .Where(e => e.Message.Contains("path signature", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var fatalErrors = diagnostic.Errors.Except(pathSignatureErrors).ToList();

        foreach (var error in pathSignatureErrors)
        {
            logger.LogWarning(
                "OpenAPI document '{ApiName}' has a path-signature collision (spec violation — some endpoints may be missing from index): {Message}",
                apiName, error.Message);
        }

        if (fatalErrors.Count > 0)
        {
            var errors = string.Join("; ", fatalErrors.Select(e => e.Message));
            throw new InvalidOperationException($"OpenAPI document '{apiName}' has parse errors: {errors}");
        }

        foreach (var warning in diagnostic.Warnings)
        {
            logger.LogWarning("OpenAPI parse warning in '{ApiName}': {Message}", apiName, warning.Message);
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
        var parts = new List<string>(7) { $"{verb} {path}" };
        if (!string.IsNullOrWhiteSpace(operation.OperationId)) parts.Add(operation.OperationId);
        if (!string.IsNullOrWhiteSpace(operation.Summary)) parts.Add(operation.Summary);
        if (!string.IsNullOrWhiteSpace(operation.Description)) parts.Add(operation.Description);
        if (operation.Tags.Count > 0) parts.Add($"tags: {string.Join(", ", operation.Tags.Select(tag => tag.Name))}");
        if (parameters.Count > 0) parts.Add($"params: {string.Join(", ", parameters.Select(parameter => parameter.Name))}");
        if (!string.IsNullOrWhiteSpace(schemaSummary)) parts.Add(schemaSummary);
        return string.Join('\n', parts);
    }
}
