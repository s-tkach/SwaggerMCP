using Microsoft.Extensions.Options;

namespace SwaggerMcp.Configuration;

public sealed class SwaggerMcpOptionsValidator : IValidateOptions<SwaggerMcpOptions>
{
    public ValidateOptionsResult Validate(string? name, SwaggerMcpOptions options)
    {
        var errors = new List<string>();

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

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
