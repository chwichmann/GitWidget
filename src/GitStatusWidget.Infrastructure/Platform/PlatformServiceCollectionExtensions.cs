using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Infrastructure.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>Dependency-injection registration of the OS integration services.</summary>
public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICredentialStore"/> (OS store, or a session-only fallback described by
    /// <see cref="CredentialStoreStatus"/>), <see cref="IAutostartService"/>,
    /// <see cref="IReducedMotionDetector"/> and <see cref="IUrlLauncher"/> as singletons. Existing
    /// registrations win, so tests and the composition root can override any of them.
    /// The single-instance coordinator is created in <c>Program.Main</c> before the host; register
    /// that instance as <see cref="ISingleInstanceGuard"/> there.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(_ => CredentialStoreFactory.CreateDefault());
        services.TryAddSingleton(sp => sp.GetRequiredService<CredentialStoreSelection>().Store);
        services.TryAddSingleton(sp => sp.GetRequiredService<CredentialStoreSelection>().Status);
        services.TryAddSingleton(_ => AutostartServiceFactory.CreateDefault());
        services.TryAddSingleton<IReducedMotionDetector>(_ => new ReducedMotionDetector());
        services.TryAddSingleton<IUrlLauncher, UrlLauncher>();
        return services;
    }
}
