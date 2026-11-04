using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitStatusWidget.Application.Abstractions;

namespace GitStatusWidget.Infrastructure.Providers.Http;

/// <summary>
/// Minimal read-only GraphQL helper. GraphQL reports errors with HTTP 200 and an <c>errors</c> array; any error
/// fails the request, partial <c>data</c> is never used.
/// </summary>
internal static class GraphQl
{
    public static async Task<TData> QueryAsync<TData>(
        ProviderHttpClient client,
        Uri endpoint,
        string query,
        IReadOnlyDictionary<string, object?> variables,
        string userAgent,
        Action<HttpRequestHeaders> configureHeaders,
        CancellationToken cancellationToken)
        where TData : class
    {
        var payload = JsonSerializer.Serialize(new GraphQlRequest(query, variables), ProviderJson.CamelCase);
        var response = await client.SendAsync(
            new ProviderRequest
            {
                Uri = endpoint,
                Method = HttpMethod.Post,
                UserAgent = userAgent,
                ConfigureHeaders = configureHeaders,
                Content = () => new StringContent(payload, Encoding.UTF8, "application/json"),
            },
            cancellationToken).ConfigureAwait(false);

        var envelope = response.Deserialize<GraphQlResponse<TData>>(ProviderJson.CamelCase);
        if (envelope.Errors is { Count: > 0 } errors)
        {
            var first = errors[0];
            var kind = string.Equals(first.Type, "NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                ? ProviderFailureKind.NotFound
                : ProviderFailureKind.Malformed;
            throw new GraphQlErrorException(kind, $"GraphQL error: {Truncate(first.Message)}");
        }

        return envelope.Data ?? throw new ProviderFailureException(ProviderFailureKind.Malformed, "GraphQL response contained no data.");
    }

    private static string Truncate(string? message) =>
        string.IsNullOrEmpty(message) ? "unknown" : message.Length <= 200 ? message : message[..200];

    private sealed record GraphQlRequest(string Query, IReadOnlyDictionary<string, object?> Variables);

    private sealed record GraphQlResponse<TData>(TData? Data, IReadOnlyList<GraphQlError>? Errors);

    private sealed record GraphQlError(string? Message, string? Type, [property: JsonPropertyName("extensions")] JsonElement? Extensions);
}

/// <summary>
/// The GraphQL endpoint answered (HTTP 200) but reported <c>errors</c>. Distinguishes query errors from endpoint
/// failures (missing route, gateway, 5xx), which may justify a REST fallback.
/// </summary>
internal sealed class GraphQlErrorException(ProviderFailureKind kind, string message) : ProviderFailureException(kind, message);
