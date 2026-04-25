using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerMcp.Configuration;
using SwaggerMcp.Embeddings;

namespace SwaggerMcp.Tests;

public sealed class OnnxEmbedderTests
{
    [Fact]
    public void Constructor_ThrowsWhenModelAssetsAreMissing()
    {
        var options = Options.Create(new SwaggerMcpOptions
        {
            EmbeddingModelPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx"),
            EmbeddingTokenizerPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt")
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new OnnxEmbedder(options, NullLogger<OnnxEmbedder>.Instance));

        Assert.Contains("Required ONNX embedding assets were not found", exception.Message);
    }
}
