using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Labels;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Snapshots;
using static GitStatusWidget.Application.Tests.TestData.Snapshots;

namespace GitStatusWidget.Application.Tests.Synchronization;

public sealed class SnapshotAssemblerTests
{
    private static readonly DateTimeOffset Now = T0.AddMinutes(1);
    private static readonly DateTimeOffset WindowStart = Now.AddDays(-14);
    private static readonly MilestoneReference M1 = new("M1", "1.0");

    private static ProviderFetch Fetch(
        WorkItemUpdate<IssueSnapshot>? issues = null,
        WorkItemUpdate<ChangeRequestSnapshot>? changeRequests = null,
        SectionUpdate<MilestoneSnapshot>? milestones = null,
        SectionUpdate<LabelSnapshot>? labels = null,
        SectionUpdate<BuildSnapshot>? builds = null,
        CountsUpdate? counts = null) =>
        new(Repo, "main", RepositoryFeatures.All,
            issues ?? WorkItemUpdate<IssueSnapshot>.Unchanged,
            changeRequests ?? WorkItemUpdate<ChangeRequestSnapshot>.Unchanged,
            milestones ?? SectionUpdate<MilestoneSnapshot>.Unchanged,
            labels ?? SectionUpdate<LabelSnapshot>.Unchanged,
            builds ?? SectionUpdate<BuildSnapshot>.Unchanged,
            counts);

    private static ProviderFetch FullFetch(IReadOnlyList<IssueSnapshot> open, IReadOnlyList<IssueSnapshot>? closed = null, CountsUpdate? counts = null) =>
        Fetch(
            WorkItemUpdate<IssueSnapshot>.Full(open, closed ?? [], WindowStart),
            WorkItemUpdate<ChangeRequestSnapshot>.Full([], [], WindowStart),
            SectionUpdate<MilestoneSnapshot>.Replace([Milestone("M1", 0, 0)]),
            SectionUpdate<LabelSnapshot>.Replace([new LabelSnapshot("bug", "#d73a4a", null)]),
            SectionUpdate<BuildSnapshot>.Replace([]),
            counts ?? new CountsUpdate(open.Count, 100, new Dictionary<string, int> { ["M1"] = 7 }));

    private static RepositorySnapshot Accept(AssemblyResult result) =>
        result.Should().BeOfType<AssemblyResult.Accepted>().Subject.Snapshot;

    [Fact]
    public void First_snapshot_requires_full_sections_and_counts()
    {
        var result = SnapshotAssembler.Assemble(null, Fetch(WorkItemUpdate<IssueSnapshot>.Delta([Issue(1)])), Now, WindowStart);

        result.Should().BeOfType<AssemblyResult.Rejected>();
    }

    [Fact]
    public void First_full_snapshot_computes_open_counts_locally_and_closed_counts_from_source()
    {
        var open = new[] { Issue(1, milestone: M1, labels: ["bug"]), Issue(2, labels: ["bug", "ui"]), Issue(3) };

        var snapshot = Accept(SnapshotAssembler.Assemble(null, FullFetch(open), Now, WindowStart));

        snapshot.IssueCounts.Should().Be(new IssueCounts(3, 100));
        snapshot.Milestones.Single().Should().Match<MilestoneSnapshot>(m => m.OpenIssueCount == 1 && m.ClosedIssueCount == 7);
        snapshot.Labels.Single(l => l.Name == "bug").OpenIssueCount.Should().Be(2);
    }

    [Fact]
    public void Delta_upserts_and_never_removes_items()
    {
        var previous = Accept(SnapshotAssembler.Assemble(null, FullFetch([Issue(1), Issue(2)]), T0, WindowStart));
        var closedNow = Issue(2, WorkItemState.Closed, updatedAt: Now.AddSeconds(-10));
        var created = Issue(3, createdAt: Now.AddSeconds(-5));

        var snapshot = Accept(SnapshotAssembler.Assemble(
            previous,
            Fetch(WorkItemUpdate<IssueSnapshot>.Delta([closedNow, created]), counts: new CountsUpdate(2, 101, new Dictionary<string, int>())),
            Now,
            WindowStart));

        snapshot.Issues.Select(i => (i.Number, i.State)).Should().BeEquivalentTo(
            [(1, WorkItemState.Open), (2, WorkItemState.Closed), (3, WorkItemState.Open)]);
        snapshot.IssueCounts.Should().Be(new IssueCounts(2, 101));
    }

    [Fact]
    public void Delta_keeps_the_newest_version_of_an_item()
    {
        var previous = Accept(SnapshotAssembler.Assemble(null, FullFetch([Issue(1, updatedAt: Now.AddSeconds(-1), title: "new")]), T0, WindowStart));

        var snapshot = Accept(SnapshotAssembler.Assemble(
            previous,
            Fetch(WorkItemUpdate<IssueSnapshot>.Delta([Issue(1, updatedAt: Now.AddMinutes(-10), title: "stale")])),
            Now,
            WindowStart));

        snapshot.Issues.Single().Title.Should().Be("new");
    }

