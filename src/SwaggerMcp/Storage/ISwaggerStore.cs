using SwaggerMcp.Models;

namespace SwaggerMcp.Storage;

public interface ISwaggerStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiRecord>> ListApisAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EndpointRecord>> GetEndpointsAsync(
        string apiName,
        string? tag,
        string? verb,
        CancellationToken cancellationToken = default);

    Task<EndpointRecord?> GetEndpointDetailsAsync(
        string apiName,
        string verb,
        string path,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EndpointSearchResult>> SearchEndpointsAsync(
        float[] embedding,
        string? apiName,
        string? verb,
        int top,
        CancellationToken cancellationToken = default);

    Task<RefreshResult> UpsertDocumentAsync(
        EndpointDocument document,
        IReadOnlyDictionary<EndpointChunk, float[]> embeddings,
        CancellationToken cancellationToken = default);

    Task<string?> GetSpecHashAsync(string apiName, CancellationToken cancellationToken = default);
}
