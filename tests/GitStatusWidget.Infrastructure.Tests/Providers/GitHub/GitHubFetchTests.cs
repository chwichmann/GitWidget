using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using GitStatusWidget.Domain.Snapshots;
using GitStatusWidget.Infrastructure.Providers.GitHub;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitHub;

public sealed class GitHubFetchTests : IDisposable
{
    private const string DeltaRelative = "repos/octo-org/widget/issues?state=all&sort=updated&direction=desc&per_page=100";

    private static readonly DateTimeOffset Now = GitHubFixtures.Now;

    private readonly GitHubProviderHarness _h = new();

    public void Dispose() => _h.Dispose();

    private void StubRichRepository()
    {
        var milestone1 = GitHubFixtures.Milestone(1, "MI_kwTestMs1", "v1.0", dueOn: "2026-12-01T08:00:00Z");
        var milestone2 = GitHubFixtures.Milestone(2, "MI_kwTestMs2", "v0.9", state: "closed");
        _h.Api.StandardRepository(
            open:
            [
                GitHubFixtures.Issue(1, Now.AddHours(-1), milestone: milestone1, labels: [GitHubFixtures.Label("bug", "D73A4A")], assignees: [GitHubFixtures.User("carol")]),
                GitHubFixtures.Issue(2, Now.AddHours(-2), labels: ["plain-string-label"]),
                GitHubFixtures.PullRequest(3, Now.AddMinutes(-30), draft: true),
            ],
            closed:
            [
                GitHubFixtures.Issue(4, Now.AddDays(-1), state: "closed", milestone: milestone1),
                GitHubFixtures.PullRequest(5, Now.AddDays(-2), state: "closed", merged: true),
                GitHubFixtures.PullRequest(6, Now.AddDays(-3), state: "closed"),
            ],
            milestones: [milestone1, milestone2],
            labels: [GitHubFixtures.Label("bug", "D73A4A", "Something is broken"), GitHubFixtures.Label("odd", "zz")],
            runs: GitHubFixtures.Runs(
                GitHubFixtures.Run(41_899_607_319L, "completed", "success"),
                GitHubFixtures.Run(41_899_607_320L, "in_progress", null, name: "Release", created: Now.AddMinutes(-5))),
            counts: GitHubFixtures.Counts(2, 40, [("MI_kwTestMs1", 3), ("MI_kwTestMs2", 5)]));
    }

    private async Task<(RepositorySnapshot Snapshot, ProviderSyncState State)> BaselineAsync()
    {
        StubRichRepository();
        var success = await _h.FetchSuccessAsync();
        var accepted = SnapshotAssembler.Assemble(null, success.Fetch, Now, GitHubFixtures.ClosedWindowStart)
            .Should().BeOfType<AssemblyResult.Accepted>().Which;
        _h.Api.Server.Reset();
        return (accepted.Snapshot, success.SyncState);
    }

    private static string? QueryValue(WireMock.Logging.ILogEntry entry, string key) =>
        entry.RequestMessage?.Query is { } query && query.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;

    private static SnapshotRequestContext Next(RepositorySnapshot previous, ProviderSyncState state, RefreshScope scope) =>
        new(previous, state, scope, GitHubFixtures.ClosedWindowStart);

