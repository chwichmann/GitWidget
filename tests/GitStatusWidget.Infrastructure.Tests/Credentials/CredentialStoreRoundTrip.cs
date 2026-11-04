using GitStatusWidget.Application.Credentials;

namespace GitStatusWidget.Infrastructure.Tests.Credentials;

/// <summary>Shared write/read/overwrite/delete/missing scenario with a random key that is always cleaned up.</summary>
internal static class CredentialStoreRoundTrip
{
    public static string NewTestKey() => $"GitWidgetTest/{Guid.NewGuid():N}";

    public static async Task RunAsync(ICredentialStore store)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string key = NewTestKey();
        try
        {
            (await store.ReadAsync(key, ct)).Should().BeNull("the key was never written");

            await store.WriteAsync(key, new Secret("glpat-first-value"), ct);
            (await store.ReadAsync(key, ct))!.Reveal().Should().Be("glpat-first-value");

            await store.WriteAsync(key, new Secret("ghp_second-välue-✓"), ct);
            (await store.ReadAsync(key, ct))!.Reveal().Should().Be("ghp_second-välue-✓", "write overwrites, including non-ASCII");

            await store.DeleteAsync(key, ct);
            (await store.ReadAsync(key, ct)).Should().BeNull("the entry was deleted");

            await store.DeleteAsync(key, ct); // Deleting a missing entry is not an error.
        }
        finally
        {
            try
            {
                await store.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception)
            {
                // Best-effort cleanup; the assertion failure above is the interesting one.
            }
        }
    }
}
