namespace GitStatusWidget.Infrastructure.Credentials;

/// <summary>A native store that probes its availability and can explain a failure.</summary>
internal interface IProbedCredentialStore
{
    /// <summary>Why the store is unavailable, or <see langword="null"/> when it is usable.</summary>
    string? UnavailableReason { get; }
}
