using System.Globalization;
using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Snapshots;
using GitStatusWidget.Infrastructure.Providers.GitLab;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace GitStatusWidget.Infrastructure.Tests.Providers.GitLab;

public sealed class GitLabFetchTests : IDisposable
{
    private readonly GitLabTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ProviderFetchResult.Success> FirstFetchAsync(GitLabTestHarness? harness = null)
    {
        var h = harness ?? _h;
        var result = await h.CreateProvider().FetchAsync(h.Configuration(), GitLabTestHarness.FirstContext(), Ct);
        return result.ShouldSucceed(h);
    }

    private static RepositorySnapshot Accept(RepositorySnapshot? previous, ProviderFetch fetch) =>
        SnapshotAssembler.Assemble(previous, fetch, GitLabTestHarness.Now, GitLabTestHarness.WindowStart)
            .Should().BeOfType<AssemblyResult.Accepted>().Subject.Snapshot;

    private void ResetServer()
    {
        _h.Server.ResetMappings();
        _h.Server.ResetLogEntries();
    }

    [Fact]
    public async Task First_fetch_returns_full_sections_and_counts()
    {
        _h.StubStandardProject();

        var success = await FirstFetchAsync();
        var fetch = success.Fetch;

        fetch.Repository.ExternalId.Should().Be("4242");
        fetch.Repository.OwnerPath.Should().Be("acme/tools");
        fetch.Repository.RepositoryName.Should().Be("widget");
        fetch.Repository.FullPath.Should().Be(GitLabTestHarness.FullPath);
        fetch.DefaultBranch.Should().Be("main");
        fetch.Features.Should().Be(new RepositoryFeatures(true, true, true));

        var issues = fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.FullItems>().Subject;
        issues.Open.Select(i => i.ExternalId).Should().BeEquivalentTo(["900001", "900002"]);
        issues.RecentlyClosed.Select(i => i.ExternalId).Should().Equal("900003");
        issues.ClosedSince.Should().Be(GitLabTestHarness.WindowStart);
        var first = issues.Open.Single(i => i.ExternalId == "900001");
        first.Number.Should().Be(1);
        first.WebUrl.AbsolutePath.Should().EndWith("/-/work_items/1", "web_url is opaque and used as-is");
        first.Milestone.Should().Be(new MilestoneReference("501", "v1.2"));
        first.Labels.Should().Equal(new LabelReference("bug", "#D9534F"));
        first.Assignees.Single().Login.Should().Be("jdoe");
        first.Author!.ExternalId.Should().Be("78");

        var changeRequests = fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.FullItems>().Subject;
        changeRequests.Open.Single().Should().Match<ChangeRequestSnapshot>(c => c.IsDraft && c.Number == 11 && c.Kind == ChangeRequestKind.MergeRequest);
        changeRequests.RecentlyClosed.Single().State.Should().Be(ChangeRequestState.Merged);

        fetch.Labels.Should().BeOfType<SectionUpdate<LabelSnapshot>.ReplaceSection>().Which.Items.Select(l => l.Name).Should().BeEquivalentTo(["bug", "priority::high"]);
        fetch.Milestones.Should().BeOfType<SectionUpdate<MilestoneSnapshot>.ReplaceSection>().Which.Items.Select(m => m.ExternalId).Should().BeEquivalentTo(["501", "7324682"]);
        var build = fetch.Builds.Should().BeOfType<SectionUpdate<BuildSnapshot>.ReplaceSection>().Which.Items.Single();
        build.ExternalId.Should().Be("3028771539", "pipeline ids exceed int.MaxValue");
        build.DisplayName.Should().Be("Pipeline #54761");
        build.State.Should().Be(BuildState.Running);
        build.CompletedAt.Should().BeNull();

        fetch.Counts.Should().NotBeNull();
        fetch.Counts!.OpenIssues.Should().Be(2);
        fetch.Counts.ClosedIssues.Should().Be(40);
        fetch.Counts.ClosedIssuesByMilestone.Should().BeEquivalentTo(new Dictionary<string, int> { ["501"] = 7, ["7324682"] = 3 });

        success.SyncState.GetValue(GitLabStatusProvider.ProjectIdKey).Should().Be("4242");
        success.SyncState.GetValue(GitLabStatusProvider.IssuesWatermarkKey).Should().NotBeNull();
        _h.Factory.Names.Should().OnlyContain(n => n == "GitLab");
    }

    [Fact]
    public async Task First_fetch_is_accepted_by_the_assembler_and_merge_requests_are_not_counted_as_issues()
    {
        _h.StubStandardProject();

        var success = await FirstFetchAsync();
        var snapshot = Accept(null, success.Fetch);

        snapshot.IssueCounts.Should().Be(new IssueCounts(2, 40), "merge requests are never counted as issues");
        snapshot.Issues.Should().HaveCount(3);
        snapshot.ChangeRequests.Should().HaveCount(2);
        snapshot.Issues.Select(i => i.ExternalId).Should().NotIntersectWith(snapshot.ChangeRequests.Select(c => c.ExternalId));
        var milestone = snapshot.Milestones.Single(m => m.ExternalId == "501");
        milestone.OpenIssueCount.Should().Be(1);
        milestone.ClosedIssueCount.Should().Be(7);
        snapshot.Builds.Should().ContainSingle();
    }

