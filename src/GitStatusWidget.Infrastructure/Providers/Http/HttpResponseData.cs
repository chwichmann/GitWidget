using System.Net;
using System.Text.Json;

namespace GitStatusWidget.Infrastructure.Providers.Http;

/// <summary>
/// Buffered, size-limited response of a provider API call. Only JSON 2xx/304 responses reach providers as
/// data; everything else is classified by <see cref="ProviderHttpClient"/>.
/// </summary>
internal sealed class HttpResponseData
{
    private readonly byte[] _body;

    public HttpResponseData(HttpStatusCode statusCode, ResponseHeaders headers, byte[] body, Uri requestUri)
    {
        StatusCode = statusCode;
        Headers = headers;
        _body = body;
        RequestUri = requestUri;
    }

    public HttpStatusCode StatusCode { get; }

    public ResponseHeaders Headers { get; }

    public Uri RequestUri { get; }

    public bool IsNotModified => StatusCode == HttpStatusCode.NotModified;

    public int BodyLength => _body.Length;

    public T Deserialize<T>(JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(_body, options)
                ?? throw new ProviderResponseException("Response body was empty or null.");
        }
        catch (JsonException ex)
        {
            throw new ProviderResponseException($"Response is not valid JSON for {typeof(T).Name}: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Response headers relevant to providers, captured case-insensitively.
/// </summary>
internal sealed class ResponseHeaders
{
    private readonly Dictionary<string, string> _values;

    public ResponseHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            _values[name] = string.Join(", ", values);
        }
    }

    public string? this[string name] => _values.TryGetValue(name, out var value) ? value : null;

    public string? ETag => this["ETag"];

    public string? ContentType => this["Content-Type"];

    public int? GetInt(string name) =>
        int.TryParse(this[name], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    public long? GetLong(string name) =>
        long.TryParse(this[name], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// Thrown when a response cannot be mapped (malformed JSON, missing required field).
/// </summary>
internal sealed class ProviderResponseException : Exception
{
    public ProviderResponseException(string message)
        : base(message)
    {
    }

    public ProviderResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
