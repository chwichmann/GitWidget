namespace GitStatusWidget.Domain.Repositories;

/// <summary>
/// Identifies the hosting provider of a repository.
/// </summary>
public enum RepositoryProviderKind
{
    /// <summary>A GitLab project (GitLab.com or self-managed).</summary>
    GitLab = 1,

    /// <summary>A GitHub repository (GitHub.com, Enterprise Server later).</summary>
    GitHub = 2,
}
