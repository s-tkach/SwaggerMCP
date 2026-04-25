using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SwaggerMcp.Configuration;

namespace SwaggerMcp.Embeddings;

public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private readonly HashingEmbedder _fallback = new();
    private readonly ILogger<OnnxEmbedder> _logger;
    private readonly InferenceSession? _session;
    private readonly BertTokenizer? _tokenizer;

    public OnnxEmbedder(IOptions<SwaggerMcpOptions> options, ILogger<OnnxEmbedder> logger)
    {
        _logger = logger;

        var modelPath = options.Value.EmbeddingModelPath;
        var tokenizerPath = options.Value.EmbeddingTokenizerPath;
        if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
        {
            _logger.LogWarning(
                "Bundled ONNX assets were not found at {ModelPath} and {TokenizerPath}. Falling back to deterministic local hashing embeddings.",
                modelPath,
                tokenizerPath);
            return;
        }

        try
        {
            _session = new InferenceSession(modelPath);
            _tokenizer = BertTokenizer.Create(tokenizerPath, new BertOptions
            {
                LowerCaseBeforeTokenization = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize bundled ONNX embedder. Falling back to deterministic local hashing embeddings.");
        }
    }

    public int Dimensions => 384;

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_session is null || _tokenizer is null)
        {
            return _fallback.EmbedAsync(text, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var tokenIds = _tokenizer.EncodeToIds(text, addSpecialTokens: true, considerNormalization: true)
                .Take(256)
                .ToArray();
            if (tokenIds.Length == 0)
            {
                return ValueTask.FromResult(new float[Dimensions]);
            }

            var inputIds = new DenseTensor<long>(new[] { 1, tokenIds.Length });
            var attentionMask = new DenseTensor<long>(new[] { 1, tokenIds.Length });
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokenIds.Length });

            for (var i = 0; i < tokenIds.Length; i++)
            {
                inputIds[0, i] = tokenIds[i];
                attentionMask[0, i] = 1;
                tokenTypeIds[0, i] = 0;
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
            };

            if (_session.InputMetadata.ContainsKey("token_type_ids"))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
            }

            using var results = _session.Run(inputs);
            var tensor = results.First().AsTensor<float>();
            var vector = MeanPool(tensor, tokenIds.Length);
            Normalize(vector);
            return ValueTask.FromResult(vector);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ONNX embedding failed. Falling back to deterministic local hashing embeddings.");
            return _fallback.EmbedAsync(text, cancellationToken);
        }
    }

    public void Dispose() => _session?.Dispose();

    private static float[] MeanPool(Tensor<float> tensor, int tokenCount)
    {
        var vector = new float[384];
        var dimensions = tensor.Dimensions.ToArray();
        if (dimensions.Length < 3)
        {
            return vector;
        }

        var hidden = Math.Min(vector.Length, dimensions[2]);
        for (var token = 0; token < tokenCount; token++)
        {
            for (var dimension = 0; dimension < hidden; dimension++)
            {
                vector[dimension] += tensor[0, token, dimension];
            }
        }

        for (var dimension = 0; dimension < hidden; dimension++)
        {
            vector[dimension] /= tokenCount;
        }

        return vector;
    }

    private static void Normalize(float[] vector)
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
