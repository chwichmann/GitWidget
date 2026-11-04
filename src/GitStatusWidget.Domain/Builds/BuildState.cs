namespace GitStatusWidget.Domain.Builds;

/// <summary>
/// Provider-neutral state of a CI pipeline (GitLab) or workflow run (GitHub Actions).
/// </summary>
public enum BuildState
{
    /// <summary>State not recognized.</summary>
    Unknown = 0,

    /// <summary>Waiting to start (queued, pending, waiting for resources, manual, scheduled).</summary>
    Queued = 1,

    /// <summary>Currently executing.</summary>
    Running = 2,

    /// <summary>Completed successfully.</summary>
    Succeeded = 3,

    /// <summary>Completed with failure.</summary>
    Failed = 4,

    /// <summary>Canceled.</summary>
    Canceled = 5,

    /// <summary>Skipped / neutral.</summary>
    Skipped = 6,
}
