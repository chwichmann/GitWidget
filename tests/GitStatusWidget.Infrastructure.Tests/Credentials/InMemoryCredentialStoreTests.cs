using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Infrastructure.Credentials;

namespace GitStatusWidget.Infrastructure.Tests.Credentials;

public sealed class InMemoryCredentialStoreTests
{
    [Fact]
    public async Task Round_trip_overwrite_and_delete()
    {
        var store = new InMemoryCredentialStore();

        await CredentialStoreRoundTrip.RunAsync(store);
    }

    [Fact]
    public void Store_name_says_tokens_are_session_only()
    {
        var store = new InMemoryCredentialStore();

        store.IsAvailable.Should().BeTrue();
        store.StoreName.Should().Contain("Session only");
    }

    [Fact]
    public async Task Rejects_invalid_keys()
    {
        var store = new InMemoryCredentialStore();

        await FluentActions.Awaiting(() => store.ReadAsync("", TestContext.Current.CancellationToken)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => store.WriteAsync("a\nb", new Secret("x"), TestContext.Current.CancellationToken)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        var store = new InMemoryCredentialStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => store.ReadAsync("k", cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }
}
