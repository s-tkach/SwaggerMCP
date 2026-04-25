using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SwaggerMcp.Configuration;
using SwaggerMcp.Embeddings;
using SwaggerMcp.Indexing;
using SwaggerMcp.Storage;
using SwaggerMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);
var appsettingsPath = GetAppsettingsOverridePath(args);

if (!string.IsNullOrWhiteSpace(appsettingsPath))
{
    builder.Configuration.AddJsonFile(appsettingsPath, optional: false, reloadOnChange: false);
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddOptions<SwaggerMcpOptions>()
    .Bind(builder.Configuration.GetSection(SwaggerMcpOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SwaggerMcpOptions>, SwaggerMcpOptionsValidator>();

builder.Services.AddHttpClient<SwaggerFetcher>();
builder.Services.AddSingleton<SchemaSummarizer>();
builder.Services.AddSingleton<OpenApiChunker>();
builder.Services.AddSingleton<IEmbedder, OnnxEmbedder>();
builder.Services.AddSingleton<SqliteSchemaInitializer>();
builder.Services.AddSingleton<SqliteVectorSearch>();
builder.Services.AddSingleton<ISwaggerStore, SqliteSwaggerStore>();
builder.Services.AddSingleton<SwaggerIndexingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SwaggerIndexingService>());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SwaggerTools>();

await builder.Build().RunAsync();

static string? GetAppsettingsOverridePath(string[] args)
{
    const string key = "--appsettings";

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (arg.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{key} requires a JSON file path value.");
            }

            return args[i + 1];
        }

        if (arg.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
        {
            var value = arg[(key.Length + 1)..];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{key} requires a JSON file path value.");
            }

            return value;
        }
    }

    return null;
}
