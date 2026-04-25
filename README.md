# Swagger MCP

Local MCP server for indexing multiple `/swagger/v1/swagger.json` OpenAPI documents and exposing endpoint discovery tools to VS Code Copilot Chat and Claude Desktop.

The server stores parsed endpoint metadata in SQLite, uses `sqlite-vec` for vector search when available, and embeds operation summaries/schemas with a built-in `sentence-transformers/all-MiniLM-L6-v2` ONNX model.

## Requirements

- .NET 10 SDK for local development (`dotnet --list-sdks` should show a 10.x SDK).
- Docker, when running the packaged MCP server.
- The `all-MiniLM-L6-v2` ONNX model and `vocab.txt` tokenizer under `models/` when running from source.

The ONNX model is about 90 MB. Local builds download and verify it when missing, using the URL and SHA-256 pinned in `src/SwaggerMcp/SwaggerMcp.csproj`. CI or test-only builds that do not need runtime embedding assets can skip that work with `/p:SkipEmbeddingModelDownload=true`.

## Tools

- `list_apis` - list indexed APIs with title, version, endpoint count, and index time.
- `get_endpoints(apiName, tag?, verb?)` - compact endpoint list for one service.
- `search_endpoints(query, apiName?, verb?, top?)` - semantic search across endpoint paths, summaries, parameters, and schema summaries.
- `get_endpoint_details(apiName, verb, path)` - full endpoint details including parameters, request schema, responses, and tags.
- `refresh_api(apiName?)` - refresh one API, or all configured APIs when omitted.

## Configure Sources

Copy `src/SwaggerMcp/appsettings.example.json` to your own `appsettings.json`, then edit the source list. When running Docker, mount your config into `/app/appsettings.json`:

```json
{
  "SwaggerMcp": {
    "DatabasePath": "./data/swagger-mcp.db",
    "EmbeddingModelPath": "./models/all-MiniLM-L6-v2.onnx",
    "EmbeddingTokenizerPath": "./models/vocab.txt",
    "RefreshOnStartup": true,
    "Sources": [
      { "name": "petstore", "url": "https://petstore.swagger.io/v2/swagger.json" },
      { "name": "fakerestapi", "url": "https://fakerestapi.azurewebsites.net/swagger/v1/swagger.json" }
    ]
  }
}
```

Swagger URLs are expected to be reachable without auth from the machine/container running the MCP server.

## Build Docker Image

```bash
docker build -t swagger-mcp:latest .
```

The Docker image includes:

- .NET 10 runtime app
- built-in `all-MiniLM-L6-v2` ONNX model and tokenizer
- `sqlite-vec` loadable extension for the target Docker architecture

The ONNX model is downloaded during build only when missing, pinned by URL and SHA-256 in `src/SwaggerMcp/SwaggerMcp.csproj`, and copied into publish/Docker output. The downloaded `.onnx` file is ignored by git. Runtime startup fails fast if the configured model or tokenizer cannot be loaded.

## Run with Docker Compose

Create `/Users/<your-user>/Documents/swagger-mcp/appsettings.json` (or update the path in `docker-compose.yml`), then run:

```bash
docker compose up -d
```

Useful commands:

```bash
docker compose logs -f swagger-mcp
docker compose restart swagger-mcp
docker compose down
```

## VS Code Copilot

Use a user-level or workspace MCP config:

```json
{
  "servers": {
    "swagger": {
      "command": "docker",
      "args": [
        "run", "--rm", "-i",
        "-v", "/Users/<your-user>/Documents/swagger-mcp/appsettings.json:/app/appsettings.json:ro",
        "-v", "/Users/<your-user>/Documents/swagger-mcp/data:/app/data",
        "swagger-mcp:latest"
      ]
    }
  }
}
```

You can also run directly from source with `dotnet`:

```json
{
  "servers": {
    "swagger": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/Users/<your-user>/Source/SwaggerMCP/src/SwaggerMcp/SwaggerMcp.csproj",
        "--",
        "--appsettings",
        "/Users/<your-user>/Documents/swagger-mcp/appsettings.json"
      ]
    }
  }
}
```

## Claude Desktop

Add this server to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "swagger": {
      "command": "docker",
      "args": [
        "run", "--rm", "-i",
        "-v", "/Users/<your-user>/Documents/swagger-mcp/appsettings.json:/app/appsettings.json:ro",
        "-v", "/Users/<your-user>/Documents/swagger-mcp/data:/app/data",
        "swagger-mcp:latest"
      ]
    }
  }
}
```

You can also run directly from source with `dotnet`:

```json
{
  "mcpServers": {
    "swagger": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/Users/<your-user>/Source/SwaggerMCP/src/SwaggerMcp/SwaggerMcp.csproj",
        "--",
        "--appsettings",
        "/Users/<your-user>/Documents/swagger-mcp/appsettings.json"
      ]
    }
  }
}
```

## Local Development

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/SwaggerMcp/SwaggerMcp.csproj
# override appsettings path
dotnet run --project src/SwaggerMcp/SwaggerMcp.csproj -- --appsettings /Users/<your-user>/Documents/swagger-mcp/appsettings.json
```

The source path examples use the repository directory name `SwaggerMCP`; project and namespace names use `SwaggerMcp`.

When the MCP client starts the server, logs are written to stderr so stdout remains reserved for MCP stdio messages.

## Notes

Large request and response schemas are summarized deterministically before embedding to avoid bloated search records. Full schema details remain available through `get_endpoint_details`.

If `sqlite-vec` cannot be loaded, the server falls back to a compatible local vector table and performs cosine similarity in process.

For every `dotnet run` example with `--appsettings`, `--` is required before `--appsettings` so `dotnet run` stops parsing its own options and forwards the remaining args to the application.
