using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Infrastructure.Providers.GitHub;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitHub;

/// <summary>
/// Read-only smoke test against the real GitHub API. Runs only when <c>GITWIDGET_GITHUB_TOKEN</c> and
/// <c>GITWIDGET_GITHUB_REPO</c> (<c>owner/name</c>) are set; otherwise it is skipped. The token is never printed.
/// </summary>
public sealed class GitHubLiveSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_repository_fetches_and_assembles()
    {
        var token = Environment.GetEnvironmentVariable("GITWIDGET_GITHUB_TOKEN");
        var repository = Environment.GetEnvironmentVariable("GITWIDGET_GITHUB_REPO");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(repository), "Set GITWIDGET_GITHUB_TOKEN and GITWIDGET_GITHUB_REPO to run.");
        var parts = repository!.Split('/', 2);

        var id = Guid.NewGuid();
        var configuration = new RepositoryConfiguration(
            id,
            RepositoryProviderKind.GitHub,
            new Uri("https://github.com/"),
            parts[0],
            parts[1],
            RepositoryConfiguration.DefaultCredentialReference(id),
            RepositoryConfiguration.DefaultIssueInterval,
            RepositoryConfiguration.DefaultBuildInterval,
            RepositoryConfiguration.DefaultMetadataInterval,
            RepositoryConfiguration.DefaultReconciliationInterval,
            IsEnabled: true);
        var credentials = new InMemoryCredentialStore();
        await credentials.WriteAsync(configuration.CredentialReference, new Secret(token!), CancellationToken.None);
        var logger = new CapturingLogger<GitHubStatusProvider>();
        using var factory = new TestHttpClientFactory();
        var provider = new GitHubStatusProvider(factory, credentials, TimeProvider.System, logger);
        var cancellation = TestContext.Current.CancellationToken;

        var connection = await provider.TestConnectionAsync(configuration, cancellation);
        output.WriteLine($"Connection: {connection.IsSuccess} {connection.Message}");
        connection.IsSuccess.Should().BeTrue(connection.Message);

        var closedWindowStart = DateTimeOffset.UtcNow.AddDays(-14);
        var first = await provider.FetchAsync(configuration, new SnapshotRequestContext(null, ProviderSyncState.Empty, RefreshScope.All, closedWindowStart), cancellation);
        var success = first.Should().BeOfType<ProviderFetchResult.Success>().Which;
        var accepted = SnapshotAssembler.Assemble(null, success.Fetch, DateTimeOffset.UtcNow, closedWindowStart).Should().BeOfType<AssemblyResult.Accepted>().Which;
        var snapshot = accepted.Snapshot;
        output.WriteLine($"Snapshot: {snapshot.Issues.Count} issues ({snapshot.IssueCounts.Open} open / {snapshot.IssueCounts.Closed} closed), {snapshot.ChangeRequests.Count} PRs, {snapshot.Milestones.Count} milestones, {snapshot.Labels.Count} labels, {snapshot.Builds.Count} builds; rate {success.RateLimit}");

        var countedMilestones = success.Fetch.Counts!.ClosedIssuesByMilestone.Keys.Intersect(snapshot.Milestones.Select(m => m.ExternalId)).Count();
        output.WriteLine($"Milestones with GraphQL closed counts matched by node id: {countedMilestones}/{snapshot.Milestones.Count}");
        countedMilestones.Should().Be(snapshot.Milestones.Count, "GraphQL Milestone.id equals REST node_id");

        var second = await provider.FetchAsync(
            configuration,
            new SnapshotRequestContext(snapshot, success.SyncState, RefreshScope.WorkItems | RefreshScope.Builds | RefreshScope.Metadata, closedWindowStart),
            cancellation);
        var secondSuccess = second.Should().BeOfType<ProviderFetchResult.Success>().Which;
        output.WriteLine($"Second fetch: issues {secondSuccess.Fetch.Issues.GetType().Name}, milestones {secondSuccess.Fetch.Milestones.GetType().Name}, builds {secondSuccess.Fetch.Builds.GetType().Name}");
        SnapshotAssembler.Assemble(snapshot, secondSuccess.Fetch, DateTimeOffset.UtcNow, closedWindowStart).Should().BeOfType<AssemblyResult.Accepted>();

        logger.Entries.Should().NotContain(e => e.Contains(token!, StringComparison.Ordinal));
        JsonSerializer.Serialize(secondSuccess.SyncState).Should().NotContain(token!);
    }
}
