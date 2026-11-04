namespace GitStatusWidget.Domain.Repositories;

/// <summary>
/// Stable identity of a remote repository.
/// </summary>
/// <param name="Provider">Hosting provider.</param>
/// <param name="Host">Host name of the provider instance, e.g. <c>github.com</c>.</param>
/// <param name="OwnerPath">Owner or namespace path, e.g. <c>octocat</c> or <c>group/subgroup</c>.</param>
/// <param name="RepositoryName">Repository or project name (last path segment).</param>
/// <param name="ExternalId">Provider's stable repository id (GitHub node id, GitLab project id).</param>
public sealed record RepositoryIdentity(
    RepositoryProviderKind Provider,
    string Host,
    string OwnerPath,
    string RepositoryName,
    string ExternalId)
{
    /// <summary>Full path as shown to the user, e.g. <c>group/subgroup/project</c>.</summary>
    public string FullPath => string.IsNullOrEmpty(OwnerPath) ? RepositoryName : $"{OwnerPath}/{RepositoryName}";
}
