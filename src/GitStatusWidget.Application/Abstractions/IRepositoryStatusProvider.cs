using GitStatusWidget.Application.Settings;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Application.Abstractions;

/// <summary>
/// Reads repository state from one hosting provider and maps it into canonical domain models.
/// Implementations are read-only and must never mutate remote state.
/// </summary>
public interface IRepositoryStatusProvider
{
    /// <summary>The provider this implementation serves.</summary>
    RepositoryProviderKind Kind { get; }

    /// <summary>
    /// Validates server, credential and repository access and resolves the repository identity.
    /// </summary>
    /// <param name="configuration">Repository configuration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The connection result.</returns>
    Task<ProviderConnectionResult> TestConnectionAsync(RepositoryConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the sections requested by <paramref name="context"/>. Sections that were not requested,
    /// or did not change, are reported as unchanged. Any failure of a requested section fails the whole
    /// fetch (no partial results).
    /// </summary>
    /// <param name="configuration">Repository configuration.</param>
    /// <param name="context">Previous snapshot, provider sync state and requested scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Success with section updates, or a classified failure.</returns>
    Task<ProviderFetchResult> FetchAsync(RepositoryConfiguration configuration, SnapshotRequestContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the provider implementation for a provider kind.
/// </summary>
public interface IRepositoryStatusProviderResolver
{
    /// <summary>Returns the provider for <paramref name="provider"/>.</summary>
    /// <param name="provider">Provider kind.</param>
    /// <returns>The provider implementation.</returns>
    /// <exception cref="NotSupportedException">No implementation is registered.</exception>
    IRepositoryStatusProvider Resolve(RepositoryProviderKind provider);
}

/// <summary>
/// Default resolver over all registered providers.
/// </summary>
/// <param name="providers">Registered providers.</param>
public sealed class RepositoryStatusProviderResolver(IEnumerable<IRepositoryStatusProvider> providers) : IRepositoryStatusProviderResolver
{
    private readonly Dictionary<RepositoryProviderKind, IRepositoryStatusProvider> _providers = providers.ToDictionary(p => p.Kind);

    /// <inheritdoc />
    public IRepositoryStatusProvider Resolve(RepositoryProviderKind provider) =>
        _providers.TryGetValue(provider, out var implementation)
            ? implementation
            : throw new NotSupportedException($"No provider registered for {provider}.");
}
