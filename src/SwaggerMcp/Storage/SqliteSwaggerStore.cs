using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SwaggerMcp.Configuration;
using SwaggerMcp.Models;

namespace SwaggerMcp.Storage;

public sealed class SqliteSwaggerStore : ISwaggerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SqliteSchemaInitializer _schemaInitializer;
    private readonly SqliteVectorSearch _vectorSearch;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;
    private SqliteVectorMode _vectorMode = SqliteVectorMode.JsonFallback;

    public SqliteSwaggerStore(
        IOptions<SwaggerMcpOptions> options,
        SqliteSchemaInitializer schemaInitializer,
        SqliteVectorSearch vectorSearch)
    {
        var databasePath = Path.GetFullPath(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _schemaInitializer = schemaInitializer;
        _vectorSearch = vectorSearch;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            _vectorMode = await _schemaInitializer.InitializeAsync(connection);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<IReadOnlyList<ApiRecord>> ListApisAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<ApiRecord>("""
            SELECT
                a.id AS Id,
                a.name AS Name,
                a.source_url AS SourceUrl,
                a.base_url AS BaseUrl,
                a.title AS Title,
                a.version AS Version,
                a.spec_hash AS SpecHash,
                a.indexed_at AS IndexedAt,
                COUNT(e.id) AS EndpointCount
            FROM apis a
            LEFT JOIN endpoints e ON e.api_id = a.id
            GROUP BY a.id
            ORDER BY a.name;
            """);

        return rows.ToList();
    }

    public async Task<IReadOnlyList<EndpointRecord>> GetEndpointsAsync(
        string apiName,
        string? tag,
        string? verb,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<EndpointRecord>("""
            SELECT
                e.id AS Id,
                e.api_id AS ApiId,
                a.name AS ApiName,
                e.verb AS Verb,
                e.path AS Path,
                e.operation_id AS OperationId,
                e.summary AS Summary,
                e.description AS Description,
                e.tags AS TagsJson,
                e.parameters_json AS ParametersJson,
                e.request_schema_json AS RequestSchemaJson,
                e.responses_json AS ResponsesJson,
                e.schema_summary AS SchemaSummary
            FROM endpoints e
            JOIN apis a ON a.id = e.api_id
            WHERE a.name = @ApiName
              AND (@Verb IS NULL OR e.verb = upper(@Verb))
              AND (
                @Tag IS NULL OR EXISTS (
                  SELECT 1 FROM json_each(e.tags)
                  WHERE value = @Tag
                )
              )
            ORDER BY e.path, e.verb;
            """, new { ApiName = apiName, Tag = tag, Verb = verb });

        return rows.ToList();
    }

    public async Task<EndpointRecord?> GetEndpointDetailsAsync(
        string apiName,
        string verb,
        string path,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<EndpointRecord>("""
            SELECT
                e.id AS Id,
                e.api_id AS ApiId,
                a.name AS ApiName,
                e.verb AS Verb,
                e.path AS Path,
                e.operation_id AS OperationId,
                e.summary AS Summary,
                e.description AS Description,
                e.tags AS TagsJson,
                e.parameters_json AS ParametersJson,
                e.request_schema_json AS RequestSchemaJson,
                e.responses_json AS ResponsesJson,
                e.schema_summary AS SchemaSummary
            FROM endpoints e
            JOIN apis a ON a.id = e.api_id
            WHERE a.name = @ApiName AND e.verb = upper(@Verb) AND e.path = @Path;
            """, new { ApiName = apiName, Verb = verb, Path = path });
    }

    public async Task<IReadOnlyList<EndpointSearchResult>> SearchEndpointsAsync(
        float[] embedding,
        string? apiName,
        string? verb,
        int top,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(loadVectorExtension: true, cancellationToken);
        return await _vectorSearch.SearchAsync(connection, _vectorMode, embedding, apiName, verb, top);
    }

    public async Task<RefreshResult> UpsertDocumentAsync(
        EndpointDocument document,
        IReadOnlyDictionary<EndpointChunk, float[]> embeddings,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(loadVectorExtension: true, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = (await connection.QueryAsync<(string Verb, string Path, string Hash)>(
            "SELECT verb AS Verb, path AS Path, coalesce(schema_summary, '') AS Hash FROM endpoints WHERE api_id = (SELECT id FROM apis WHERE name = @Name);",
            new { Name = document.ApiName },
            transaction)).ToDictionary(row => (row.Verb, row.Path), row => row.Hash);

        var apiId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO apis (name, source_url, base_url, title, version, spec_hash, indexed_at)
            VALUES (@Name, @SourceUrl, @BaseUrl, @Title, @Version, @SpecHash, @IndexedAt)
            ON CONFLICT(name) DO UPDATE SET
                source_url = excluded.source_url,
                base_url = excluded.base_url,
                title = excluded.title,
                version = excluded.version,
                spec_hash = excluded.spec_hash,
                indexed_at = excluded.indexed_at
            RETURNING id;
            """, new
        {
            Name = document.ApiName,
            document.SourceUrl,
            document.BaseUrl,
            document.Title,
            document.Version,
            document.SpecHash,
            IndexedAt = DateTimeOffset.UtcNow
        }, transaction);

        var seen = new HashSet<(string Verb, string Path)>();
        var added = 0;
        var changed = 0;

        foreach (var endpoint in document.Endpoints)
        {
            var key = (endpoint.Verb, endpoint.Path);
            seen.Add(key);
            if (!existing.TryGetValue(key, out var oldSummary))
            {
                added++;
            }
            else if (!string.Equals(oldSummary, endpoint.SchemaSummary, StringComparison.Ordinal))
            {
                changed++;
            }

            var endpointId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO endpoints (
                    api_id, verb, path, operation_id, summary, description, tags,
                    parameters_json, request_schema_json, responses_json, schema_summary)
                VALUES (
                    @ApiId, @Verb, @Path, @OperationId, @Summary, @Description, @Tags,
                    @ParametersJson, @RequestSchemaJson, @ResponsesJson, @SchemaSummary)
                ON CONFLICT(api_id, verb, path) DO UPDATE SET
                    operation_id = excluded.operation_id,
                    summary = excluded.summary,
                    description = excluded.description,
                    tags = excluded.tags,
                    parameters_json = excluded.parameters_json,
                    request_schema_json = excluded.request_schema_json,
                    responses_json = excluded.responses_json,
                    schema_summary = excluded.schema_summary
                RETURNING id;
                """, new
            {
                ApiId = apiId,
                endpoint.Verb,
                endpoint.Path,
                endpoint.OperationId,
                endpoint.Summary,
                endpoint.Description,
                Tags = JsonSerializer.Serialize(endpoint.Tags, JsonOptions),
                endpoint.ParametersJson,
                endpoint.RequestSchemaJson,
                endpoint.ResponsesJson,
                endpoint.SchemaSummary
            }, transaction);

            await _vectorSearch.UpsertEmbeddingAsync(connection, transaction, _vectorMode, endpointId, embeddings[endpoint]);
        }

        var removedKeys = existing.Keys.Where(key => !seen.Contains(key)).ToList();
        foreach (var (oldVerb, oldPath) in removedKeys)
        {
            var endpointId = await connection.ExecuteScalarAsync<long?>(
                "SELECT id FROM endpoints WHERE api_id = @ApiId AND verb = @Verb AND path = @Path;",
                new { ApiId = apiId, Verb = oldVerb, Path = oldPath },
                transaction);

            if (endpointId is not null)
            {
                await _vectorSearch.DeleteEmbeddingAsync(connection, transaction, _vectorMode, endpointId.Value);
            }

            await connection.ExecuteAsync(
                "DELETE FROM endpoints WHERE api_id = @ApiId AND verb = @Verb AND path = @Path;",
                new { ApiId = apiId, Verb = oldVerb, Path = oldPath },
                transaction);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RefreshResult(document.ApiName, true, added, removedKeys.Count, changed, null);
    }

    public async Task<string?> GetSpecHashAsync(string apiName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = CreateConnection();
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT spec_hash FROM apis WHERE name = @ApiName;",
            new { ApiName = apiName });
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private async Task<SqliteConnection> OpenConnectionAsync(bool loadVectorExtension, CancellationToken cancellationToken)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (loadVectorExtension && _vectorMode == SqliteVectorMode.SqliteVec)
        {
            _schemaInitializer.LoadVectorExtension(connection);
        }

        return connection;
    }

    private static IReadOnlyList<string> DeserializeTags(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];
}
