namespace GitStatusWidget.Domain.Milestones;

/// <summary>
/// Reference from an issue to its milestone.
/// </summary>
/// <param name="ExternalId">Provider milestone id.</param>
/// <param name="Title">Milestone title.</param>
public sealed record MilestoneReference(string ExternalId, string Title);
