using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SwaggerMcp.Storage;

public sealed class SqliteSchemaInitializer(ILogger<SqliteSchemaInitializer> logger)
{
    public const string SqliteVecTableName = "endpoints_vec";
    public const string JsonFallbackTableName = "endpoints_vec_json";

    private string? _extensionPath;

    public async Task<SqliteVectorMode> InitializeAsync(SqliteConnection connection)
    {
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

        return await TryCreateSqliteVecTableAsync(connection)
            ? SqliteVectorMode.SqliteVec
            : await CreateJsonFallbackTableAsync(connection);
    }

    public void LoadVectorExtension(SqliteConnection connection)
    {
        if (string.IsNullOrWhiteSpace(_extensionPath))
        {
            return;
        }

        connection.EnableExtensions(true);
        connection.LoadExtension(_extensionPath);
    }

    private async Task<bool> TryCreateSqliteVecTableAsync(SqliteConnection connection)
    {
        _extensionPath = ResolveVecExtensionPath();
        if (string.IsNullOrWhiteSpace(_extensionPath))
        {
            return false;
        }

        try
        {
            LoadVectorExtension(connection);
            var existingSql = await GetTableSqlAsync(connection, SqliteVecTableName);
            if (existingSql is not null && !existingSql.Contains("USING vec0", StringComparison.OrdinalIgnoreCase))
            {
                await connection.ExecuteAsync($"DROP TABLE {SqliteVecTableName};");
            }

            await connection.ExecuteAsync("""
                CREATE VIRTUAL TABLE IF NOT EXISTS endpoints_vec USING vec0(
                  embedding FLOAT[384]
                );
                """);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "sqlite-vec extension load failed from path '{ExtensionPath}'; using a compatible local vector table fallback.", _extensionPath);
            _extensionPath = null;
            return false;
        }
    }

    private static async Task<SqliteVectorMode> CreateJsonFallbackTableAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS endpoints_vec_json (
              endpoint_id INTEGER PRIMARY KEY REFERENCES endpoints(id) ON DELETE CASCADE,
              embedding TEXT NOT NULL
            );
            """);

        await MigrateLegacyJsonFallbackTableAsync(connection);
        return SqliteVectorMode.JsonFallback;
    }

    private static async Task MigrateLegacyJsonFallbackTableAsync(SqliteConnection connection)
    {
        var existingSql = await GetTableSqlAsync(connection, SqliteVecTableName);
        if (existingSql is null || existingSql.Contains("USING vec0", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await connection.ExecuteAsync($"""
            INSERT OR IGNORE INTO {JsonFallbackTableName} (endpoint_id, embedding)
            SELECT endpoint_id, embedding
            FROM {SqliteVecTableName};

            DROP TABLE {SqliteVecTableName};
            """);
    }

    private static Task<string?> GetTableSqlAsync(SqliteConnection connection, string tableName) =>
        connection.ExecuteScalarAsync<string?>("SELECT sql FROM sqlite_master WHERE name = @TableName;", new { TableName = tableName });

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

        return candidates.FirstOrDefault(File.Exists);
    }
}
