using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using SwaggerMcp.Configuration;
using SwaggerMcp.Models;

namespace SwaggerMcp.Storage;

public sealed class SqliteSwaggerStore : ISwaggerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly ILogger<SqliteSwaggerStore> _logger;
    private bool _sqliteVecEnabled;

    public SqliteSwaggerStore(IOptions<SwaggerMcpOptions> options, ILogger<SqliteSwaggerStore> logger)
    {
        _logger = logger;
        var databasePath = Path.GetFullPath(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync("PRAGMA foreign_keys = ON;");
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS apis (
              id INTEGER PRIMARY KEY,
              name TEXT UNIQUE NOT NULL,
              source_url TEXT NOT NULL,
              base_url TEXT,
              title TEXT,
              version TEXT,
              spec_hash TEXT,
              indexed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS endpoints (
              id INTEGER PRIMARY KEY,
              api_id INTEGER NOT NULL REFERENCES apis(id) ON DELETE CASCADE,
              verb TEXT NOT NULL,
              path TEXT NOT NULL,
              operation_id TEXT,
              summary TEXT,
              description TEXT,
              tags TEXT NOT NULL,
              parameters_json TEXT NOT NULL,
              request_schema_json TEXT,
              responses_json TEXT NOT NULL,
              schema_summary TEXT NOT NULL,
              UNIQUE(api_id, verb, path)
            );
            """);

        _sqliteVecEnabled = await TryCreateVectorTableAsync(connection);
    }

    public async Task<IReadOnlyList<ApiRecord>> ListApisAsync(CancellationToken cancellationToken = default)
    {
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
              AND (@Tag IS NULL OR e.tags LIKE '%' || @Tag || '%')
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
        top = Math.Clamp(top, 1, 25);
        await using var connection = CreateConnection();

        var rows = await connection.QueryAsync<SearchRow>("""
            SELECT
                a.name AS ApiName,
                e.verb AS Verb,
                e.path AS Path,
                e.summary AS Summary,
                e.tags AS TagsJson,
                v.embedding AS EmbeddingJson
            FROM endpoints e
            JOIN apis a ON a.id = e.api_id
            JOIN endpoints_vec v ON v.endpoint_id = e.id
            WHERE (@ApiName IS NULL OR a.name = @ApiName)
              AND (@Verb IS NULL OR e.verb = upper(@Verb));
            """, new { ApiName = apiName, Verb = verb });

        return rows
            .Select(row => new EndpointSearchResult(
                row.ApiName,
                row.Verb,
                row.Path,
                row.Summary,
                DeserializeTags(row.TagsJson),
                CosineSimilarity(embedding, DeserializeVector(row.EmbeddingJson))))
            .OrderByDescending(result => result.Score)
            .Take(top)
            .ToList();
    }

    public async Task<RefreshResult> UpsertDocumentAsync(
        EndpointDocument document,
        IReadOnlyDictionary<EndpointChunk, float[]> embeddings,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
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

            await connection.ExecuteAsync("""
                INSERT OR REPLACE INTO endpoints_vec (endpoint_id, embedding)
                VALUES (@EndpointId, @Embedding);
                """, new
            {
                EndpointId = endpointId,
                Embedding = JsonSerializer.Serialize(embeddings[endpoint], JsonOptions)
            }, transaction);
        }

        var removedKeys = existing.Keys.Where(key => !seen.Contains(key)).ToList();
        foreach (var (oldVerb, oldPath) in removedKeys)
        {
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
        await using var connection = CreateConnection();
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT spec_hash FROM apis WHERE name = @ApiName;",
            new { ApiName = apiName });
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private async Task<bool> TryCreateVectorTableAsync(SqliteConnection connection)
    {
        var extensionPath = ResolveVecExtensionPath();
        if (string.IsNullOrWhiteSpace(extensionPath))
        {
            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS endpoints_vec (
                  endpoint_id INTEGER PRIMARY KEY REFERENCES endpoints(id) ON DELETE CASCADE,
                  embedding TEXT NOT NULL
                );
                """);
            return false;
        }

        try
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(extensionPath);
            await connection.ExecuteAsync("""
                CREATE VIRTUAL TABLE IF NOT EXISTS endpoints_vec USING vec0(
                  endpoint_id INTEGER PRIMARY KEY,
                  embedding FLOAT[384]
                );
                """);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sqlite-vec extension load failed from path '{ExtensionPath}'; using a compatible local vector table fallback.", extensionPath);
            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS endpoints_vec (
                  endpoint_id INTEGER PRIMARY KEY REFERENCES endpoints(id) ON DELETE CASCADE,
                  embedding TEXT NOT NULL
                );
                """);
            return false;
        }
    }

    private static string? ResolveVecExtensionPath()
    {
        var configured = Environment.GetEnvironmentVariable("SQLITE_VEC_EXTENSION_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var extension = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? ".dylib"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ".dll"
                : ".so";

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, $"vec0{extension}"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", $"vec0{extension}"),
            Path.Combine(AppContext.BaseDirectory, "native", $"vec0{extension}")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> DeserializeTags(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];

    private static float[] DeserializeVector(string json) =>
        JsonSerializer.Deserialize<float[]>(json, JsonOptions) ?? [];

    private static double CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length)
        {
            return 0;
        }

        double dot = 0;
        double leftLength = 0;
        double rightLength = 0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftLength += left[i] * left[i];
            rightLength += right[i] * right[i];
        }

        return leftLength <= 0 || rightLength <= 0
            ? 0
            : dot / (Math.Sqrt(leftLength) * Math.Sqrt(rightLength));
    }

    private sealed record SearchRow(
        string ApiName,
        string Verb,
        string Path,
        string? Summary,
        string TagsJson,
        string EmbeddingJson);
}
