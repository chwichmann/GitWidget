using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Infrastructure.Providers.GitLab;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Extension methods live in the DI namespace by convention so they are found without an extra using.
#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

/// <summary>
/// Registers the GitLab provider.
/// </summary>
public static class GitLabServiceCollectionExtensions
{
    /// <summary>
    /// Registers the named <see cref="HttpClient"/> <c>"GitLab"</c> (no automatic redirects, see architecture §9.4)
    /// and the GitLab <see cref="IRepositoryStatusProvider"/> as a singleton. Requires an
    /// <see cref="ICredentialStore"/>; uses <see cref="TimeProvider.System"/> and a null logger when none are registered.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddGitLabProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient(GitLabStatusProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(ProviderHttpClient.CreateHandler);
        services.AddSingleton<IRepositoryStatusProvider>(sp => new GitLabStatusProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILoggerFactory>()?.CreateLogger<GitLabStatusProvider>() ?? NullLogger<GitLabStatusProvider>.Instance));
        return services;
    }
}
