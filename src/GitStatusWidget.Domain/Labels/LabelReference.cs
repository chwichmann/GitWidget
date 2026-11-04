namespace GitStatusWidget.Domain.Labels;

/// <summary>
/// Label attached to an issue. Labels are identified by name, which is unique per repository.
/// </summary>
/// <param name="Name">Label name.</param>
/// <param name="Color">Color as <c>#RRGGBB</c>, if known.</param>
public sealed record LabelReference(string Name, string? Color);