    [Fact]
    public async Task Requests_carry_private_token_and_configured_user_agent_and_are_read_only()
    {
        _h.StubStandardProject();

        await FirstFetchAsync();

        _h.Server.LogEntries.Should().NotBeEmpty();
        foreach (var entry in _h.Server.LogEntries)
        {
            var request = entry.RequestMessage!;
            request.Headers!["PRIVATE-TOKEN"].Single().Should().Be(GitLabTestHarness.Token);
            request.Headers["User-Agent"].Single().Should().Be(GitLabTestHarness.UserAgent);
            request.Headers.Should().NotContainKey("Authorization");
            request.Url.Should().NotContain(GitLabTestHarness.Token);
            if (request.Method == "POST")
            {
                request.AbsolutePath.Should().Be("/api/graphql");
                request.Body.Should().NotContain("mutation");
            }
            else
            {
                request.Method.Should().Be("GET");
            }
        }
    }

    [Fact]
    public async Task Default_user_agent_is_used_when_none_is_configured()
    {
        _h.StubStandardProject();

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(userAgent: null), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldSucceed(_h);
        _h.Server.LogEntries[0].RequestMessage!.Headers!["User-Agent"].Single().Should().StartWith("GitWidget/");
    }

    [Fact]
    public async Task Token_never_appears_in_logs_sync_state_or_results()
    {
        _h.StubStandardProject();

        var success = await FirstFetchAsync();

        JsonSerializer.Serialize(success.SyncState).Should().NotContain(GitLabTestHarness.Token);
        JsonSerializer.Serialize(success.Fetch).Should().NotContain(GitLabTestHarness.Token);
        JsonSerializer.Serialize(success.Fetch).Should().NotContain("should-never-be-mapped", "e-mail addresses are not mapped");
        _h.Logger.Messages.Should().NotBeEmpty();
        _h.Logger.Messages.Should().NotContain(m => m.Contains(GitLabTestHarness.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Subgroup_path_is_encoded_once_and_sent_raw()
    {
        _h.StubStandardProject();

        await FirstFetchAsync();

        var resolution = _h.Server.LogEntries[0].RequestMessage!;
        resolution.Url.Should().EndWith("/api/v4/projects/acme%2Ftools%2Fwidget", "the %2F must reach the server undecoded");
        resolution.Url.Should().NotContain("%252F");
        _h.Server.LogEntries.Skip(1).Should().OnlyContain(e => e.RequestMessage!.Url.Contains("/projects/4242", StringComparison.Ordinal) || e.RequestMessage.AbsolutePath == "/api/graphql",
            "after resolution the project is addressed by numeric id");
    }

    [Fact]
    public async Task Relative_url_root_is_kept_for_rest_and_graphql()
    {
        using var h = new GitLabTestHarness("/gitlab/");
        h.StubStandardProject();

        var success = await FirstFetchAsync(h);

        success.Fetch.Repository.ExternalId.Should().Be("4242");
        h.Server.LogEntries.Should().OnlyContain(e => e.RequestMessage!.AbsolutePath.StartsWith("/gitlab/api/", StringComparison.Ordinal));
        h.Server.LogEntries.Should().Contain(e => e.RequestMessage!.AbsolutePath == "/gitlab/api/graphql");
        h.Server.LogEntries[0].RequestMessage!.Url.Should().EndWith("/gitlab/api/v4/projects/acme%2Ftools%2Fwidget");
    }

    [Fact]
    public async Task Work_items_of_other_types_are_filtered_and_issue_type_is_requested()
    {
        _h.StubStandardProject();
        _h.StubIssues("opened", [
            Fixtures.Issue(_h.WebBase, 900001, 1, "opened", GitLabTestHarness.Now.AddHours(-5)),
            Fixtures.Issue(_h.WebBase, 900010, 10, "opened", GitLabTestHarness.Now.AddHours(-5), issueType: "task"),
            Fixtures.Issue(_h.WebBase, 900011, 11, "opened", GitLabTestHarness.Now.AddHours(-5), issueType: "incident"),
            Fixtures.Issue(_h.WebBase, 900012, 12, "opened", GitLabTestHarness.Now.AddHours(-5), issueType: "requirement"),
        ], priority: 1);

        var success = await FirstFetchAsync();

        success.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.FullItems>().Which.Open.Select(i => i.ExternalId).Should().Equal("900001");
        _h.Requests("/issues").Should().OnlyContain(e => e.RequestMessage!.Query!["issue_type"].Single() == "issue");
        _h.Requests("/issues").Should().OnlyContain(e => e.RequestMessage!.Query!["with_labels_details"].Single() == "true");
    }

    [Fact]
    public async Task Issues_follow_keyset_link_pagination_beyond_100_items_on_the_configured_host()
    {
        _h.StubStandardProject();
        var page1 = Enumerable.Range(1, 100).Select(i => Fixtures.Issue(_h.WebBase, 800000 + i, i, "opened", GitLabTestHarness.Now.AddHours(-i))).ToArray();
        var page2 = Enumerable.Range(101, 20).Select(i => Fixtures.Issue(_h.WebBase, 800000 + i, i, "opened", GitLabTestHarness.Now.AddHours(-i))).ToArray();

        // The Link host differs (GitLab's external_url); the adapter keeps its own host and path and only takes the cursor.
        var link = $"<https://gitlab.example.invalid/api/v4/projects/4242/issues?cursor=eyJpZCI6IjEwMCJ9%3D&issue_type=issue&order_by=created_at&pagination=keyset&per_page=100&sort=asc&state=opened&with_labels_details=true>; rel=\"next\"";
        _h.StubJson(_h.ProjectApi + "/issues", page1, match: r => r.WithParam("state", "opened"), response: r => r.WithHeader("Link", link), priority: 5);
        _h.StubJson(_h.ProjectApi + "/issues", page2, match: r => r.WithParam("state", "opened").WithParam("cursor", "eyJpZCI6IjEwMCJ9="), priority: 1);
        _h.StubCounts(open: 120, closed: 40, 7, 3);

        var success = await FirstFetchAsync();

        var issues = success.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.FullItems>().Subject;
        issues.Open.Should().HaveCount(120);
        var openRequests = _h.Requests("/issues").Where(e => e.RequestMessage!.Query!["state"].Single() == "opened").ToList();
        openRequests.Should().HaveCount(2);
        openRequests[0].RequestMessage!.Query!["pagination"].Single().Should().Be("keyset");
        openRequests[0].RequestMessage!.Query!["order_by"].Single().Should().Be("created_at");
        openRequests[1].RequestMessage!.Url.Should().StartWith(_h.Server.Url + "/api/v4/projects/4242/issues?cursor=eyJpZCI6IjEwMCJ9%3D");
        Accept(null, success.Fetch).IssueCounts.Open.Should().Be(120);
    }

    [Fact]
    public async Task Merge_requests_follow_offset_pagination_with_x_next_page()
    {
        _h.StubStandardProject();
        var page1 = Enumerable.Range(1, 100).Select(i => Fixtures.MergeRequest(_h.WebBase, 700000 + i, i, "opened", GitLabTestHarness.Now.AddHours(-i))).ToArray();
        var page2 = Enumerable.Range(101, 5).Select(i => Fixtures.MergeRequest(_h.WebBase, 700000 + i, i, "opened", GitLabTestHarness.Now.AddHours(-i))).ToArray();
        _h.StubJson(_h.ProjectApi + "/merge_requests", page1, match: r => r.WithParam("state", "opened"), response: r => r.WithHeader("X-Next-Page", "2").WithHeader("X-Page", "1"), priority: 5);
        _h.StubJson(_h.ProjectApi + "/merge_requests", page2, match: r => r.WithParam("state", "opened").WithParam("page", "2"), response: r => r.WithHeader("X-Next-Page", string.Empty), priority: 1);

        var success = await FirstFetchAsync();

        success.Fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.FullItems>().Which.Open.Should().HaveCount(105);
        _h.Requests("/merge_requests").Should().NotContain(e => e.RequestMessage!.Query!.ContainsKey("pagination"), "keyset is not supported for merge requests");
    }

    [Fact]
    public async Task Delta_uses_updated_after_with_overlap_and_advances_watermark()
    {
        _h.StubStandardProject();
        var first = await FirstFetchAsync();
        var snapshot = Accept(null, first.Fetch);
        var watermark = DateTimeOffset.Parse(first.SyncState.GetValue(GitLabStatusProvider.IssuesWatermarkKey)!, CultureInfo.InvariantCulture);
        watermark.Should().Be(GitLabTestHarness.Now.AddHours(-3), "the watermark is the newest updated_at seen (server clock)");

        ResetServer();
        var changedAt = GitLabTestHarness.Now.AddMinutes(1);
        _h.StubIssues("all", [
            Fixtures.Issue(_h.WebBase, 900002, 2, "closed", changedAt),
            Fixtures.Issue(_h.WebBase, 900020, 20, "opened", changedAt.AddSeconds(-5), issueType: "task"),
        ]);
        _h.StubMergeRequests("all", []);
        _h.StubCounts(open: 1, closed: 41, 8, 3);

        var context = new SnapshotRequestContext(snapshot, first.SyncState, RefreshScope.WorkItems, GitLabTestHarness.WindowStart);
        var delta = (await _h.CreateProvider().FetchAsync(_h.Configuration(), context, Ct)).ShouldSucceed(_h);

        var changed = delta.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.DeltaItems>().Subject.Changed;
        changed.Should().ContainSingle().Which.State.Should().Be(WorkItemState.Closed);
        delta.Fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.DeltaItems>().Which.Changed.Should().BeEmpty();
        delta.Fetch.Labels.Should().Be(SectionUpdate<LabelSnapshot>.Unchanged);
        delta.Fetch.Builds.Should().Be(SectionUpdate<BuildSnapshot>.Unchanged);
        delta.Fetch.Counts!.ClosedIssues.Should().Be(41, "counts refresh when the delta contained issues");

        var issueRequest = _h.Requests("/issues").Single().RequestMessage!;
        issueRequest.Query!["updated_after"].Single().Should().Be(Fixtures.Ts(watermark.AddSeconds(-120)));
        issueRequest.Query["state"].Single().Should().Be("all");
        issueRequest.Query["order_by"].Single().Should().Be("updated_at");
        issueRequest.Query["sort"].Single().Should().Be("asc");
        issueRequest.Query["per_page"].Single().Should().Be("100");
        _h.Requests("/merge_requests").Single().RequestMessage!.Query!["updated_after"].Single().Should().Be(
            Fixtures.Ts(DateTimeOffset.Parse(first.SyncState.GetValue(GitLabStatusProvider.MergeRequestsWatermarkKey)!, CultureInfo.InvariantCulture).AddSeconds(-120)));
        _h.Server.LogEntries.Should().NotContain(e => e.RequestMessage!.AbsolutePath == "/api/v4/projects/4242", "the project is not re-read for a delta");

        DateTimeOffset.Parse(delta.SyncState.GetValue(GitLabStatusProvider.IssuesWatermarkKey)!, CultureInfo.InvariantCulture).Should().Be(changedAt);

        var next = Accept(snapshot, delta.Fetch);
        next.IssueCounts.Should().Be(new IssueCounts(1, 41));
        next.Issues.Should().NotContain(i => i.ExternalId == "900020");
    }

    [Fact]
    public async Task Empty_delta_keeps_watermark_and_skips_counts()
    {
        _h.StubStandardProject();
        var first = await FirstFetchAsync();
        var snapshot = Accept(null, first.Fetch);
        ResetServer();
        _h.StubIssues("all", []);
        _h.StubMergeRequests("all", []);

        var context = new SnapshotRequestContext(snapshot, first.SyncState, RefreshScope.WorkItems, GitLabTestHarness.WindowStart);
        var delta = (await _h.CreateProvider().FetchAsync(_h.Configuration(), context, Ct)).ShouldSucceed(_h);

        delta.Fetch.Issues.Should().BeOfType<WorkItemUpdate<IssueSnapshot>.DeltaItems>().Which.Changed.Should().BeEmpty();
        delta.Fetch.Counts.Should().BeNull();
        _h.Server.LogEntries.Should().NotContain(e => e.RequestMessage!.AbsolutePath == "/api/graphql");
        delta.SyncState.GetValue(GitLabStatusProvider.IssuesWatermarkKey).Should().Be(first.SyncState.GetValue(GitLabStatusProvider.IssuesWatermarkKey));
        Accept(snapshot, delta.Fetch).IssueCounts.Should().Be(snapshot.IssueCounts);
    }

    [Fact]
    public async Task Counts_use_graphql_variables_and_chunks_of_twenty_milestones()
    {
        _h.StubStandardProject();
        var milestones = Enumerable.Range(1, 45).Select(i => Fixtures.Milestone(1000 + i, $"Release \"{i}\" }} {{ query")).ToArray();
        _h.StubMilestones(milestones);
        var aliases = new Dictionary<string, object> { ["io"] = new { count = 2 }, ["ic"] = new { count = 40 } };
        for (var i = 0; i < 20; i++)
        {
            aliases[$"m{i}"] = new { count = i };
        }

        _h.StubGraphQl(new { project = aliases }, priority: 1);

        var success = await FirstFetchAsync();

        var posts = _h.Server.LogEntries.Where(e => e.RequestMessage!.AbsolutePath == "/api/graphql").Select(e => JsonDocument.Parse(e.RequestMessage!.Body!)).ToList();
        posts.Should().HaveCount(3, "45 milestones need 3 queries of at most 20");
        foreach (var post in posts)
        {
            var query = post.RootElement.GetProperty("query").GetString()!;
            query.Should().NotContain("Release", "titles travel only as variables");
            query.Should().StartWith("query ");
            post.RootElement.GetProperty("variables").GetProperty("path").GetString().Should().Be(GitLabTestHarness.FullPath);
        }

        posts[0].RootElement.GetProperty("query").GetString().Should().Contain("io: issues(state: opened, types: [ISSUE])").And.Contain("m19:").And.NotContain("m20:");
        posts[1].RootElement.GetProperty("query").GetString().Should().NotContain("io:");
        posts[2].RootElement.GetProperty("variables").EnumerateObject().Count().Should().Be(1 + 5);
        posts[0].RootElement.GetProperty("variables").GetProperty("t0").GetString().Should().Be("Release \"1\" } { query");

        var byMilestone = success.Fetch.Counts!.ClosedIssuesByMilestone;
        byMilestone.Should().HaveCount(45);
        byMilestone["1001"].Should().Be(0);
        byMilestone["1020"].Should().Be(19);
        byMilestone["1021"].Should().Be(0);
        byMilestone["1045"].Should().Be(4);
    }

    [Fact]
    public async Task Graphql_errors_fail_the_fetch_without_fallback()
    {
        _h.StubStandardProject();
        _h.Server.Given(Request.Create().WithPath("/api/graphql").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"errors\":[{\"message\":\"Query has complexity of 300, which exceeds max complexity of 250\"}],\"data\":{\"project\":null}}"));

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.Malformed).Message.Should().Contain("complexity");
        _h.Requests("issues_statistics").Should().BeEmpty();
    }

    [Fact]
    public async Task Graphql_project_null_is_not_found()
    {
        _h.StubStandardProject();
        _h.StubGraphQl(new { project = (object?)null }, priority: 1);

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.NotFound);
    }

