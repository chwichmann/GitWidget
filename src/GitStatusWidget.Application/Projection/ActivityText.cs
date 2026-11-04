using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Application.Projection;

/// <summary>An entry of the activity feed.</summary>
/// <param name="RepositoryId">Configuration id.</param>
/// <param name="RepositoryName">Repository display name.</param>
/// <param name="Event">Underlying event.</param>
/// <param name="Headline">Short description, e.g. "Issue #12 closed".</param>
/// <param name="Title">Entity title (plain text).</param>
public sealed record ActivityProjection(Guid RepositoryId, string RepositoryName, RepositoryChangeEvent Event, string Headline, string Title);

/// <summary>
/// Provider-neutral wording of events. Provider terms appear only as presentation labels
/// ("pull request" / "merge request", "workflow run" / "pipeline").
/// </summary>
public static class ActivityText
{
    /// <summary>Creates the activity entry for an event.</summary>
    /// <param name="repositoryId">Configuration id.</param>
    /// <param name="repositoryName">Display name.</param>
    /// <param name="e">Event.</param>
    /// <returns>The activity entry.</returns>
    public static ActivityProjection Describe(Guid repositoryId, string repositoryName, RepositoryChangeEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new ActivityProjection(repositoryId, repositoryName, e, Headline(e), e.Title);
    }

    /// <summary>Short headline of an event.</summary>
    /// <param name="e">Event.</param>
    /// <returns>Headline text.</returns>
    public static string Headline(RepositoryChangeEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var issue = e.EntityNumber is { } n ? $"Issue #{n}" : "Issue";
        var changeRequest = e.Repository.Provider == RepositoryProviderKind.GitLab ? "Merge request" : "Pull request";
        var cr = e.EntityNumber is { } m ? $"{changeRequest} {(e.Repository.Provider == RepositoryProviderKind.GitLab ? "!" : "#")}{m}" : changeRequest;
        var build = e.Repository.Provider == RepositoryProviderKind.GitLab ? "Pipeline" : "Workflow run";
        var on = string.IsNullOrEmpty(e.Detail) ? string.Empty : $" on {e.Detail}";
        return e.Type switch
        {
            ChangeEventType.IssueCreated => $"{issue} opened",
            ChangeEventType.IssueClosed => $"{issue} closed",
            ChangeEventType.IssueReopened => $"{issue} reopened",
            ChangeEventType.IssueTitleChanged => $"{issue} renamed",
            ChangeEventType.IssueLabelsChanged => string.IsNullOrEmpty(e.Detail) ? $"{issue}: labels removed" : $"{issue} labeled {e.Detail}",
            ChangeEventType.IssueMilestoneChanged => string.IsNullOrEmpty(e.Detail) ? $"{issue} removed from milestone" : $"{issue} moved to {e.Detail}",
            ChangeEventType.IssueAssigneesChanged => string.IsNullOrEmpty(e.Detail) ? $"{issue} unassigned" : $"{issue} assigned to {e.Detail}",
            ChangeEventType.MilestoneCreated => "Milestone created",
            ChangeEventType.MilestoneClosed => "Milestone closed",
            ChangeEventType.MilestoneReopened => "Milestone reopened",
            ChangeEventType.MilestoneProgressChanged => $"Milestone progress {e.Detail}",
            ChangeEventType.ChangeRequestOpened => $"{cr} opened",
            ChangeEventType.ChangeRequestClosed => $"{cr} closed",
            ChangeEventType.ChangeRequestMerged => $"{cr} merged",
            ChangeEventType.BuildStarted => $"{build} started{on}",
            ChangeEventType.BuildSucceeded => $"{build} succeeded{on}",
            ChangeEventType.BuildFailed => $"{build} failed{on}",
            ChangeEventType.BuildCanceled => $"{build} canceled{on}",
            _ => e.Type.ToString(),
        };
    }
}
