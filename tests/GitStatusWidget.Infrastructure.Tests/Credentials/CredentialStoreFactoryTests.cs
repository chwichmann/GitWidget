using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Infrastructure.Credentials;

namespace GitStatusWidget.Infrastructure.Tests.Credentials;

public sealed class CredentialStoreFactoryTests
{
    [Fact]
    public void Uses_native_store_when_available()
    {
        var native = new FakeStore(available: true);

        var selection = CredentialStoreFactory.Select(native);

        selection.Store.Should().BeSameAs(native);
        selection.Status.IsPersistent.Should().BeTrue();
        selection.Status.StoreName.Should().Be("Fake OS store");
        selection.Status.FallbackReason.Should().BeNull();
    }

    [Fact]
    public void Falls_back_visibly_when_native_store_is_unavailable()
    {
        var native = new FakeStore(available: false, reason: "No keyring daemon.");

        var selection = CredentialStoreFactory.Select(native);

        selection.Store.Should().BeOfType<InMemoryCredentialStore>();
        selection.Store.IsAvailable.Should().BeTrue("the fallback is usable for this session");
        selection.Status.IsPersistent.Should().BeFalse();
        selection.Status.StoreName.Should().Contain("Session only");
        selection.Status.FallbackReason.Should().Be("No keyring daemon.");
    }

    [Fact]
    public void Falls_back_when_probe_throws()
    {
        var native = new FakeStore(available: true) { ThrowOnProbe = true };

        var selection = CredentialStoreFactory.Select(native);

        selection.Store.Should().BeOfType<InMemoryCredentialStore>();
        selection.Status.FallbackReason.Should().Contain("Fake OS store");
    }

    [Fact]
    public void Falls_back_when_os_has_no_store()
    {
        var selection = CredentialStoreFactory.Select(null);

        selection.Status.IsPersistent.Should().BeFalse();
        selection.Status.FallbackReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Default_selection_matches_current_os_without_touching_secrets()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Skip("The Linux default probes D-Bus; covered by GITWIDGET_SECRET_SERVICE_TESTS.");
        }

        var selection = CredentialStoreFactory.CreateDefault();

        if (OperatingSystem.IsWindows())
        {
            selection.Store.Should().BeOfType<WindowsCredentialStore>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            selection.Store.Should().BeOfType<MacKeychainCredentialStore>();
            selection.Status.IsPersistent.Should().BeTrue();
        }
        else
        {
            selection.Store.Should().BeOfType<InMemoryCredentialStore>();
        }
    }

    private sealed class FakeStore(bool available, string? reason = null) : ICredentialStore, IProbedCredentialStore
    {
        public bool ThrowOnProbe { get; init; }

        public bool IsAvailable => ThrowOnProbe ? throw new DllNotFoundException("libsecret-1.so.0") : available;

        public string? UnavailableReason => ThrowOnProbe ? throw new DllNotFoundException("libsecret-1.so.0") : reason;

        public string StoreName => "Fake OS store";

        public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken) => Task.FromResult<Secret?>(null);

        public Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