    [Theory]
    [InlineData(404, "{\"error\":\"404 Not Found\"}", "application/json")]
    [InlineData(503, "<html>maintenance</html>", "text/html")]
    [InlineData(200, "<html>logon</html>", "text/html")]
    public async Task Unavailable_graphql_falls_back_to_issues_statistics(int status, string body, string contentType)
    {
        _h.StubStandardProject();
        _h.Server.Given(Request.Create().WithPath("/api/graphql").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body));
        _h.StubJson(_h.ProjectApi + "/issues_statistics", new { statistics = new { counts = new { all = 45, closed = 42, opened = 3 } } }, priority: 5);
        _h.StubJson(_h.ProjectApi + "/issues_statistics", new { statistics = new { counts = new { all = 9, closed = 8, opened = 1 } } }, match: r => r.WithParam("milestone", "v1.2"), priority: 1);
        _h.StubJson(_h.ProjectApi + "/issues_statistics", new { statistics = new { counts = new { all = 2, closed = 2, opened = 0 } } }, match: r => r.WithParam("milestone", "19.9"), priority: 1);

        var success = await FirstFetchAsync();

        var counts = success.Fetch.Counts!;
        counts.ClosedIssues.Should().Be(42);
        counts.OpenIssues.Should().BeNull("issues_statistics counts every work-item type, so it is no consistency check for the issue-only open set");
        counts.ClosedIssuesByMilestone.Should().BeEquivalentTo(new Dictionary<string, int> { ["501"] = 8, ["7324682"] = 2 });
        _h.Requests("issues_statistics").Should().HaveCount(3);
        _h.Logger.Messages.Should().Contain(m => m.Contains("issues_statistics", StringComparison.Ordinal));
        Accept(null, success.Fetch).IssueCounts.Should().Be(new IssueCounts(2, 42));
    }

    [Fact]
    public async Task Rate_limited_graphql_does_not_fall_back()
    {
        _h.StubStandardProject();
        _h.Server.Given(Request.Create().WithPath("/api/graphql").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "text/plain").WithHeader("Retry-After", "30").WithBody("Retry later"));

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.RateLimited);
        _h.Requests("issues_statistics").Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_merge_requests_and_pipelines_give_empty_sections()
    {
        _h.StubStandardProject();
        _h.StubJson(_h.ProjectApi + "/merge_requests", Array.Empty<object>(), priority: 1);
        _h.StubJson(_h.ProjectApi + "/pipelines", Array.Empty<object>(), priority: 1);

        var success = await FirstFetchAsync();

        var changeRequests = success.Fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.FullItems>().Subject;
        changeRequests.Open.Should().BeEmpty();
        changeRequests.RecentlyClosed.Should().BeEmpty();
        success.Fetch.Builds.Should().BeOfType<SectionUpdate<BuildSnapshot>.ReplaceSection>().Which.Items.Should().BeEmpty();
        success.Fetch.Features.Builds.Should().BeTrue("feature flags stay enabled; the UI hides empty panels");
        _h.Server.LogEntries.Should().NotContain(e => e.RequestMessage!.AbsolutePath.EndsWith("/pipelines/latest", StringComparison.Ordinal));
        Accept(null, success.Fetch).Builds.Should().BeEmpty();
    }

    [Fact]
    public async Task Pipelines_forbidden_because_ci_is_disabled_hides_builds_without_failing()
    {
        _h.StubStandardProject();
        _h.StubJson(_h.ProjectApi + "/pipelines", new { message = "403 Forbidden" }, status: 403, priority: 1);

        var success = await FirstFetchAsync();

        success.Fetch.Builds.Should().BeOfType<SectionUpdate<BuildSnapshot>.ReplaceSection>().Which.Items.Should().BeEmpty();
        success.Fetch.Features.Builds.Should().BeFalse();
    }

    [Fact]
    public async Task Pipelines_with_insufficient_scope_fail_the_fetch()
    {
        _h.StubStandardProject();
        _h.StubJson(_h.ProjectApi + "/pipelines", new
        {
            error = "insufficient_scope",
            error_description = "The request requires higher privileges than provided by the access token.",
            scope = "api read_api",
        }, status: 403, priority: 1);

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.AuthorizationFailed).Message.Should().Contain("read_api");
    }

    [Fact]
    public async Task Disabled_features_are_not_requested()
    {
        _h.StubStandardProject();
        _h.StubJson(_h.Api + "projects/" + GitLabTestHarness.FullPath, Fixtures.Project(_h.WebBase, mergeRequests: "disabled", builds: "disabled"), priority: 1);

        var success = await FirstFetchAsync();

        success.Fetch.Features.Should().Be(new RepositoryFeatures(true, false, false));
        success.Fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.FullItems>().Which.Open.Should().BeEmpty();
        _h.Requests("/merge_requests").Should().BeEmpty();
        _h.Requests("/pipelines").Should().BeEmpty();
    }

    [Fact]
    public async Task Builds_use_etag_and_304_is_unchanged()
    {
        _h.StubStandardProject();
        var first = await FirstFetchAsync();
        var snapshot = Accept(null, first.Fetch);
        ResetServer();
        _h.Server.Given(Request.Create().WithPath(_h.ProjectApi + "/pipelines").UsingGet().WithHeader("If-None-Match", "W/\"pipelines-v1\""))
            .RespondWith(Response.Create().WithStatusCode(304).WithHeader("ETag", "W/\"pipelines-v1\""));

        var context = new SnapshotRequestContext(snapshot, first.SyncState, RefreshScope.Builds, GitLabTestHarness.WindowStart);
        var result = (await _h.CreateProvider().FetchAsync(_h.Configuration(), context, Ct)).ShouldSucceed(_h);

        result.Fetch.Builds.Should().Be(SectionUpdate<BuildSnapshot>.Unchanged);
        result.Fetch.Issues.Should().Be(WorkItemUpdate<IssueSnapshot>.Unchanged);
        result.Fetch.Counts.Should().BeNull();
        _h.Server.LogEntries.Should().ContainSingle("a builds-only fetch needs one request (project id from sync state)");
        var request = _h.Server.LogEntries.Single().RequestMessage!;
        request.Headers!["If-None-Match"].Single().Should().Be("W/\"pipelines-v1\"", "weak validators are sent back byte-for-byte");
        request.Query!["ref"].Single().Should().Be("main");
        request.Query["order_by"].Single().Should().Be("id");
        request.Query["sort"].Single().Should().Be("desc");
        request.Query["per_page"].Single().Should().Be("20");
        result.SyncState.Requests.Values.Should().ContainSingle().Which.ETag.Should().Be("W/\"pipelines-v1\"");
        Accept(snapshot, result.Fetch).Builds.Should().Equal(snapshot.Builds);
    }

    [Fact]
    public async Task Moved_project_redirect_is_followed_once_by_numeric_id()
    {
        _h.StubStandardProject();
        _h.Server.Given(Request.Create().WithPath(_h.Api + "projects/" + GitLabTestHarness.FullPath).UsingGet()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(301).WithHeader("Location", _h.Server.Url + "/api/v4/projects/4242"));

        var success = await FirstFetchAsync();

        success.Fetch.Repository.ExternalId.Should().Be("4242");
        _h.Server.LogEntries.Take(2).Select(e => e.RequestMessage!.AbsolutePath).Should().Equal("/api/v4/projects/acme/tools/widget", "/api/v4/projects/4242");
        _h.Logger.Messages.Should().Contain(m => m.Contains("moved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gateway_302_to_portal_is_interception_and_not_followed()
    {
        // Exact shape observed on the target (gitlab-api.md §19): 302 "Object Moved", text/html, empty body.
        _h.Server.Given(Request.Create().UsingAnyMethod()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Content-Type", "text/html")
                .WithHeader("Location", "https://portal.example.invalid/cgi/tm?code=8f3a0c1d").WithBody(string.Empty));

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(userAgent: "curl/8.7.1"), GitLabTestHarness.FirstContext(), Ct);

        var failure = result.ShouldFail(ProviderFailureKind.GatewayInterception);
        failure.Message.Should().Contain("portal.example.invalid").And.NotContain("code=");
        _h.Server.LogEntries.Should().ContainSingle();
    }

    [Fact]
    public async Task Gateway_html_200_is_interception()
    {
        _h.Server.Given(Request.Create().UsingAnyMethod()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/html; charset=utf-8").WithBody("<html><body>NetScaler Gateway logon</body></html>"));

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.GatewayInterception);
    }

    [Theory]
    [InlineData("{\"message\":\"401 Unauthorized\"}", "rejected")]
    [InlineData("{\"error\":\"invalid_token\",\"error_description\":\"Token is expired. You can either do re-authorization or token refresh.\"}", "expired")]
    [InlineData("{\"error\":\"invalid_token\",\"error_description\":\"Token was revoked. You have to re-authorize from the user.\"}", "revoked")]
    public async Task Unauthorized_shapes_are_authentication_failures(string body, string expected)
    {
        _h.StubJson(_h.Api + "projects/" + GitLabTestHarness.FullPath, JsonDocument.Parse(body).RootElement, status: 401);

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        var failure = result.ShouldFail(ProviderFailureKind.AuthenticationFailed);
        failure.Message.Should().Contain(expected);
        failure.Message.Should().NotContain(GitLabTestHarness.Token);
        _h.Logger.Messages.Should().NotContain(m => m.Contains(GitLabTestHarness.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Route_404_is_configuration_and_project_404_is_not_found()
    {
        _h.StubJson(_h.Api + "projects/" + GitLabTestHarness.FullPath, new { error = "404 Not Found" }, status: 404);
        var route = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);
        route.ShouldFail(ProviderFailureKind.Configuration).Message.Should().Contain("%2F");

        ResetServer();
        _h.StubJson(_h.Api + "projects/" + GitLabTestHarness.FullPath, new { message = "404 Project Not Found" }, status: 404);
        var project = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);
        project.ShouldFail(ProviderFailureKind.NotFound).Message.Should().Contain("Project Not Found");
    }

    [Fact]
    public async Task Plain_text_429_with_retry_after_is_rate_limited_and_not_retried()
    {
        _h.Server.Given(Request.Create().UsingGet()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "text/plain").WithHeader("Retry-After", "37").WithBody("Retry later\n"));

        var result = await _h.CreateProvider(retries: 2).FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        var failure = result.ShouldFail(ProviderFailureKind.RateLimited);
        failure.RetryAfter.Should().Be(GitLabTestHarness.Now.AddSeconds(37));
        _h.Server.LogEntries.Should().ContainSingle();
        result.RateLimit.Should().BeNull("the target sends no RateLimit-* headers");
    }

    [Fact]
    public async Task Rate_limit_headers_are_reported_when_present()
    {
        _h.StubStandardProject();
        _h.StubJson(_h.ProjectApi + "/pipelines", Array.Empty<object>(), response: r => r
            .WithHeader("RateLimit-Limit", "2000").WithHeader("RateLimit-Remaining", "1990").WithHeader("RateLimit-Reset", "1793821500")
            .WithHeader("RateLimit-Name", "throttle_authenticated_api"), priority: 1);

        var success = await FirstFetchAsync();

        success.RateLimit.Should().Be(new RateLimitInfo(2000, 1990, DateTimeOffset.FromUnixTimeSeconds(1793821500), "throttle_authenticated_api"));
    }

    [Fact]
    public async Task Server_errors_are_retried_then_reported_as_transient()
    {
        _h.StubJson(_h.Api + "projects/" + GitLabTestHarness.FullPath, new { message = "500 Internal Server Error" }, status: 500);

        var result = await _h.CreateProvider(retries: 2).FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.Transient);
        _h.Server.LogEntries.Should().HaveCount(3);
    }

    [Fact]
    public async Task Malformed_json_is_malformed()
    {
        _h.StubStandardProject();
        _h.Server.Given(Request.Create().WithPath(_h.ProjectApi + "/labels").UsingGet()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[{\"name\":\"bug\","));

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Missing_required_field_is_malformed()
    {
        _h.StubStandardProject();
        _h.StubJson(_h.ProjectApi + "/issues", new[] { new { id = 1, iid = 1, state = "opened", issue_type = "issue" } }, match: r => r.WithParam("state", "opened"), priority: 1);

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Failing_later_page_fails_the_whole_fetch()
    {
        _h.StubStandardProject();
        var page1 = Enumerable.Range(1, 100).Select(i => Fixtures.Issue(_h.WebBase, 800000 + i, i, "opened", GitLabTestHarness.Now.AddHours(-i))).ToArray();
        _h.StubJson(_h.ProjectApi + "/issues", page1, match: r => r.WithParam("state", "opened"),
            response: r => r.WithHeader("Link", $"<{_h.Server.Url}/api/v4/projects/4242/issues?cursor=abc&state=opened>; rel=\"next\""), priority: 5);
        _h.StubJson(_h.ProjectApi + "/issues", new { message = "500 Internal Server Error" }, status: 500, match: r => r.WithParam("cursor", "abc"), priority: 1);

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.Transient);
    }

    [Fact]
    public async Task Pagination_loop_is_detected()
    {
        _h.StubStandardProject();
        var page = new[] { Fixtures.Issue(_h.WebBase, 800001, 1, "opened", GitLabTestHarness.Now) };
        _h.StubJson(_h.ProjectApi + "/issues", page, match: r => r.WithParam("state", "opened"), response: r => r.WithHeader("X-Next-Page", "1"), priority: 1);

        var result = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.Malformed);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        _h.Server.Given(Request.Create().UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{}").WithDelay(TimeSpan.FromSeconds(5)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var act = () => _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Missing_token_and_unavailable_store_are_classified()
    {
        _h.Credentials.Set(_h.CredentialReference, "x");
        var noToken = await _h.CreateProvider().FetchAsync(_h.Configuration() with { CredentialReference = "GitStatusWidget/none" }, GitLabTestHarness.FirstContext(), Ct);
        noToken.ShouldFail(ProviderFailureKind.AuthenticationFailed);

        _h.Credentials.IsAvailable = false;
        var noStore = await _h.CreateProvider().FetchAsync(_h.Configuration(), GitLabTestHarness.FirstContext(), Ct);
        noStore.ShouldFail(ProviderFailureKind.Configuration);
        _h.Server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Http_server_without_explicit_permission_is_configuration_error()
    {
        var result = await _h.CreateProvider().FetchAsync(_h.Configuration() with { AllowInsecureHttp = false }, GitLabTestHarness.FirstContext(), Ct);

        result.ShouldFail(ProviderFailureKind.Configuration);
        _h.Credentials.ReadCount.Should().Be(0, "the token is not loaded for an invalid configuration");
    }

    [Fact]
    public async Task Changed_configured_path_forces_re_resolution()
    {
        _h.StubStandardProject();
        var first = await FirstFetchAsync();
        var snapshot = Accept(null, first.Fetch);
        var stale = first.SyncState.WithValues((GitLabStatusProvider.ResolvedForKey, "https://elsewhere.invalid/|other/project"));
        ResetServer();
        _h.StubStandardProject();

        var context = new SnapshotRequestContext(snapshot, stale, RefreshScope.Builds, GitLabTestHarness.WindowStart);
        (await _h.CreateProvider().FetchAsync(_h.Configuration(), context, Ct)).ShouldSucceed(_h);

        _h.Server.LogEntries[0].RequestMessage!.Url.Should().EndWith("/projects/acme%2Ftools%2Fwidget");
    }

    [Fact]
    public async Task Metadata_refresh_counts_all_milestones_and_delta_counts_only_active_ones()
    {
        _h.StubStandardProject();
        _h.StubMilestones(Fixtures.Milestone(501, "v1.2"), Fixtures.Milestone(400, "v1.0", state: "closed"));
        var first = await FirstFetchAsync();
        first.Fetch.Counts!.ClosedIssuesByMilestone.Keys.Should().BeEquivalentTo(["501", "400"]);
        var snapshot = Accept(null, first.Fetch);

        ResetServer();
        _h.StubIssues("all", [Fixtures.Issue(_h.WebBase, 900002, 2, "closed", GitLabTestHarness.Now.AddMinutes(1))]);
        _h.StubMergeRequests("all", []);
        _h.StubCounts(open: 1, closed: 41, 8);

        var context = new SnapshotRequestContext(snapshot, first.SyncState, RefreshScope.WorkItems, GitLabTestHarness.WindowStart);
        var delta = (await _h.CreateProvider().FetchAsync(_h.Configuration(), context, Ct)).ShouldSucceed(_h);

        delta.Fetch.Counts!.ClosedIssuesByMilestone.Keys.Should().Equal("501");
        var next = Accept(snapshot, delta.Fetch);
        next.Milestones.Single(m => m.ExternalId == "400").ClosedIssueCount.Should().Be(snapshot.Milestones.Single(m => m.ExternalId == "400").ClosedIssueCount);
    }

    [Fact]
    public async Task Reconcile_returns_full_sets_and_recently_closed_merge_requests()
    {
        _h.StubStandardProject();
        var first = await FirstFetchAsync();
        var snapshot = Accept(null, first.Fetch);
        ResetServer();
        _h.StubStandardProject();
        _h.StubMergeRequests("all", [
            Fixtures.MergeRequest(_h.WebBase, 562002, 12, "merged", GitLabTestHarness.Now.AddHours(-1)),
            Fixtures.MergeRequest(_h.WebBase, 562003, 13, "closed", GitLabTestHarness.Now.AddHours(-1)),
            Fixtures.MergeRequest(_h.WebBase, 562001, 11, "opened", GitLabTestHarness.Now.AddMinutes(-1), draft: false),
            Fixtures.MergeRequest(_h.WebBase, 562004, 14, "locked", GitLabTestHarness.Now.AddMinutes(-1)),
        ]);

        var context = new SnapshotRequestContext(snapshot, first.SyncState, RefreshScope.Reconcile, GitLabTestHarness.WindowStart);
        var result = (await _h.CreateProvider().FetchAsync(_h.Configuration(), context, Ct)).ShouldSucceed(_h);

        var mrs = result.Fetch.ChangeRequests.Should().BeOfType<WorkItemUpdate<ChangeRequestSnapshot>.FullItems>().Subject;
        mrs.Open.Select(m => m.ExternalId).Should().BeEquivalentTo(["562001", "562004"], "locked is transitional and treated as open");
        mrs.Open.Single(m => m.ExternalId == "562001").IsDraft.Should().BeFalse("the newer copy wins");
        mrs.RecentlyClosed.Select(m => m.State).Should().BeEquivalentTo([ChangeRequestState.Merged, ChangeRequestState.Closed]);
        _h.Requests("/merge_requests").Single(e => e.RequestMessage!.Query!["state"].Single() == "all").RequestMessage!.Query!["updated_after"].Single()
            .Should().Be(Fixtures.Ts(GitLabTestHarness.WindowStart));
        result.Fetch.Labels.Should().Be(SectionUpdate<LabelSnapshot>.Unchanged, "reconciliation without metadata keeps labels");
        result.Fetch.Counts.Should().NotBeNull();
        Accept(snapshot, result.Fetch).ChangeRequests.Should().HaveCount(4);
    }
}
