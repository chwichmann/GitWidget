namespace GitStatusWidget.Infrastructure.Credentials;

/// <summary>
/// A native credential store call failed. The message names the operation and the native error
/// code only; it never contains the secret value.
/// </summary>
public sealed class CredentialStoreException : Exception
{
    /// <summary>Creates an exception without a native error code.</summary>
    public CredentialStoreException()
    {
    }

    /// <summary>Creates an exception without a native error code.</summary>
    /// <param name="message">Description of the failure (must not contain secrets).</param>
    public CredentialStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception wrapping another one.</summary>
    /// <param name="message">Description of the failure (must not contain secrets).</param>
    /// <param name="innerException">The underlying exception.</param>
    public CredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception for a native error code.</summary>
    /// <param name="message">Description of the failure (must not contain secrets).</param>
    /// <param name="errorCode">Win32 error, OSStatus or GError code.</param>
    public CredentialStoreException(string message, int errorCode)
        : base($"{message} (code {errorCode})")
    {
        ErrorCode = errorCode;
    }

    /// <summary>Native error code: Win32 error (Windows), OSStatus (macOS) or GError code (Linux); 0 when unknown.</summary>
    public int ErrorCode { get; }
}
