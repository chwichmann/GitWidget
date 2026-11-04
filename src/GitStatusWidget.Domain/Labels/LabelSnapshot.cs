namespace GitStatusWidget.Domain.Labels;

/// <summary>
/// Repository label with optional issue counts.
/// </summary>
/// <param name="Name">Label name (unique per repository).</param>
/// <param name="Color">Color as <c>#RRGGBB</c>.</param>
/// <param name="Description">Description, if any.</param>
/// <param name="OpenIssueCount">Open issues carrying the label, if the provider supplies counts.</param>
/// <param name="ClosedIssueCount">Closed issues carrying the label, if the provider supplies counts.</param>
public sealed record LabelSnapshot(
    string Name,
    string Color,
    string? Description,
    int? OpenIssueCount = null,
    int? ClosedIssueCount = null);
