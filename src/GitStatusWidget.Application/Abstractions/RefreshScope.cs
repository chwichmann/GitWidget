namespace GitStatusWidget.Application.Abstractions;

/// <summary>
/// Sections requested from a provider in one fetch.
/// </summary>
[Flags]
public enum RefreshScope
{
    /// <summary>Nothing requested.</summary>
    None = 0,

    /// <summary>Changed issues and change requests (delta since the provider's watermark).</summary>
    WorkItems = 1,

    /// <summary>Recent builds.</summary>
    Builds = 2,

    /// <summary>Repository metadata, milestones and labels.</summary>
    Metadata = 4,

    /// <summary>Full reconciliation: complete open sets and recently closed items, plus counts.</summary>
    Reconcile = 8,

    /// <summary>Everything; used for the first snapshot.</summary>
    All = WorkItems | Builds | Metadata | Reconcile,
}
