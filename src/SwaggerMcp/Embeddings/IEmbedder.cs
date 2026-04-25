namespace SwaggerMcp.Embeddings;

public interface IEmbedder
{
    int Dimensions { get; }

    ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
