namespace SwaggerMcp.Embeddings;

public static class EmbeddingMath
{
    public static void Normalize(float[] vector)
    {
        var sum = vector.Sum(value => value * value);
        if (sum <= 0)
        {
            return;
        }

        var length = MathF.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= length;
        }
    }
}