    [Fact]
    public async Task First_fetch_returns_full_sections_and_is_accepted_by_the_assembler()
    {
        StubRichRepository();

        var success = await _h.FetchSuccessAsync();
        var fetch = success.Fetch;

        fetch.Repository.Should().Be(new RepositoryIdentity(RepositoryProviderKind.GitHub, "localhost", "octo-org", "widget", GitHubFakeServer.RepoNodeId));
        fetch.DefaultBranch.Should().Be("main");
        fetch.Features.Should().Be(RepositoryFeatures.All);

        var issues = fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.FullItems>().Which;
        issues.Open.Select(i => i.Number).Should().BeEquivalentTo([1, 2], "pull requests are never issues (G1)");
        issues.RecentlyClosed.Select(i => i.Number).Should().Equal(4);
        issues.ClosedSince.Should().Be(GitHubFixtures.ClosedWindowStart);

        var first = issues.Open.Single(i => i.Number == 1);
        first.ExternalId.Should().Be("I_kwTest00001");
        first.State.Should().Be(WorkItemState.Open);
        first.WebUrl.Should().Be(new Uri("https://github.com/octo-org/widget/issues/1"));
        first.UpdatedAt.Should().Be(Now.AddHours(-1));
        first.UpdatedAt.Offset.Should().Be(TimeSpan.Zero);
        first.Author.Should().Be(new UserSummary("U_alice", "alice", null));
        first.Assignees.Should().Equal(new UserSummary("U_carol", "carol", null));
        first.Milestone.Should().Be(new MilestoneReference("MI_kwTestMs1", "v1.0"));
        first.Labels.Should().Equal(new LabelReference("bug", "#d73a4a"));
        issues.Open.Single(i => i.Number == 2).Labels.Should().Equal(new LabelReference("plain-string-label", null));
        issues.RecentlyClosed.Single().ClosedAt.Should().Be(Now.AddDays(-1));

        var requests = fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.FullItems>().Which;
        requests.Open.Should().ContainSingle().Which.Should().Match<ChangeRequestSnapshot>(c =>
            c.Number == 3 && c.IsDraft && c.State == ChangeRequestState.Open && c.Kind == ChangeRequestKind.PullRequest
            && c.ExternalId == "PR_kwTest00003" && c.WebUrl == new Uri("https://github.com/octo-org/widget/pull/3"));
        requests.RecentlyClosed.Single(c => c.Number == 5).State.Should().Be(ChangeRequestState.Merged);
        requests.RecentlyClosed.Single(c => c.Number == 6).State.Should().Be(ChangeRequestState.Closed);

        var milestones = fetch.Milestones.Should().BeOfType<SectionUpdate<MilestoneSnapshot>.ReplaceSection>().Which.Items;
        milestones.Should().HaveCount(2);
        milestones[0].Should().Match<MilestoneSnapshot>(m =>
            m.ExternalId == "MI_kwTestMs1" && m.DueDate == new DateOnly(2026, 12, 1) && !m.IsClosed && m.StartDate == null
            && m.OpenIssueCount == 0 && m.ClosedIssueCount == 0);
        milestones[1].IsClosed.Should().BeTrue();

        fetch.Labels.Should().BeOfType<SectionUpdate<LabelSnapshot>.ReplaceSection>().Which.Items.Should().Equal(
            new LabelSnapshot("bug", "#d73a4a", "Something is broken"),
            new LabelSnapshot("odd", GitHubMapper.FallbackLabelColor, null));

        var builds = fetch.Builds.Should().BeOfType<SectionUpdate<BuildSnapshot>.ReplaceSection>().Which.Items;
        builds.Should().HaveCount(2);
        builds[0].Should().Match<BuildSnapshot>(b => b.ExternalId == "41899607319" && b.State == BuildState.Succeeded && b.CompletedAt == b.UpdatedAt);
        builds[1].Should().Match<BuildSnapshot>(b => b.DisplayName == "Release" && b.State == BuildState.Running && b.CompletedAt == null);

        fetch.Counts.Should().NotBeNull();
        fetch.Counts!.OpenIssues.Should().Be(2);
        fetch.Counts.ClosedIssues.Should().Be(40);
        fetch.Counts.ClosedIssuesByMilestone.Should().BeEquivalentTo(new Dictionary<string, int> { ["MI_kwTestMs1"] = 3, ["MI_kwTestMs2"] = 5 });
        fetch.Counts.ClosedIssuesByLabel.Should().BeNull();

        success.SyncState.GetValue(GitHubStatusProvider.WatermarkKey).Should().Be(Now.AddMinutes(-30).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        success.RateLimit.Should().NotBeNull();

        var accepted = SnapshotAssembler.Assemble(null, fetch, Now, GitHubFixtures.ClosedWindowStart)
            .Should().BeOfType<AssemblyResult.Accepted>().Which;
        accepted.Snapshot.IssueCounts.Should().Be(new IssueCounts(2, 40));
        var v1 = accepted.Snapshot.Milestones.Single(m => m.ExternalId == "MI_kwTestMs1");
        v1.OpenIssueCount.Should().Be(1);
        v1.ClosedIssueCount.Should().Be(3, "GraphQL milestone ids equal REST node_id and key the closed counts");

        var serialized = JsonSerializer.Serialize(accepted.Snapshot);
        serialized.Should().NotContain("example.invalid", "e-mail addresses are never mapped");
        serialized.Should().NotContain("Synthetic body", "issue bodies are not mapped");
    }

    [Fact]
    public async Task Every_request_carries_github_headers_and_only_reads()
    {
        _h.Api.StandardRepository();

        await _h.FetchSuccessAsync();

        _h.ClientNames.Should().OnlyContain(n => n == GitHubStatusProvider.HttpClientName);
        _h.Api.Requests.Should().NotBeEmpty();
        foreach (var entry in _h.Api.Requests)
        {
            GitHubFakeServer.Header(entry, "Authorization").Should().Be($"Bearer {GitHubProviderHarness.Token}");
            GitHubFakeServer.Header(entry, "Accept").Should().Be("application/vnd.github+json");
            GitHubFakeServer.Header(entry, "X-GitHub-Api-Version").Should().Be("2026-03-10");
            GitHubFakeServer.Header(entry, "User-Agent").Should().StartWith("GitWidget/");
            GitHubFakeServer.Header(entry, "If-None-Match").Should().BeNull("the first fetch has no stored sections to fall back to");
            var method = entry.RequestMessage!.Method;
            (method == "GET" || (method == "POST" && entry.RequestMessage.Path == GitHubFakeServer.GraphQlPath)).Should().BeTrue();
        }

        var graphQl = _h.Api.Requests.Single(r => r.RequestMessage!.Method == "POST");
        graphQl.RequestMessage!.Body.Should().NotContain("mutation");
    }

    [Fact]
    public async Task Configured_user_agent_is_used()
    {
        _h.Api.StandardRepository();
        var configuration = _h.Configuration with { UserAgent = "GitWidget/0.1 git/2.47.0" };

        var result = await _h.Provider.FetchAsync(configuration, GitHubProviderHarness.FirstFetch(), CancellationToken.None);

        result.Should().BeOfType<ProviderFetchResult.Success>();
        _h.Api.Requests.Should().OnlyContain(r => GitHubFakeServer.Header(r, "User-Agent") == "GitWidget/0.1 git/2.47.0");
    }

    [Fact]
    public async Task Token_never_appears_in_logs_sync_state_or_failures()
    {
        _h.Api.StandardRepository();
        var success = await _h.FetchSuccessAsync();
        _h.Api.Server.Reset();
        _h.Api.Get(GitHubFakeServer.RepoPath, new { message = "Bad credentials" }, status: 401);
        var failure = await _h.FetchFailureAsync();

        _h.Logger.Entries.Should().NotBeEmpty();
        _h.Logger.Entries.Should().NotContain(e => e.Contains(GitHubProviderHarness.Token, StringComparison.Ordinal));
        JsonSerializer.Serialize(success.SyncState).Should().NotContain(GitHubProviderHarness.Token);
        JsonSerializer.Serialize(success.Fetch).Should().NotContain(GitHubProviderHarness.Token);
        failure.Message.Should().NotContain(GitHubProviderHarness.Token);
    }

    [Fact]
    public async Task Open_set_follows_cursor_pagination_beyond_100_items()
    {
        var page1 = Enumerable.Range(1, 100).Select(n => GitHubFixtures.Issue(n, Now.AddHours(-n))).ToArray();
        var page2 = Enumerable.Range(101, 50).Select(n => GitHubFixtures.Issue(n, Now.AddHours(-n))).ToArray();
        _h.Api.StandardRepository(open: page1, counts: GitHubFixtures.Counts(150, 0));
        _h.Api.Get(
            $"{GitHubFakeServer.RepoPath}/issues",
            page1,
            query: [("state", "open")],
            link: _h.Api.NextLink("/api/v3/repositories/4200000001/issues", "per_page=100&state=open&page=2&after=Y3Vyc29yOjEwMA%3D%3D"),
            priority: 5);
        _h.Api.Get("/api/v3/repositories/4200000001/issues", page2, query: [("after", "Y3Vyc29yOjEwMA==")]);

        var success = await _h.FetchSuccessAsync();

        var issues = success.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.FullItems>().Which;
        issues.Open.Should().HaveCount(150);
        issues.Open.Select(i => i.ExternalId).Should().OnlyHaveUniqueItems();
        _h.Api.Requests.Should().Contain(r => r.RequestMessage!.Path == "/api/v3/repositories/4200000001/issues");
        SnapshotAssembler.Assemble(null, success.Fetch, Now, GitHubFixtures.ClosedWindowStart).Should().BeOfType<AssemblyResult.Accepted>();
    }

    [Fact]
    public async Task Pagination_link_to_another_host_is_rejected()
    {
        _h.Api.StandardRepository();
        _h.Api.Get(
            $"{GitHubFakeServer.RepoPath}/issues",
            new[] { GitHubFixtures.Issue(1, Now.AddHours(-1)) },
            query: [("state", "open")],
            link: "<https://elsewhere.example.invalid/api/v3/issues?page=2>; rel=\"next\"",
            priority: 5);

        var failure = await _h.FetchFailureAsync();

        failure.Kind.Should().Be(ProviderFailureKind.Partial);
    }

    [Fact]
    public async Task Delta_304_reports_unchanged_work_items_without_counts()
    {
        var (previous, state) = await BaselineAsync();
        state = state.WithRequests([new ConditionalRequestState(GitHubConditionalCache.Key(DeltaRelative), "W/\"delta-1\"", null, Now, 1)]);
        _h.Api.NotModified($"{GitHubFakeServer.RepoPath}/issues", "W/\"delta-1\"", [("state", "all")]);

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.WorkItems));

