using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SwaggerMcp.Embeddings;
using SwaggerMcp.Indexing;
using SwaggerMcp.Json;
using SwaggerMcp.Storage;

namespace SwaggerMcp.Tools;

[McpServerToolType]
public sealed class SwaggerTools(
    ISwaggerStore store,
    IEmbedder embedder,
    SwaggerIndexingService indexingService)
{
    [McpServerTool(Name = "list_apis")]
    [Description("List configured and indexed APIs with title, version, endpoint count, and last index time.")]
    public async Task<IReadOnlyList<ApiSummaryDto>> ListApis(CancellationToken cancellationToken = default)
    {
        var apis = await store.ListApisAsync(cancellationToken);
        return apis.Select(api => new ApiSummaryDto(
            api.Name,
            api.Title,
            api.Version,
            api.EndpointCount,
            api.IndexedAt)).ToList();
    }

    [McpServerTool(Name = "get_endpoints")]
    [Description("Return a compact list of endpoints for one API. Optional tag and verb filters are supported.")]
    public async Task<IReadOnlyList<EndpointSummaryDto>> GetEndpoints(
        [Description("Configured API name, for example billing-service.")] string apiName,
        [Description("Optional OpenAPI tag filter.")] string? tag = null,
        [Description("Optional HTTP verb filter, for example GET or POST.")] string? verb = null,
        CancellationToken cancellationToken = default)
    {
        var endpoints = await store.GetEndpointsAsync(apiName, tag, verb, cancellationToken);
        return endpoints.Select(endpoint => new EndpointSummaryDto(
            endpoint.Verb,
            endpoint.Path,
            endpoint.Summary,
            DeserializeTags(endpoint.TagsJson))).ToList();
    }

    [McpServerTool(Name = "search_endpoints")]
    [Description("Semantically search endpoint purpose, path, params, request schema, and response schema across indexed APIs.")]
    public async Task<IReadOnlyList<SearchHitDto>> SearchEndpoints(
        [Description("Natural-language search query, for example 'create invoice' or 'find users by email'.")] string query,
        [Description("Optional API name to limit search.")] string? apiName = null,
        [Description("Optional HTTP verb filter.")] string? verb = null,
        [Description("Maximum number of results.")] int top = 10,
        CancellationToken cancellationToken = default)
    {
        var embedding = await embedder.EmbedAsync(query, cancellationToken);
        var results = await store.SearchEndpointsAsync(embedding, apiName, verb, top, cancellationToken);
        return results.Select(result => new SearchHitDto(
            result.ApiName,
            result.Verb,
            result.Path,
            result.Summary,
            result.Tags,
            Math.Round(result.Score, 4))).ToList();
    }

    [McpServerTool(Name = "get_endpoint_details")]
    [Description("Return full endpoint details including parameters, request body schema, responses, tags, and summaries.")]
    public async Task<EndpointDetailsDto?> GetEndpointDetails(
        [Description("Configured API name, for example billing-service.")] string apiName,
        [Description("HTTP verb, for example GET or POST.")] string verb,
        [Description("OpenAPI path, for example /invoices/{id}.")] string path,
        CancellationToken cancellationToken = default)
    {
        var endpoint = await store.GetEndpointDetailsAsync(apiName, verb, path, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        return new EndpointDetailsDto(
            endpoint.ApiName,
            endpoint.Verb,
            endpoint.Path,
            endpoint.OperationId,
            endpoint.Summary,
            endpoint.Description,
            DeserializeTags(endpoint.TagsJson),
            DeserializeJson(endpoint.ParametersJson),
            endpoint.RequestSchemaJson is null ? null : DeserializeJson(endpoint.RequestSchemaJson),
            DeserializeJson(endpoint.ResponsesJson),
            endpoint.SchemaSummary);
    }

    [McpServerTool(Name = "refresh_api")]
    [Description("Re-fetch and re-index one configured API, or all APIs when apiName is omitted.")]
    public async Task<object> RefreshApi(
        [Description("Optional configured API name. Omit to refresh all APIs.")] string? apiName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiName))
        {
            return await indexingService.RefreshAllAsync(cancellationToken);
        }

        return await indexingService.RefreshAsync(apiName, cancellationToken);
    }

    private static IReadOnlyList<string> DeserializeTags(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonDefaults.Web) ?? [];

    private static object? DeserializeJson(string json) =>
        JsonSerializer.Deserialize<object>(json, JsonDefaults.Web);
}

public sealed record ApiSummaryDto(
    string Name,
    string? Title,
    string? Version,
    int EndpointCount,
    DateTimeOffset? IndexedAt);

public sealed record EndpointSummaryDto(
    string Verb,
    string Path,
    string? Summary,
    IReadOnlyList<string> Tags);

public sealed record SearchHitDto(
    string ApiName,
    string Verb,
    string Path,
    string? Summary,
    IReadOnlyList<string> Tags,
    double Score);

public sealed record EndpointDetailsDto(
    string ApiName,
    string Verb,
    string Path,
    string? OperationId,
    string? Summary,
    string? Description,
    IReadOnlyList<string> Tags,
    object? Parameters,
    object? RequestSchema,
    object? Responses,
    string SchemaSummary);
