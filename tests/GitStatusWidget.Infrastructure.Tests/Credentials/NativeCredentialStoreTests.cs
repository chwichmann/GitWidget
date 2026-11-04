using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Infrastructure.Credentials;

namespace GitStatusWidget.Infrastructure.Tests.Credentials;

/// <summary>
/// Round trips against the real OS stores with random <c>GitWidgetTest/</c> keys that are always deleted.
/// Windows runs everywhere it can (Credential Manager never prompts). macOS and Linux are opt-in because
/// a keychain/keyring prompt would block an unattended run:
/// <c>GITWIDGET_KEYCHAIN_TESTS=1</c> and <c>GITWIDGET_SECRET_SERVICE_TESTS=1</c>.
/// </summary>
public sealed class NativeCredentialStoreTests
{
    private const int TimeoutMs = 60_000;

    [Fact(Timeout = TimeoutMs)]
    public async Task Windows_credential_manager_round_trip()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
            return;
        }

        var store = new WindowsCredentialStore();
        store.IsAvailable.Should().BeTrue(store.UnavailableReason);

        await CredentialStoreRoundTrip.RunAsync(store);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Windows_rejects_oversized_secret_without_echoing_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
            return;
        }

        var store = new WindowsCredentialStore();
        string huge = "S3cr3t" + new string('x', WindowsCredentialStore.MaxSecretBytes);

        ArgumentException? thrown = null;
        try
        {
            await store.WriteAsync(CredentialStoreRoundTrip.NewTestKey(), new Secret(huge), TestContext.Current.CancellationToken);
        }
        catch (ArgumentException ex)
        {
            thrown = ex;
        }

        thrown.Should().NotBeNull();
        thrown!.Message.Should().Contain("2560").And.NotContain("S3cr3t");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Mac_keychain_round_trip()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }

        Assert.SkipUnless(IsEnabled("GITWIDGET_KEYCHAIN_TESTS"), "Set GITWIDGET_KEYCHAIN_TESTS=1 to run (may show a keychain prompt).");

        await CredentialStoreRoundTrip.RunAsync(new MacKeychainCredentialStore());
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Linux_secret_service_round_trip()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
            return;
        }

        Assert.SkipUnless(IsEnabled("GITWIDGET_SECRET_SERVICE_TESTS"), "Set GITWIDGET_SECRET_SERVICE_TESTS=1 to run (needs an unlocked keyring).");

        var store = new LinuxSecretServiceCredentialStore();
        store.IsAvailable.Should().BeTrue(store.UnavailableReason);

        await CredentialStoreRoundTrip.RunAsync(store);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Linux_availability_probe_never_throws()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
            return;
        }

        var store = new LinuxSecretServiceCredentialStore();

        bool available = store.IsAvailable; // Bounded by the probe timeout.
        if (!available)
        {
            store.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        }

        await Task.CompletedTask;
    }

    private static bool IsEnabled(string variable) => Environment.GetEnvironmentVariable(variable) == "1";
}
