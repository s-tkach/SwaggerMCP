using System.Text;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using SwaggerMcp.Models;

namespace SwaggerMcp.Indexing;

public sealed class OpenApiChunker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public EndpointDocument Chunk(string apiName, FetchedSwagger swagger)
    {
        var document = new OpenApiStringReader().Read(swagger.Json, out var diagnostic);
        if (diagnostic.Errors.Count > 0)
        {
            var errors = string.Join("; ", diagnostic.Errors.Select(error => error.Message));
            throw new InvalidOperationException($"OpenAPI document '{apiName}' has parse errors: {errors}");
        }

        var endpoints = new List<EndpointChunk>();
        foreach (var (path, pathItem) in document.Paths)
        {
            foreach (var (operationType, operation) in pathItem.Operations)
            {
                var verb = operationType.ToString().ToUpperInvariant();
                var parameters = MergeParameters(pathItem.Parameters, operation.Parameters);
                var requestSchema = ExtractRequestBody(operation.RequestBody);
                var responses = ExtractResponses(operation.Responses);
                var schemaSummary = BuildSchemaSummary(parameters, requestSchema, responses);
                var embeddingText = BuildEmbeddingText(verb, path, operation, parameters, schemaSummary);

                endpoints.Add(new EndpointChunk(
                    verb,
                    path,
                    operation.OperationId,
                    operation.Summary,
                    operation.Description,
                    operation.Tags.Select(tag => tag.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
                    JsonSerializer.Serialize(parameters, JsonOptions),
                    requestSchema is null ? null : JsonSerializer.Serialize(requestSchema, JsonOptions),
                    JsonSerializer.Serialize(responses, JsonOptions),
                    schemaSummary,
                    embeddingText));
            }
        }

        return new EndpointDocument(
            apiName,
            swagger.Url,
            document.Servers.FirstOrDefault()?.Url,
            document.Info.Title,
            document.Info.Version,
            swagger.Hash,
            endpoints);
    }

    private static IReadOnlyList<ParameterShape> MergeParameters(
        IList<OpenApiParameter>? pathParameters,
        IList<OpenApiParameter>? operationParameters)
    {
        return (pathParameters ?? [])
            .Concat(operationParameters ?? [])
            .Select(parameter => new ParameterShape(
                parameter.Name,
                parameter.In?.ToString() ?? "unknown",
                parameter.Required,
                SummarizeSchema(parameter.Schema, 0)))
            .ToList();
    }

    private static RequestBodyShape? ExtractRequestBody(OpenApiRequestBody? requestBody)
    {
        if (requestBody is null)
        {
            return null;
        }

        return new RequestBodyShape(
            requestBody.Required,
            requestBody.Content.ToDictionary(
                content => content.Key,
                content => SummarizeSchema(content.Value.Schema, 0)));
    }

    private static IReadOnlyDictionary<string, ResponseShape> ExtractResponses(OpenApiResponses responses)
    {
        return responses.ToDictionary(
            response => response.Key,
            response => new ResponseShape(
                response.Value.Description,
                response.Value.Content.ToDictionary(
                    content => content.Key,
                    content => SummarizeSchema(content.Value.Schema, 0))));
    }

    private static SchemaShape SummarizeSchema(OpenApiSchema? schema, int depth)
    {
        if (schema is null)
        {
            return new SchemaShape("unknown", null, [], [], null);
        }

        var type = schema.Type;
        if (schema.Reference is not null)
        {
            type = string.IsNullOrWhiteSpace(type) ? "object" : type;
        }

        if (schema.Items is not null)
        {
            return new SchemaShape(
                "array",
                schema.Reference?.Id,
                [],
                [],
                SummarizeSchema(schema.Items, depth + 1));
        }

        if (depth >= 2)
        {
            return new SchemaShape(type ?? "object", schema.Reference?.Id, [], schema.Required?.ToList() ?? [], null);
        }

        var properties = schema.Properties
            .Take(40)
            .Select(property => new SchemaPropertyShape(
                property.Key,
                SummarizeSchema(property.Value, depth + 1).Type,
                property.Value.Reference?.Id,
                property.Value.Enum.Count > 0))
            .ToList();

        return new SchemaShape(
            type ?? (properties.Count > 0 ? "object" : "unknown"),
            schema.Reference?.Id,
            properties,
            schema.Required?.ToList() ?? [],
            null);
    }

    private static string BuildSchemaSummary(
        IReadOnlyList<ParameterShape> parameters,
        RequestBodyShape? requestSchema,
        IReadOnlyDictionary<string, ResponseShape> responses)
    {
        var builder = new StringBuilder();

        if (parameters.Count > 0)
        {
            builder.Append("params: ");
            builder.Append(string.Join(", ", parameters.Select(parameter => $"{parameter.In}.{parameter.Name}:{parameter.Schema.Type}")));
            builder.AppendLine();
        }

        if (requestSchema is not null)
        {
            builder.Append("request: ");
            builder.Append(string.Join("; ", requestSchema.Content.Select(content => $"{content.Key}:{DescribeSchema(content.Value)}")));
            builder.AppendLine();
        }

        if (responses.Count > 0)
        {
            builder.Append("responses: ");
            builder.Append(string.Join("; ", responses.Select(response =>
                $"{response.Key}:{string.Join("|", response.Value.Content.Select(content => DescribeSchema(content.Value)))}")));
        }

        return builder.ToString().Trim();
    }

    private static string BuildEmbeddingText(
        string verb,
        string path,
        OpenApiOperation operation,
        IReadOnlyList<ParameterShape> parameters,
        string schemaSummary)
    {
        return string.Join('\n', new[]
        {
            $"{verb} {path}",
            operation.OperationId ?? string.Empty,
            operation.Summary ?? string.Empty,
            operation.Description ?? string.Empty,
            operation.Tags.Count > 0 ? $"tags: {string.Join(", ", operation.Tags.Select(tag => tag.Name))}" : string.Empty,
            parameters.Count > 0 ? $"params: {string.Join(", ", parameters.Select(parameter => parameter.Name))}" : string.Empty,
            schemaSummary
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string DescribeSchema(SchemaShape schema)
    {
        var name = schema.Ref is null ? schema.Type : $"{schema.Type} {schema.Ref}";
        var required = schema.Required.Count > 0 ? $" required[{string.Join(",", schema.Required)}]" : string.Empty;
        var properties = schema.Properties.Count > 0
            ? $" fields[{string.Join(",", schema.Properties.Select(property => $"{property.Name}:{property.Type}").Take(20))}]"
            : string.Empty;
        var items = schema.Items is null ? string.Empty : $" items[{DescribeSchema(schema.Items)}]";
        return $"{name}{required}{properties}{items}";
    }

    public sealed record ParameterShape(string Name, string In, bool Required, SchemaShape Schema);
    public sealed record RequestBodyShape(bool Required, IReadOnlyDictionary<string, SchemaShape> Content);
    public sealed record ResponseShape(string? Description, IReadOnlyDictionary<string, SchemaShape> Content);
    public sealed record SchemaShape(
        string Type,
        string? Ref,
        IReadOnlyList<SchemaPropertyShape> Properties,
        IReadOnlyList<string> Required,
        SchemaShape? Items);
    public sealed record SchemaPropertyShape(string Name, string Type, string? Ref, bool IsEnum);
}
