using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Infrastructure.Providers.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Updates;

/// <summary>Registers the release feed used by the update check (issue #7).</summary>
public static class UpdateServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IReleaseFeed"/> reading GitWidget releases from GitHub. Requires the GitHub provider
    /// (named HTTP client), an <see cref="ICredentialStore"/> and an <see cref="ISynchronizationController"/>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="options">Feed options; defaults to <c>chwichmann/GitWidget</c> on github.com.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddReleaseFeed(this IServiceCollection services, ReleaseFeedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddGitHubProvider();
        services.TryAddSingleton<IReleaseFeed>(sp => new GitHubReleaseFeed(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<ISynchronizationController>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<GitHubReleaseFeed>>(),
            options));
        return services;
    }

    /// <summary>
    /// Registers <see cref="IUpdateInstaller"/> for in-place updates (issue #19) and its named
    /// <see cref="HttpClient"/> (no automatic redirects, redacted header logging).
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="options">Locations; defaults to the folder GitWidget runs from.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddUpdateInstaller(this IServiceCollection services, UpdateInstallerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient(UpdateInstaller.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(Providers.Http.ProviderHttpClient.CreateHandler)
            .RedactLoggedHeaders(static _ => true);
        services.TryAddSingleton<IUpdateInstaller>(sp => new UpdateInstaller(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<UpdateInstaller>>(),
            options));
        return services;
    }

    /// <summary>
    /// Called first thing at startup: when this instance was started by an update, waits for the previous version to
    /// exit (it still owns the single-instance lock), then removes the backups an update left behind.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="installDirectory">Folder GitWidget runs from.</param>
    public static void CompletePendingUpdate(IReadOnlyList<string> args, string installDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = args.ToList().IndexOf(UpdateInstaller.WaitForExitArgument);
        if (index >= 0 && index + 1 < args.Count && int.TryParse(args[index + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var processId))
        {
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(processId);
                previous.WaitForExit(TimeSpan.FromSeconds(20));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        UpdateInstaller.DeleteLeftovers(installDirectory);
    }
}
