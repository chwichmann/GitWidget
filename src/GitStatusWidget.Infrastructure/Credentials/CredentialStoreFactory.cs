using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Platform;

namespace GitStatusWidget.Infrastructure.Credentials;

/// <summary>The credential store chosen for this process and a description for the settings UI.</summary>
/// <param name="Store">The store to register as <see cref="ICredentialStore"/>.</param>
/// <param name="Status">Whether tokens persist, and why not.</param>
public sealed record CredentialStoreSelection(ICredentialStore Store, CredentialStoreStatus Status);

/// <summary>
/// Chooses the OS credential store (Windows Credential Manager, macOS Keychain, Linux Secret Service)
/// and falls back to a visible session-only <see cref="InMemoryCredentialStore"/> when it is unusable.
/// </summary>
public static class CredentialStoreFactory
{
    /// <summary>
    /// Selects the store for the current OS. Never throws. On Linux the availability probe may take
    /// up to a few seconds (D-Bus round-trip), so call it off the UI thread or during startup.
    /// </summary>
    /// <returns>The selected store and its status.</returns>
    public static CredentialStoreSelection CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return Select(new WindowsCredentialStore());
        }

        if (OperatingSystem.IsMacOS())
        {
            return Select(new MacKeychainCredentialStore());
        }

        if (OperatingSystem.IsLinux())
        {
            return Select(new LinuxSecretServiceCredentialStore());
        }

        return Select(null);
    }

    /// <summary>Selection rule: use the native store when available, otherwise the session-only fallback.</summary>
    /// <param name="native">The native store for this OS, or <see langword="null"/> when there is none.</param>
    /// <returns>The selection.</returns>
    internal static CredentialStoreSelection Select(ICredentialStore? native)
    {
        bool available;
        try
        {
            available = native?.IsAvailable == true;
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            available = false;
        }

        if (native is not null && available)
        {
            return new CredentialStoreSelection(native, new CredentialStoreStatus(native.StoreName, IsPersistent: true, FallbackReason: null));
        }

        string reason = native is null
            ? "This operating system has no supported credential store."
            : SafeReason(native as IProbedCredentialStore) ?? $"{native.StoreName} is not available.";
        var fallback = new InMemoryCredentialStore();
        return new CredentialStoreSelection(fallback, new CredentialStoreStatus(fallback.StoreName, IsPersistent: false, FallbackReason: reason));
    }

    private static string? SafeReason(IProbedCredentialStore? store)
    {
        try
        {
            return store?.UnavailableReason;
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            return null;
        }
    }

    private static bool IsNativeFailure(Exception ex) =>
        ex is CredentialStoreException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException;
}
