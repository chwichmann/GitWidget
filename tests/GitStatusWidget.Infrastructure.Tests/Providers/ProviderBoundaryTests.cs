using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Infrastructure.Providers;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Tests.Providers;

public sealed class ProviderBoundaryTests
{
    [Fact]
    public async Task Gateway_host_reaches_the_failure_result()
    {
        var result = await ProviderBoundary.RunAsync(
            () => throw new ProviderFailureException(ProviderFailureKind.GatewayInterception, "Redirected") { GatewayHost = "portal.example.net" },
            () => null,
            CancellationToken.None);

        result.Should().BeOfType<ProviderFetchResult.Failure>().Which.GatewayHost.Should().Be("portal.example.net");
    }

    [Fact]
    public async Task Gateway_host_reaches_the_connection_result()
    {
        var result = await ProviderBoundary.RunConnectionAsync(
            () => throw new ProviderFailureException(ProviderFailureKind.GatewayInterception, "Redirected") { GatewayHost = "portal.example.net" },
            CancellationToken.None);

        result.Failure.Should().Be(ProviderFailureKind.GatewayInterception);
        result.GatewayHost.Should().Be("portal.example.net");
    }
}
