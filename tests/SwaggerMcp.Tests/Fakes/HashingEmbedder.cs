using System.Security.Cryptography;
using System.Text;
using SwaggerMcp.Embeddings;

namespace SwaggerMcp.Tests.Fakes;

internal sealed class HashingEmbedder : IEmbedder
{
    public int Dimensions => 384;

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var vector = new float[Dimensions];
        foreach (var token in Tokenize(text))
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var bucket = BitConverter.ToUInt32(bytes, 0) % Dimensions;
            var sign = (bytes[4] & 1) == 0 ? 1f : -1f;
            vector[bucket] += sign;
        }

        EmbeddingMath.Normalize(vector);
        return ValueTask.FromResult(vector);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }
}
