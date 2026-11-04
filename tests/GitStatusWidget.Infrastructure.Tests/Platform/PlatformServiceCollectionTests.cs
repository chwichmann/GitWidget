using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Infrastructure.Credentials;
using GitStatusWidget.Infrastructure.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace GitStatusWidget.Infrastructure.Tests.Platform;

public sealed class PlatformServiceCollectionTests
{
    [Fact]
    public void Registers_platform_services_and_keeps_overrides()
    {
        var overrideStore = new InMemoryCredentialStore("Test store");
        var services = new ServiceCollection();
        services.AddSingleton(new CredentialStoreSelection(overrideStore, new CredentialStoreStatus("Test store", false, "test")));

        services.AddPlatformServices();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICredentialStore>().Should().BeSameAs(overrideStore);
        provider.GetRequiredService<CredentialStoreStatus>().IsPersistent.Should().BeFalse();
        provider.GetRequiredService<IAutostartService>().Should().NotBeNull();
        provider.GetRequiredService<IReducedMotionDetector>().Should().NotBeNull();
        provider.GetRequiredService<IUrlLauncher>().Should().NotBeNull();
    }
}
