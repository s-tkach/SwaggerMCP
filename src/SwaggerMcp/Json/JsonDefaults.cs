using System.Text.Json;

namespace SwaggerMcp.Json;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
