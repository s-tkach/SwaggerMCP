using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SwaggerMcp.Configuration;

namespace SwaggerMcp.Embeddings;

public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private readonly ILogger<OnnxEmbedder> _logger;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;

    public OnnxEmbedder(IOptions<SwaggerMcpOptions> options, ILogger<OnnxEmbedder> logger)
    {
        _logger = logger;

        var modelPath = options.Value.EmbeddingModelPath;
        var tokenizerPath = options.Value.EmbeddingTokenizerPath;
        var missingAssets = new[]
        {
            File.Exists(modelPath) ? null : modelPath,
            File.Exists(tokenizerPath) ? null : tokenizerPath
        }.Where(path => path is not null);

        if (missingAssets.Any())
        {
            throw new InvalidOperationException(
                $"Required ONNX embedding assets were not found: {string.Join(", ", missingAssets)}.");
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
            throw new InvalidOperationException("Failed to initialize the ONNX embedder.", ex);
        }
    }

    public int Dimensions => 384;

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
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
            var vector = ExtractSentenceVector(tensor, tokenIds.Length);
            EmbeddingMath.Normalize(vector);
            return ValueTask.FromResult(vector);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ONNX embedding failed.");
            throw;
        }
    }

    public void Dispose() => _session?.Dispose();

    private static float[] ExtractSentenceVector(Tensor<float> tensor, int tokenCount)
    {
        var dimensions = tensor.Dimensions.ToArray();
        return dimensions.Length switch
        {
            2 => CopyPooledVector(tensor, dimensions),
            3 => MeanPool(tensor, tokenCount, dimensions),
            _ => new float[384]
        };
    }

    private static float[] CopyPooledVector(Tensor<float> tensor, int[] dimensions)
    {
        var vector = new float[384];
        var hidden = Math.Min(vector.Length, dimensions[1]);
        for (var dimension = 0; dimension < hidden; dimension++)
        {
            vector[dimension] = tensor[0, dimension];
        }

        return vector;
    }

    private static float[] MeanPool(Tensor<float> tensor, int tokenCount, int[] dimensions)
    {
        var vector = new float[384];
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

}
