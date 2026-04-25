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
builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
{
    ["--appsettings"] = "AppsettingsPath"
});

var appsettingsPath = builder.Configuration["AppsettingsPath"];
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
