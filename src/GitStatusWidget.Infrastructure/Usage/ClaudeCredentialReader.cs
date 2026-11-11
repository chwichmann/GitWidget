using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using GitStatusWidget.Application.Credentials;

namespace GitStatusWidget.Infrastructure.Usage;

/// <summary>Where the Claude usage and the Claude Code sign-in are read from (overridable for tests).</summary>
public sealed class ClaudeUsageOptions
{
    /// <summary>Usage endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.anthropic.com/api/oauth/usage");

    /// <summary>
    /// Credentials file of Claude Code; <see langword="null"/> for the default <c>.credentials.json</c> in
    /// <c>CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>.
    /// </summary>
    public string? CredentialsFile { get; init; }

    /// <summary>Reads an environment variable.</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>On macOS, fall back to the keychain item of Claude Code when there is no credentials file.</summary>
    public bool UseMacKeychain { get; init; } = true;

    /// <summary>Timeout of one usage request.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>The sign-in of Claude Code.</summary>
/// <param name="Token">OAuth access token.</param>
/// <param name="Plan">Subscription type, if stored.</param>
/// <param name="ExpiresAt">Expiry of the access token, if stored.</param>
internal sealed record ClaudeCredentials(Secret Token, string? Plan, DateTimeOffset? ExpiresAt);

/// <summary>
/// Reads the existing Claude Code sign-in: the credentials file, on macOS the keychain item of Claude Code, then the
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c> environment variable. Strictly read-only: the token is never refreshed and nothing
/// is written; Claude Code keeps the sign-in current.
/// </summary>
internal static class ClaudeCredentialReader
{
    private const string TokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";
    private const string ConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";
    private const string KeychainService = "Claude Code-credentials";
    private const int MaxCredentialBytes = 1024 * 1024;

    /// <summary>Reads the sign-in.</summary>
    /// <returns>The sign-in, or <see langword="null"/> when Claude Code is not signed in.</returns>
    /// <exception cref="IOException">The credentials file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The credentials file is not accessible.</exception>
    /// <exception cref="JsonException">The credentials are not valid JSON.</exception>
    public static async Task<ClaudeCredentials?> ReadAsync(ClaudeUsageOptions options, CancellationToken cancellationToken)
    {
        var path = CredentialsPath(options);
        string? json = null;
        if (File.Exists(path) && new FileInfo(path).Length <= MaxCredentialBytes)
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        else if (options.UseMacKeychain && options.CredentialsFile is null && OperatingSystem.IsMacOS())
        {
            json = await ReadKeychainAsync(cancellationToken).ConfigureAwait(false);
        }

        if (json is not null && Parse(json) is { } credentials)
        {
            return credentials;
        }

        var token = options.Environment(TokenVariable)?.Trim();
        return string.IsNullOrEmpty(token) ? null : new ClaudeCredentials(new Secret(token), null, null);
    }

    /// <summary>Path of the credentials file.</summary>
    public static string CredentialsPath(ClaudeUsageOptions options)
    {
        if (options.CredentialsFile is { } file)
        {
            return file;
        }

        var directory = options.Environment(ConfigDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".claude");
        }

        return Path.Combine(directory, ".credentials.json");
    }

    /// <summary>Extracts <c>claudeAiOauth</c> from the credentials JSON.</summary>
    /// <returns>The sign-in, or <see langword="null"/> when the JSON holds no access token.</returns>
    public static ClaudeCredentials? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
            || oauth.ValueKind != JsonValueKind.Object
            || !oauth.TryGetProperty("accessToken", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || tokenElement.GetString()?.Trim() is not { Length: > 0 } token)
        {
            return null;
        }

        var plan = oauth.TryGetProperty("subscriptionType", out var planElement) && planElement.ValueKind == JsonValueKind.String
            ? planElement.GetString()
            : null;
        DateTimeOffset? expiresAt = oauth.TryGetProperty("expiresAt", out var expiresElement)
            && expiresElement.ValueKind == JsonValueKind.Number
            && expiresElement.TryGetInt64(out var milliseconds)
            && milliseconds > 0
            && milliseconds < 253_402_300_800_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;
        return new ClaudeCredentials(new Secret(token), string.IsNullOrWhiteSpace(plan) ? null : plan, expiresAt);
    }

    [SupportedOSPlatform("macos")]
    private static async Task<string?> ReadKeychainAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("find-generic-password");
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add(KeychainService);
        start.ArgumentList.Add("-w");
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
