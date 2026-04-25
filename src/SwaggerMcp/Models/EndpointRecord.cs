namespace SwaggerMcp.Models;

public sealed record EndpointRecord(
    long Id,
    long ApiId,
    string ApiName,
    string Verb,
    string Path,
    string? OperationId,
    string? Summary,
    string? Description,
    string TagsJson,
    string ParametersJson,
    string? RequestSchemaJson,
    string ResponsesJson,
    string SchemaSummary);

public sealed record EndpointDocument(
    string ApiName,
    string SourceUrl,
    string? BaseUrl,
    string? Title,
    string? Version,
    string SpecHash,
    IReadOnlyList<EndpointChunk> Endpoints);

public sealed record EndpointChunk(
    string Verb,
    string Path,
    string? OperationId,
    string? Summary,
    string? Description,
    IReadOnlyList<string> Tags,
    string ParametersJson,
    string? RequestSchemaJson,
    string ResponsesJson,
    string SchemaSummary,
    string EmbeddingText);

public sealed record EndpointSearchResult(
    string ApiName,
    string Verb,
    string Path,
    string? Summary,
    IReadOnlyList<string> Tags,
    double Score);

public sealed record RefreshResult(
    string ApiName,
    bool Refreshed,
    int Added,
    int Removed,
    int Changed,
    string? Error);
