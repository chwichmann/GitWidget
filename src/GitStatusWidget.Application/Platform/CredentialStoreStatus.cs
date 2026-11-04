namespace GitStatusWidget.Application.Platform;

/// <summary>
/// Describes the credential store selected at startup so the settings UI can warn when tokens
/// are kept for the current session only.
/// </summary>
/// <param name="StoreName">Human-readable name of the active store.</param>
/// <param name="IsPersistent">
/// <see langword="false"/> when no OS credential store is usable and tokens are kept in memory
/// until the application exits.
/// </param>
/// <param name="FallbackReason">Why the OS store could not be used; <see langword="null"/> when it is in use.</param>
public sealed record CredentialStoreStatus(string StoreName, bool IsPersistent, string? FallbackReason);
