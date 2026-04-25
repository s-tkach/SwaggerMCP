using System.ComponentModel.DataAnnotations;

namespace SwaggerMcp.Configuration;

public sealed class SwaggerMcpOptions
{
    public const string SectionName = "SwaggerMcp";

    [Required]
    public string DatabasePath { get; init; } = "./data/swagger-mcp.db";

    [Required]
    public string EmbeddingModelPath { get; init; } = "./models/all-MiniLM-L6-v2-quantized.onnx";

    [Required]
    public string EmbeddingTokenizerPath { get; init; } = "./models/vocab.txt";

    public bool RefreshOnStartup { get; init; } = true;

    public IReadOnlyList<SwaggerSourceOptions> Sources { get; init; } = [];
}

public sealed class SwaggerSourceOptions
{
    [Required]
    public string Name { get; init; } = string.Empty;

    [Required]
    [Url]
    public string Url { get; init; } = string.Empty;
}