        success.Fetch.Issues.Should().Be(WorkItemUpdate<IssueSnapshot>.Unchanged);
        success.Fetch.ChangeRequests.Should().Be(WorkItemUpdate<ChangeRequestSnapshot>.Unchanged);
        success.Fetch.Milestones.Should().Be(SectionUpdate<MilestoneSnapshot>.Unchanged);
        success.Fetch.Labels.Should().Be(SectionUpdate<LabelSnapshot>.Unchanged);
        success.Fetch.Builds.Should().Be(SectionUpdate<BuildSnapshot>.Unchanged);
        success.Fetch.Counts.Should().BeNull();
        success.Fetch.Repository.Should().Be(previous.Repository);
        success.SyncState.GetValue(GitHubStatusProvider.WatermarkKey).Should().Be(state.GetValue(GitHubStatusProvider.WatermarkKey));
        success.SyncState.GetRequest(GitHubConditionalCache.Key(DeltaRelative))!.ETag.Should().Be("W/\"delta-1\"", "the 200 validator is kept verbatim (G7)");
        var request = _h.Api.Requests.Should().ContainSingle().Which;
        GitHubFakeServer.Header(request, "If-None-Match").Should().Be("W/\"delta-1\"");

        SnapshotAssembler.Assemble(previous, success.Fetch, Now, GitHubFixtures.ClosedWindowStart)
            .Should().BeOfType<AssemblyResult.Accepted>().Which.Snapshot.Issues.Should().BeEquivalentTo(previous.Issues);
    }

    [Fact]
    public async Task Delta_200_filters_by_watermark_overlap_and_pages_only_while_needed()
    {
        var (previous, state) = await BaselineAsync();
        var watermark = Now.AddHours(-1);
        state = state.WithValues((GitHubStatusProvider.WatermarkKey, watermark.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        _h.Api.Get(
            $"{GitHubFakeServer.RepoPath}/issues",
            new[]
            {
                GitHubFixtures.Issue(10, Now.AddMinutes(-1), state: "closed"),
                GitHubFixtures.PullRequest(11, Now.AddMinutes(-10), state: "closed", merged: true),
                GitHubFixtures.Issue(12, Now.AddMinutes(-61)),
            },
            etag: "W/\"delta-2\"",
            query: [("state", "all")],
            link: _h.Api.NextLink("/api/v3/repositories/4200000001/issues", "state=all&after=p2"));
        _h.Api.Get(
            "/api/v3/repositories/4200000001/issues",
            new[] { GitHubFixtures.Issue(13, Now.AddMinutes(-61).AddSeconds(-30)), GitHubFixtures.Issue(14, Now.AddMinutes(-70)) },
            query: [("after", "p2")],
            link: _h.Api.NextLink("/api/v3/repositories/4200000001/issues", "state=all&after=p3"));
        _h.Api.Get("/api/v3/repositories/4200000001/issues", new { message = "must not be requested" }, status: 500, query: [("after", "p3")]);
        _h.Api.GraphQl(GitHubFixtures.Counts(3, 41, [("MI_kwTestMs1", 3)]));

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.WorkItems));

        success.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.DeltaItems>()
            .Which.Changed.Select(i => i.Number).Should().BeEquivalentTo([10, 12, 13], "items older than watermark - 2 min are skipped");
        success.Fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.DeltaItems>()
            .Which.Changed.Should().ContainSingle().Which.State.Should().Be(ChangeRequestState.Merged);
        success.Fetch.Counts.Should().NotBeNull("issues changed, so counts are refreshed");
        success.SyncState.GetValue(GitHubStatusProvider.WatermarkKey).Should().Be(Now.AddMinutes(-1).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        success.SyncState.GetRequest(GitHubConditionalCache.Key(DeltaRelative))!.ETag.Should().Be("W/\"delta-2\"");
        _h.Api.Requests.Should().NotContain(r => QueryValue(r, "after") == "p3");

        var accepted = SnapshotAssembler.Assemble(previous, success.Fetch, Now, GitHubFixtures.ClosedWindowStart).Should().BeOfType<AssemblyResult.Accepted>().Which;
        accepted.Snapshot.Issues.Single(i => i.Number == 10).State.Should().Be(WorkItemState.Closed);
        accepted.Snapshot.IssueCounts.Closed.Should().Be(41);
    }

    [Fact]
    public async Task Delta_with_only_pull_request_changes_skips_the_counts_query()
    {
        var (previous, state) = await BaselineAsync();
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/issues", new[] { GitHubFixtures.PullRequest(20, Now.AddMinutes(-2)) }, query: [("state", "all")]);

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.WorkItems));

        success.Fetch.Issues.Should().Be(WorkItemUpdate<IssueSnapshot>.Unchanged);
        success.Fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.DeltaItems>();
        success.Fetch.Counts.Should().BeNull();
        _h.Api.Requests.Should().NotContain(r => r.RequestMessage!.Method == "POST");
    }

    [Fact]
    public async Task Delta_without_watermark_runs_a_full_reconciliation()
    {
        var (previous, state) = await BaselineAsync();
        StubRichRepository();

        var success = await _h.FetchSuccessAsync(Next(previous, state.WithValues((GitHubStatusProvider.WatermarkKey, null)), RefreshScope.WorkItems));

        success.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.FullItems>();
        success.Fetch.Counts.Should().NotBeNull();
        success.SyncState.GetValue(GitHubStatusProvider.WatermarkKey).Should().NotBeNull();
    }

    [Fact]
    public async Task Delta_exceeding_the_page_budget_falls_back_to_reconciliation()
    {
        var (previous, state) = await BaselineAsync();
        StubRichRepository();
        for (var page = 1; page <= GitHubStatusProvider.MaxDeltaPages + 1; page++)
        {
            var items = new[] { GitHubFixtures.Issue(1000 + page, Now.AddSeconds(-page)) };
            var link = _h.Api.NextLink("/api/v3/repositories/4200000001/issues", $"state=all&after=d{page + 1}");
            if (page == 1)
            {
                _h.Api.Get($"{GitHubFakeServer.RepoPath}/issues", items, query: [("state", "all")], link: link);
            }
            else
            {
                _h.Api.Get("/api/v3/repositories/4200000001/issues", items, query: [("after", $"d{page}")], link: link);
            }
        }

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.WorkItems));

        success.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.FullItems>();
        success.Fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.FullItems>();
    }

    [Fact]
    public async Task Reconcile_scope_returns_full_sets_and_counts()
    {
        var (previous, state) = await BaselineAsync();
        StubRichRepository();

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.Reconcile));

        success.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.FullItems>();
        success.Fetch.Milestones.Should().Be(SectionUpdate<MilestoneSnapshot>.Unchanged);
        success.Fetch.Counts.Should().NotBeNull();
        var closedRequest = _h.Api.Requests.Single(r => QueryValue(r, "state") == "closed");
        closedRequest.RequestMessage!.Query!["since"].Single().Should().Be("2026-10-21T12:00:00Z");
        closedRequest.RequestMessage.Query["sort"].Single().Should().Be("updated");
        closedRequest.RequestMessage.Query["direction"].Single().Should().Be("desc");
        var openRequest = _h.Api.Requests.Single(r => QueryValue(r, "state") == "open");
        openRequest.RequestMessage!.Query!["sort"].Single().Should().Be("created");
        openRequest.RequestMessage.Query["direction"].Single().Should().Be("asc");
        openRequest.RequestMessage.Query["per_page"].Single().Should().Be("100");
        SnapshotAssembler.Assemble(previous, success.Fetch, Now, GitHubFixtures.ClosedWindowStart).Should().BeOfType<AssemblyResult.Accepted>();
    }

    [Fact]
    public async Task Counts_query_uses_variables_and_pages_milestones()
    {
        _h.Api.StandardRepository(counts: GitHubFixtures.Counts(1, 7, [("MI_a", 1)], hasNextPage: true, endCursor: "Y3Vyc29yOnYyOpHOAAE="));
        _h.Api.GraphQl(
            GitHubFixtures.Counts(1, 7, [("MI_b", 2)]),
            when: body => body.Contains("\"milestoneCursor\":\"Y3Vyc29yOnYyOpHOAAE=\"", StringComparison.Ordinal),
            priority: 1);

        var success = await _h.FetchSuccessAsync();

        success.Fetch.Counts!.ClosedIssuesByMilestone.Should().BeEquivalentTo(new Dictionary<string, int> { ["MI_a"] = 1, ["MI_b"] = 2 });
        success.Fetch.Counts.ClosedIssues.Should().Be(7);
        var bodies = _h.Api.Requests.Where(r => r.RequestMessage!.Method == "POST").Select(r => JsonDocument.Parse(r.RequestMessage!.Body!)).ToList();
        bodies.Should().HaveCount(2);
        foreach (var body in bodies)
        {
            var query = body.RootElement.GetProperty("query").GetString()!;
            query.Should().StartWith("query").And.Contain("$owner").And.Contain("$name");
            query.Should().NotContain("octo-org").And.NotContain("widget\"", "names are passed as variables, never interpolated");
            var variables = body.RootElement.GetProperty("variables");
            variables.GetProperty("owner").GetString().Should().Be("octo-org");
            variables.GetProperty("name").GetString().Should().Be("widget");
        }

        bodies[0].RootElement.GetProperty("variables").GetProperty("milestoneCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Metadata_reuses_etags_and_keeps_previous_identity_on_304()
    {
        var (previous, state) = await BaselineAsync();
        state.GetRequest(GitHubConditionalCache.Key("repos/octo-org/widget"))!.ETag.Should().Be("W/\"repo-1\"");
        _h.Api.NotModified(GitHubFakeServer.RepoPath, "W/\"repo-1\"");
        _h.Api.NotModified($"{GitHubFakeServer.RepoPath}/milestones", "W/\"ms-1\"", [("state", "all")]);
        _h.Api.NotModified($"{GitHubFakeServer.RepoPath}/labels", "W/\"labels-1\"");
        _h.Api.GraphQl(GitHubFixtures.Counts(2, 40, [("MI_kwTestMs1", 3), ("MI_kwTestMs2", 5)]));

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.Metadata));

        success.Fetch.Repository.Should().Be(previous.Repository);
        success.Fetch.DefaultBranch.Should().Be(previous.DefaultBranch);
        success.Fetch.Features.Should().Be(previous.Features);
        success.Fetch.Milestones.Should().Be(SectionUpdate<MilestoneSnapshot>.Unchanged);
        success.Fetch.Labels.Should().Be(SectionUpdate<LabelSnapshot>.Unchanged);
        success.Fetch.Counts.Should().NotBeNull();
        _h.Api.Requests.Where(r => r.RequestMessage!.Method == "GET")
            .Should().HaveCount(3).And.OnlyContain(r => GitHubFakeServer.Header(r, "If-None-Match") != null);
        SnapshotAssembler.Assemble(previous, success.Fetch, Now, GitHubFixtures.ClosedWindowStart).Should().BeOfType<AssemblyResult.Accepted>();
    }

    [Fact]
    public async Task Multi_page_list_is_refetched_without_validator()
    {
        var (previous, state) = await BaselineAsync();
        var labelsKey = GitHubConditionalCache.Key("repos/octo-org/widget/labels?per_page=100");
        state = state.WithRequests([new ConditionalRequestState(labelsKey, "W/\"labels-1\"", null, Now, PageCount: 2)]);
        _h.Api.StandardRepository();

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.Metadata));

        success.Fetch.Labels.Should().BeOfType<SectionUpdate<LabelSnapshot>.ReplaceSection>();
        GitHubFakeServer.Header(_h.Api.Requests.Single(r => r.RequestMessage!.Path.EndsWith("/labels", StringComparison.Ordinal)), "If-None-Match")
            .Should().BeNull("a 304 carries no Link header, so multi-page lists are never validated (G6)");
    }

    [Fact]
    public async Task Renamed_repository_redirect_is_followed_and_identity_updated()
    {
        const string NewPath = "/api/v3/repos/octo-org/widget-renamed";
        _h.Api.StandardRepository(repoPath: NewPath, repository: GitHubFixtures.Repository("octo-org/widget-renamed"));
        _h.Api.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(GitHubFakeServer.RepoPath).UsingGet()).AtPriority(1)
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(301).WithHeader("Location", $"{_h.Api.Url}/api/v3/repositories/4200000001"));
        _h.Api.Get("/api/v3/repositories/4200000001", GitHubFixtures.Repository("octo-org/widget-renamed"));

        var success = await _h.FetchSuccessAsync();

        success.Fetch.Repository.RepositoryName.Should().Be("widget-renamed");
        success.Fetch.Repository.ExternalId.Should().Be(GitHubFakeServer.RepoNodeId);
        var redirected = _h.Api.Requests.Single(r => r.RequestMessage!.Path == "/api/v3/repositories/4200000001");
        GitHubFakeServer.Header(redirected, "Authorization").Should().Be($"Bearer {GitHubProviderHarness.Token}");
        _h.Logger.Entries.Should().Contain(e => e.Contains("widget-renamed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Builds_map_runs_and_skip_fork_pull_request_runs()
    {
        _h.Api.StandardRepository(runs: GitHubFixtures.Runs(
            GitHubFixtures.Run(1, "completed", "failure"),
            GitHubFixtures.Run(2, "queued", null),
            GitHubFixtures.Run(3, "completed", "success", evt: "pull_request", headRepository: "someone/widget-fork"),
            GitHubFixtures.Run(4, "completed", "stale")));

        var success = await _h.FetchSuccessAsync();

        var builds = success.Fetch.Builds.Should().BeOfType<SectionUpdate<BuildSnapshot>.ReplaceSection>().Which.Items;
        builds.Select(b => (b.ExternalId, b.State)).Should().Equal(("1", BuildState.Failed), ("2", BuildState.Queued), ("4", BuildState.Unknown));
        builds[0].CompletedAt.Should().Be(builds[0].UpdatedAt, "completed runs have no completed_at; updated_at is used");
        builds[1].CompletedAt.Should().BeNull();
        builds[0].RefName.Should().Be("main");
        builds[0].CommitSha.Should().Be("eeb44bc432c0107de002dadd34a61b4e2bd15882");
        var runsRequest = _h.Api.Requests.Single(r => r.RequestMessage!.Path.EndsWith("/actions/runs", StringComparison.Ordinal));
        runsRequest.RequestMessage!.Query!["branch"].Single().Should().Be("main");
        runsRequest.RequestMessage.Query["per_page"].Single().Should().Be("20");
    }

    [Fact]
    public async Task Actions_disabled_404_yields_empty_builds_and_disabled_feature()
    {
        _h.Api.StandardRepository();
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/actions/runs", new { message = "Not Found" }, status: 404, priority: 1);

        var success = await _h.FetchSuccessAsync();

        success.Fetch.Builds.Should().BeOfType<SectionUpdate<BuildSnapshot>.ReplaceSection>().Which.Items.Should().BeEmpty();
        success.Fetch.Features.Builds.Should().BeFalse();
        success.Fetch.Features.Issues.Should().BeTrue();
        SnapshotAssembler.Assemble(null, success.Fetch, Now, GitHubFixtures.ClosedWindowStart).Should().BeOfType<AssemblyResult.Accepted>();
    }

    [Fact]
    public async Task Token_without_actions_permission_hides_builds_instead_of_failing()
    {
        _h.Api.StandardRepository();
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/actions/runs", new { message = "Resource not accessible by personal access token" }, status: 403, priority: 1);

        var success = await _h.FetchSuccessAsync();

        success.Fetch.Builds.Should().BeOfType<SectionUpdate<BuildSnapshot>.ReplaceSection>().Which.Items.Should().BeEmpty();
        success.Fetch.Features.Builds.Should().BeFalse();
        SnapshotAssembler.Assemble(null, success.Fetch, Now, GitHubFixtures.ClosedWindowStart).Should().BeOfType<AssemblyResult.Accepted>();
    }

    [Fact]
    public async Task Builds_only_fetch_with_404_confirms_the_repository_exists()
    {
        var (previous, state) = await BaselineAsync();
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/actions/runs", new { message = "Not Found" }, status: 404);
        _h.Api.Get(GitHubFakeServer.RepoPath, GitHubFixtures.Repository());

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.Builds));

        success.Fetch.Features.Builds.Should().BeFalse();
        success.Fetch.Builds.Should().BeOfType<SectionUpdate<BuildSnapshot>.ReplaceSection>();
        success.SyncState.GetRequest(GitHubConditionalCache.Key("repos/octo-org/widget"))!.ETag
            .Should().Be("W/\"repo-1\"", "the existence check must not replace the metadata validator");
    }

    [Fact]
    public async Task Builds_only_fetch_fails_when_the_repository_is_gone()
    {
        var (previous, state) = await BaselineAsync();
        _h.Api.Get($"{GitHubFakeServer.RepoPath}/actions/runs", new { message = "Not Found" }, status: 404);
        _h.Api.Get(GitHubFakeServer.RepoPath, new { message = "Not Found" }, status: 404);

        var failure = await _h.FetchFailureAsync(Next(previous, state, RefreshScope.Builds));

        failure.Kind.Should().Be(ProviderFailureKind.NotFound);
    }

    [Fact]
    public async Task Builds_304_is_unchanged()
    {
        var (previous, state) = await BaselineAsync();
        _h.Api.NotModified($"{GitHubFakeServer.RepoPath}/actions/runs", "W/\"runs-1\"");

        var success = await _h.FetchSuccessAsync(Next(previous, state, RefreshScope.Builds));

        success.Fetch.Builds.Should().Be(SectionUpdate<BuildSnapshot>.Unchanged);
        success.Fetch.Features.Builds.Should().BeTrue();
        success.Fetch.Counts.Should().BeNull();
        _h.Api.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Disabled_issues_and_pull_requests_are_reported_as_features()
    {
        // Issues disabled: REST lists nothing (G12) while GraphQL still counts 12 hidden open issues.
        _h.Api.StandardRepository(open: [], repository: GitHubFixtures.Repository(hasIssues: false, hasPullRequests: false), counts: GitHubFixtures.Counts(12, 30));

        var success = await _h.FetchSuccessAsync();

        success.Fetch.Features.Should().Be(new RepositoryFeatures(Issues: false, ChangeRequests: false, Builds: true));
        success.Fetch.Counts!.OpenIssues.Should().BeNull();
        SnapshotAssembler.Assemble(null, success.Fetch, Now, GitHubFixtures.ClosedWindowStart).Should().BeOfType<AssemblyResult.Accepted>();
    }
}
