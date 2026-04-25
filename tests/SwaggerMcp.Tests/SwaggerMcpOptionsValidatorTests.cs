using SwaggerMcp.Configuration;

namespace SwaggerMcp.Tests;

public sealed class SwaggerMcpOptionsValidatorTests
{
    private readonly SwaggerMcpOptionsValidator _validator = new();

    [Fact]
    public void Validate_AllowsEmptySources()
    {
        var result = _validator.Validate(null, new SwaggerMcpOptions());

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validate_RejectsDuplicateSourceNames()
    {
        var result = _validator.Validate(null, new SwaggerMcpOptions
        {
            Sources =
            [
                new SwaggerSourceOptions { Name = "billing", Url = "https://billing.local/swagger.json" },
                new SwaggerSourceOptions { Name = "BILLING", Url = "https://billing2.local/swagger.json" }
            ]
        });

        Assert.True(result.Failed);
        Assert.Contains("duplicate API names", string.Join('\n', result.Failures));
    }
}
