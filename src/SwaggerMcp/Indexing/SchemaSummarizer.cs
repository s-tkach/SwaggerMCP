using System.Text;
using Microsoft.OpenApi.Models;

namespace SwaggerMcp.Indexing;

public sealed class SchemaSummarizer
{
    public EndpointSchemaSummary Summarize(
        IList<OpenApiParameter>? pathParameters,
        IList<OpenApiParameter>? operationParameters,
        OpenApiRequestBody? requestBody,
        OpenApiResponses responses)
    {
        var parameters = MergeParameters(pathParameters, operationParameters);
        var requestSchema = ExtractRequestBody(requestBody);
        var responseShapes = ExtractResponses(responses);
        var text = BuildSchemaSummary(parameters, requestSchema, responseShapes);
        return new EndpointSchemaSummary(parameters, requestSchema, responseShapes, text);
    }

    private static IReadOnlyList<ParameterShape> MergeParameters(
        IList<OpenApiParameter>? pathParameters,
        IList<OpenApiParameter>? operationParameters)
    {
        return (pathParameters ?? [])
            .Concat(operationParameters ?? [])
            .GroupBy(parameter => (parameter.Name, In: FormatParameterLocation(parameter)))
            .Select(group => group.Last())
            .Select(parameter => new ParameterShape(
                parameter.Name,
                FormatParameterLocation(parameter),
                parameter.Required,
                SummarizeSchema(parameter.Schema, 0)))
            .ToList();
    }

    private static string FormatParameterLocation(OpenApiParameter parameter) =>
        parameter.In?.ToString().ToLowerInvariant() ?? "unknown";

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
}

public sealed record EndpointSchemaSummary(
    IReadOnlyList<ParameterShape> Parameters,
    RequestBodyShape? RequestBody,
    IReadOnlyDictionary<string, ResponseShape> Responses,
    string Text);

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
