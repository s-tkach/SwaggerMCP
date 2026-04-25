using System.Security.Cryptography;
using System.Text;

namespace SwaggerMcp.Indexing;

public sealed class SwaggerFetcher(HttpClient httpClient)
{
    public async Task<FetchedSwagger> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new FetchedSwagger(url, json, hash);
    }
}

public sealed record FetchedSwagger(string Url, string Json, string Hash);
