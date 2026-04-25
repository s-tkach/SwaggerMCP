namespace SwaggerMcp.Models;

public sealed record ApiRecord(
    long Id,
    string Name,
    string SourceUrl,
    string? BaseUrl,
    string? Title,
    string? Version,
    string? SpecHash,
    DateTimeOffset? IndexedAt,
    int EndpointCount);
