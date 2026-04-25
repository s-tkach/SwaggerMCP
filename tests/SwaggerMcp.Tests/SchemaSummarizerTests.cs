using Microsoft.OpenApi.Models;
using SwaggerMcp.Indexing;

namespace SwaggerMcp.Tests;

public sealed class SchemaSummarizerTests
{
    private readonly SchemaSummarizer _summarizer = new();

    [Fact]
    public void Summarize_PreservesRequiredRequestBodyFlag()
    {
        var requestBody = new OpenApiRequestBody
        {
            Required = true,
            Content =
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = "object",
                        Required = new HashSet<string> { "name" },
                        Properties =
                        {
                            ["name"] = new OpenApiSchema { Type = "string" }
                        }
                    }
                }
            }
        };

        var summary = _summarizer.Summarize(null, null, requestBody, []);

        Assert.NotNull(summary.RequestBody);
        Assert.True(summary.RequestBody.Required);
        Assert.Contains("name:string", summary.Text);
    }

    [Fact]
    public void Summarize_LimitsLargeObjectPropertyLists()
    {
        var schema = new OpenApiSchema { Type = "object" };
        for (var i = 0; i < 45; i++)
        {
            schema.Properties[$"field{i}"] = new OpenApiSchema { Type = "string" };
        }

        var requestBody = new OpenApiRequestBody
        {
            Content =
            {
                ["application/json"] = new OpenApiMediaType { Schema = schema }
            }
        };

        var summary = _summarizer.Summarize(null, null, requestBody, []);
        var shape = summary.RequestBody!.Content["application/json"];

        Assert.Equal(40, shape.Properties.Count);
    }

    [Fact]
    public void Summarize_IncludesArrayItemShape()
    {
        var responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse
            {
                Description = "OK",
                Content =
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "array",
                            Items = new OpenApiSchema { Type = "string" }
                        }
                    }
                }
            }
        };

        var summary = _summarizer.Summarize(null, null, null, responses);

        Assert.Contains("items[string", summary.Text);
    }
}
