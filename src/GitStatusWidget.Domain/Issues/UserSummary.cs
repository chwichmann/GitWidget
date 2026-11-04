namespace GitStatusWidget.Domain.Issues;

/// <summary>
/// Minimal, non-sensitive user information (no e-mail addresses).
/// </summary>
/// <param name="ExternalId">Provider user id.</param>
/// <param name="Login">User name / login handle.</param>
/// <param name="DisplayName">Display name, if the provider returns one.</param>
public sealed record UserSummary(string ExternalId, string Login, string? DisplayName);
