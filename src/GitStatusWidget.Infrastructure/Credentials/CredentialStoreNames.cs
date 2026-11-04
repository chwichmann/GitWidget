namespace GitStatusWidget.Infrastructure.Credentials;

/// <summary>
/// Names under which the native stores keep GitStatusWidget secrets. Debug builds use a separate
/// namespace so development runs (for example under the shared <c>dotnet</c> host) never touch
/// production tokens.
/// </summary>
internal static class CredentialStoreNames
{
#if DEBUG
    /// <summary>macOS Keychain <c>kSecAttrService</c> and Windows target-name prefix.</summary>
    public const string Service = "GitStatusWidget.Debug";

    /// <summary>libsecret schema name.</summary>
    public const string SecretSchema = "io.github.chwichmann.GitStatusWidget.Debug.Token";
#else
    /// <summary>macOS Keychain <c>kSecAttrService</c> and Windows target-name prefix.</summary>
    public const string Service = "GitStatusWidget";

    /// <summary>libsecret schema name.</summary>
    public const string SecretSchema = "io.github.chwichmann.GitStatusWidget.Token";
#endif

    /// <summary>Maximum accepted key length (keys are short references such as <c>GitStatusWidget/{guid}</c>).</summary>
    public const int MaxKeyLength = 512;

    /// <summary>Validates a credential reference before it reaches native code.</summary>
    /// <param name="key">The credential reference.</param>
    public static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > MaxKeyLength)
        {
            throw new ArgumentException($"Credential key exceeds {MaxKeyLength} characters.", nameof(key));
        }

        foreach (char c in key)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException("Credential key contains control characters.", nameof(key));
            }
        }
    }
}