    [Fact]
    public void Full_update_drops_open_items_that_vanished_but_keeps_recently_closed()
    {
        var recentlyClosed = Issue(5, WorkItemState.Closed, updatedAt: Now.AddDays(-1));
        var previous = Accept(SnapshotAssembler.Assemble(null, FullFetch([Issue(1), Issue(2)], [recentlyClosed]), T0, WindowStart));

        var snapshot = Accept(SnapshotAssembler.Assemble(previous, FullFetch([Issue(1)]), Now, WindowStart));

        snapshot.Issues.Select(i => i.Number).Should().BeEquivalentTo([1, 5]);
    }

    [Fact]
    public void Closed_items_outside_the_window_are_pruned()
    {
        var previous = Accept(SnapshotAssembler.Assemble(null, FullFetch([Issue(1)], [Issue(9, WorkItemState.Closed, updatedAt: Now.AddDays(-3))]), T0, WindowStart));

        var snapshot = Accept(SnapshotAssembler.Assemble(previous, Fetch(), Now, Now.AddDays(-2)));

        snapshot.Issues.Select(i => i.Number).Should().Equal(1);
        snapshot.ClosedIssuesSince.Should().Be(Now.AddDays(-2));
    }

    [Fact]
    public void Full_update_that_disagrees_substantially_with_counts_is_rejected()
    {
        // Simulates a provider returning an incomplete open set (e.g. a lost page): must not be accepted.
        var open = Enumerable.Range(1, 10).Select(n => Issue(n)).ToList();

        var result = SnapshotAssembler.Assemble(null, FullFetch(open, counts: new CountsUpdate(318, 1134, new Dictionary<string, int>())), Now, WindowStart);

        result.Should().BeOfType<AssemblyResult.Rejected>();
    }

    [Fact]
    public void Small_drift_after_full_update_is_accepted_without_reconciliation_request()
    {
        var open = Enumerable.Range(1, 100).Select(n => Issue(n)).ToList();

        var result = SnapshotAssembler.Assemble(null, FullFetch(open, counts: new CountsUpdate(102, 5, new Dictionary<string, int>())), Now, WindowStart);

        result.Should().BeOfType<AssemblyResult.Accepted>().Which.ReconciliationRecommended.Should().BeFalse();
    }

    [Fact]
    public void Count_drift_after_delta_recommends_reconciliation()
    {
        var previous = Accept(SnapshotAssembler.Assemble(null, FullFetch([Issue(1), Issue(2)]), T0, WindowStart));

        var result = SnapshotAssembler.Assemble(
            previous,
            Fetch(WorkItemUpdate<IssueSnapshot>.Delta([]), counts: new CountsUpdate(1, 100, new Dictionary<string, int>())),
            Now,
            WindowStart);

        result.Should().BeOfType<AssemblyResult.Accepted>().Which.ReconciliationRecommended.Should().BeTrue();
    }

    [Fact]
    public void Unchanged_sections_and_missing_counts_carry_previous_values_forward()
    {
        var previous = Accept(SnapshotAssembler.Assemble(null, FullFetch([Issue(1, milestone: M1)]), T0, WindowStart));

        var snapshot = Accept(SnapshotAssembler.Assemble(previous, Fetch(), Now, WindowStart));

        snapshot.IssueCounts.Should().Be(previous.IssueCounts);
        snapshot.Milestones.Single().ClosedIssueCount.Should().Be(7);
        snapshot.Labels.Should().HaveCount(1);
        snapshot.RetrievedAt.Should().Be(Now);
    }

    [Fact]
    public void Change_requests_are_merged_like_issues_and_never_counted_as_issues()
    {
        var previous = Accept(SnapshotAssembler.Assemble(null, FullFetch([Issue(1)]), T0, WindowStart));

        var snapshot = Accept(SnapshotAssembler.Assemble(
            previous,
            Fetch(changeRequests: WorkItemUpdate<ChangeRequestSnapshot>.Delta([ChangeRequest(10, ChangeRequestState.Open, Now.AddSeconds(-3))])),
            Now,
            WindowStart));

        snapshot.ChangeRequests.Should().ContainSingle();
        snapshot.IssueCounts.Open.Should().Be(1);
    }

    [Fact]
    public void Builds_are_ordered_newest_first_and_capped()
    {
        var previous = Accept(SnapshotAssembler.Assemble(null, FullFetch([]), T0, WindowStart));
        var builds = Enumerable.Range(1, 30).Select(n => Build(n.ToString(System.Globalization.CultureInfo.InvariantCulture), BuildState.Succeeded, T0.AddMinutes(n))).ToList();

        var snapshot = Accept(SnapshotAssembler.Assemble(previous, Fetch(builds: SectionUpdate<BuildSnapshot>.Replace(builds)), Now, WindowStart));

        snapshot.Builds.Should().HaveCount(SnapshotAssembler.MaxBuilds);
        snapshot.Builds[0].ExternalId.Should().Be("30");
    }
}
