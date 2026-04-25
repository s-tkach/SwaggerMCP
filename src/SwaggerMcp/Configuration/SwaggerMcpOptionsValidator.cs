using Microsoft.Extensions.Options;

namespace SwaggerMcp.Configuration;

public sealed class SwaggerMcpOptionsValidator : IValidateOptions<SwaggerMcpOptions>
{
    public ValidateOptionsResult Validate(string? name, SwaggerMcpOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            errors.Add("SwaggerMcp:DatabasePath is required.");
        }

        if (string.IsNullOrWhiteSpace(options.EmbeddingModelPath))
        {
            errors.Add("SwaggerMcp:EmbeddingModelPath is required.");
        }

        if (string.IsNullOrWhiteSpace(options.EmbeddingTokenizerPath))
        {
            errors.Add("SwaggerMcp:EmbeddingTokenizerPath is required.");
        }

        var duplicateNames = options.Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateNames.Count > 0)
        {
            errors.Add($"SwaggerMcp:Sources contains duplicate API names: {string.Join(", ", duplicateNames)}.");
        }

        for (var i = 0; i < options.Sources.Count; i++)
        {
            var source = options.Sources[i];
            if (string.IsNullOrWhiteSpace(source.Name))
            {
                errors.Add($"SwaggerMcp:Sources:{i}:Name is required.");
            }

            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"SwaggerMcp:Sources:{i}:Url must be an absolute HTTP or HTTPS URL.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
