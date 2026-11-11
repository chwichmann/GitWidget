using System.Globalization;
using System.Net;
using System.Text.Json;
using GitStatusWidget.Application.Usage;
using GitStatusWidget.Infrastructure.Providers;

namespace GitStatusWidget.Infrastructure.Usage;

/// <summary>
/// Reads the usage of the Claude subscription from the endpoint Claude Code itself uses (issue #17), authenticated
/// with the existing Claude Code sign-in. The endpoint is undocumented, so parsing is tolerant: unknown fields are
/// ignored and limits that are missing or <see langword="null"/> are left out. The token is sent only to
/// <see cref="ClaudeUsageOptions.Endpoint"/> and never logged.
/// </summary>
internal sealed class ClaudeUsageSource : IClaudeUsageSource
{
    /// <summary>Name of the <see cref="HttpClient"/> (no automatic redirects, redacted header logging).</summary>
    public const string HttpClientName = "ClaudeUsage";

    internal const string RunClaudeHint = "run “claude” to sign in";
    private const int MaxResponseBytes = 512 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _time;
    private readonly ClaudeUsageOptions _options;

    /// <summary>Creates the source.</summary>
    /// <param name="httpClientFactory">HTTP client factory (uses the <see cref="HttpClientName"/> client).</param>
    /// <param name="time">Time provider.</param>
    /// <param name="options">Endpoint and credential locations.</param>
    public ClaudeUsageSource(IHttpClientFactory httpClientFactory, TimeProvider time, ClaudeUsageOptions? options = null)
    {
        _httpClientFactory = httpClientFactory;
        _time = time;
        _options = options ?? new ClaudeUsageOptions();
    }

    /// <inheritdoc />
    public async Task<ClaudeUsageResult> GetUsageAsync(CancellationToken cancellationToken)
    {
        ClaudeCredentials? credentials;
        try
        {
            credentials = await ClaudeCredentialReader.ReadAsync(_options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ClaudeUsageResult.SignedOut("The Claude Code sign-in could not be read");
        }

        var now = _time.GetUtcNow();
        if (credentials is null)
        {
            return new ClaudeUsageResult.SignedOut($"Not signed in to Claude Code – {RunClaudeHint}");
        }

        if (credentials.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            // Claude Code refreshes the token when it runs; GitWidget never does.
            return new ClaudeUsageResult.SignedOut($"Claude Code sign-in expired – {RunClaudeHint}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.Token.Reveal()}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", ProviderBoundary.DefaultUserAgent);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new ClaudeUsageResult.SignedOut($"Claude Code sign-in was rejected – {RunClaudeHint}");
                case HttpStatusCode.TooManyRequests:
                    return new ClaudeUsageResult.RateLimited(RetryAfter(response, now));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ClaudeUsageResult.Unavailable($"Claude usage is unavailable (HTTP {(int)response.StatusCode})");
            }

            var body = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            if (body is null)
            {
                return new ClaudeUsageResult.Unavailable("Claude usage is unavailable (answer too large)");
            }

            using var document = JsonDocument.Parse(body);
            return new ClaudeUsageResult.Success(new ClaudeUsageSnapshot(ParseLimits(document.RootElement), credentials.Plan, now));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ClaudeUsageResult.Unavailable("Claude usage is unavailable (timed out)");
        }
        catch (HttpRequestException)
        {
            return new ClaudeUsageResult.Unavailable("Claude usage is unavailable (no connection)");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ClaudeUsageResult.Unavailable("Claude usage is unavailable (unexpected answer)");
        }
    }

    /// <summary>Extracts the limits from the usage answer.</summary>
    /// <param name="root">Root of the answer.</param>
    /// <returns>Session, weekly and model limits, as far as reported.</returns>
    internal static IReadOnlyList<ClaudeUsageLimit> ParseLimits(JsonElement root)
    {
        var limits = new List<ClaudeUsageLimit>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return limits;
        }

        AddWindow(limits, root, "five_hour", ClaudeUsageLimitKind.Session, null);
        AddWindow(limits, root, "seven_day", ClaudeUsageLimitKind.Weekly, null);

        var entries = root.TryGetProperty("limits", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
            : [];
        if (limits.TrueForAll(l => l.Kind != ClaudeUsageLimitKind.Weekly)
            && entries.FirstOrDefault(e => Text(e, "kind") == "weekly_all") is { ValueKind: JsonValueKind.Object } all
            && Number(all, "percent") is { } allPercent)
        {
            limits.Add(new ClaudeUsageLimit(ClaudeUsageLimitKind.Weekly, null, allPercent, Instant(all, "resets_at")));
        }

        foreach (var entry in entries.Where(e => Text(e, "kind") == "weekly_scoped"))
        {
            var model = entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                && scope.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.Object
                    ? Text(modelElement, "display_name")
                    : null;
            if (Number(entry, "percent") is { } percent && !string.IsNullOrWhiteSpace(model))
            {
                limits.Add(new ClaudeUsageLimit(ClaudeUsageLimitKind.WeeklyModel, model.Trim(), percent, Instant(entry, "resets_at")));
            }
        }

        if (limits.TrueForAll(l => l.Kind != ClaudeUsageLimitKind.WeeklyModel))
        {
            // Older shape: one window per model family.
            AddWindow(limits, root, "seven_day_opus", ClaudeUsageLimitKind.WeeklyModel, "Opus");
            AddWindow(limits, root, "seven_day_sonnet", ClaudeUsageLimitKind.WeeklyModel, "Sonnet");
        }

        return limits;
    }

    private static void AddWindow(List<ClaudeUsageLimit> limits, JsonElement root, string name, ClaudeUsageLimitKind kind, string? model)
    {
        if (root.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object && Number(window, "utilization") is { } percent)
        {
            limits.Add(new ClaudeUsageLimit(kind, model, percent, Instant(window, "resets_at")));
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? Math.Clamp(number, 0, 100)
            : null;

    private static DateTimeOffset? Instant(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) ? instant : null;

    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - now : null);
        return wait is { } value && value > TimeSpan.Zero ? value : null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }

        return buffer.ToArray();
    }
}
