using GitStatusWidget.Application.Comparison;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.ChangeRequests;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Issues;
using GitStatusWidget.Domain.Milestones;
using GitStatusWidget.Domain.Repositories;
using static GitStatusWidget.Application.Tests.TestData.Snapshots;

namespace GitStatusWidget.Application.Tests.Comparison;

public sealed class SnapshotComparerTests
{
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);
    private readonly SnapshotComparer _comparer = new();

    [Fact]
    public void Identical_snapshots_produce_no_events()
    {
        var issues = new[] { Issue(1), Issue(2, WorkItemState.Closed), Issue(3, labels: ["bug"]) };
        var previous = Snapshot(T0, issues, [Milestone("M1", 2, 1)]);
        var current = Snapshot(T1, issues, [Milestone("M1", 2, 1)]);

        _comparer.Compare(previous, current).Should().BeEmpty();
    }

    [Fact]
    public void New_open_issue_created_after_previous_snapshot_is_IssueCreated()
    {
        var previous = Snapshot(T0, [Issue(1)]);
        var current = Snapshot(T1, [Issue(1), Issue(2, createdAt: T0.AddSeconds(10))]);

        var events = _comparer.Compare(previous, current);

        events.Should().ContainSingle().Which.Should().Match<RepositoryChangeEvent>(e =>
            e.Type == ChangeEventType.IssueCreated && e.EntityNumber == 2 && e.Priority == EventPriority.Medium);
    }

    [Fact]
    public void Open_issue_becoming_closed_is_IssueClosed()
    {
        var previous = Snapshot(T0, [Issue(1)]);
        var current = Snapshot(T1, [Issue(1, WorkItemState.Closed, updatedAt: T0.AddSeconds(5))]);

        _comparer.Compare(previous, current).Should().ContainSingle()
            .Which.Type.Should().Be(ChangeEventType.IssueClosed);
    }

    [Fact]
    public void Closed_issue_becoming_open_is_IssueReopened()
    {
        var previous = Snapshot(T0, [Issue(1, WorkItemState.Closed, updatedAt: T0.AddDays(-1))]);
        var current = Snapshot(T1, [Issue(1, updatedAt: T0.AddSeconds(5))]);

        _comparer.Compare(previous, current).Should().ContainSingle()
            .Which.Type.Should().Be(ChangeEventType.IssueReopened);
    }

    [Fact]
    public void Old_open_issue_unknown_to_previous_snapshot_and_recently_updated_is_IssueReopened()
    {
        // Closed long ago (outside the closed window, so not in the previous snapshot), reopened now.
        var previous = Snapshot(T0, [Issue(1)]);
        var current = Snapshot(T1, [Issue(1), Issue(7, createdAt: T0.AddYears(-1), updatedAt: T0.AddSeconds(3))]);

        _comparer.Compare(previous, current).Should().ContainSingle()
            .Which.Should().Match<RepositoryChangeEvent>(e => e.Type == ChangeEventType.IssueReopened && e.EntityNumber == 7);
    }

    [Fact]
    public void Issue_created_and_closed_between_polls_yields_created_and_closed()
    {
        var previous = Snapshot(T0, []);
        var current = Snapshot(T1, [Issue(5, WorkItemState.Closed, createdAt: T0.AddSeconds(2), updatedAt: T0.AddSeconds(20))]);

        _comparer.Compare(previous, current).Select(e => e.Type).Should()
            .Equal(ChangeEventType.IssueCreated, ChangeEventType.IssueClosed);
    }

    [Fact]
    public void Old_closed_issue_entering_the_window_produces_no_event()
    {
        // e.g. someone commented on an old closed issue, so it is now "recently updated".
        var previous = Snapshot(T0, [Issue(1)]);
        var current = Snapshot(T1, [Issue(1), Issue(9, WorkItemState.Closed, createdAt: T0.AddYears(-2), updatedAt: T0.AddSeconds(5))]);

        _comparer.Compare(previous, current).Should().BeEmpty();
    }

    [Fact]
    public void Issues_disappearing_from_the_snapshot_produce_no_events()
    {
        // Guards against false mass-closure events (deleted, transferred or out-of-window issues).
        var previous = Snapshot(T0, Enumerable.Range(1, 50).Select(n => Issue(n)));
        var current = Snapshot(T1, [Issue(1)]);

        _comparer.Compare(previous, current).Should().BeEmpty();
    }

    [Fact]
    public void Title_label_milestone_and_assignee_changes_are_detected_as_low_priority()
    {
        var milestone = new MilestoneReference("M1", "1.0");
        var previous = Snapshot(T0, [Issue(1, title: "Old", labels: ["bug"], assignees: ["ann"])]);
        var current = Snapshot(T1, [Issue(1, updatedAt: T0.AddSeconds(1), title: "New", labels: ["bug", "ui"], milestone: milestone, assignees: ["bob"])]);

        var events = _comparer.Compare(previous, current);

        events.Select(e => e.Type).Should().BeEquivalentTo(
        [
            ChangeEventType.IssueTitleChanged,
            ChangeEventType.IssueLabelsChanged,
            ChangeEventType.IssueMilestoneChanged,
            ChangeEventType.IssueAssigneesChanged,
        ]);
        events.Should().OnlyContain(e => e.Priority == EventPriority.Low);
        events.Single(e => e.Type == ChangeEventType.IssueTitleChanged).Detail.Should().Be("Old");
    }

    [Fact]
    public void Label_order_does_not_matter()
    {
        var previous = Snapshot(T0, [Issue(1, labels: ["b", "a"])]);
        var current = Snapshot(T1, [Issue(1, labels: ["a", "b"])]);

        _comparer.Compare(previous, current).Should().BeEmpty();
    }

    [Fact]
    public void Milestone_events_are_detected()
    {
        var previous = Snapshot(T0, milestones: [Milestone("M1", 3, 1), Milestone("M2", 0, 5)]);
        var current = Snapshot(T1, milestones: [Milestone("M1", 2, 2), Milestone("M2", 0, 5, isClosed: true), Milestone("M3", 0, 0)]);

        _comparer.Compare(previous, current).Select(e => e.Type).Should().Equal(
            ChangeEventType.MilestoneCreated,
            ChangeEventType.MilestoneClosed,
            ChangeEventType.MilestoneProgressChanged);
    }

    [Fact]
    public void Change_request_merge_close_and_open_are_detected()
    {
        var previous = Snapshot(T0, changeRequests:
        [
            ChangeRequest(10, ChangeRequestState.Open, T0.AddDays(-1)),
            ChangeRequest(11, ChangeRequestState.Open, T0.AddDays(-1)),
        ]);
        var current = Snapshot(T1, changeRequests:
        [
            ChangeRequest(10, ChangeRequestState.Merged, T0.AddDays(-1), T0.AddSeconds(5)),
            ChangeRequest(11, ChangeRequestState.Closed, T0.AddDays(-1), T0.AddSeconds(5)),
            ChangeRequest(12, ChangeRequestState.Open, T0.AddSeconds(5)),
        ]);

        _comparer.Compare(previous, current).Select(e => (e.Type, e.EntityNumber)).Should().Equal(
            (ChangeEventType.ChangeRequestOpened, 12),
            (ChangeEventType.ChangeRequestClosed, 11),
            (ChangeEventType.ChangeRequestMerged, 10));
    }

    [Fact]
    public void Build_transitions_are_detected_and_failures_are_high_priority()
    {
        var previous = Snapshot(T0, builds: [Build("1", BuildState.Queued, T0.AddMinutes(-1)), Build("2", BuildState.Running, T0.AddMinutes(-5))]);
        var current = Snapshot(T1, builds:
        [
            Build("3", BuildState.Running, T0.AddSeconds(10)),
            Build("1", BuildState.Running, T0.AddMinutes(-1), T0.AddSeconds(5)),
            Build("2", BuildState.Failed, T0.AddMinutes(-5), T0.AddSeconds(20)),
        ]);

        var events = _comparer.Compare(previous, current);

        events.Count(e => e.Type == ChangeEventType.BuildStarted).Should().Be(2);
        events.Single(e => e.Type == ChangeEventType.BuildFailed).Priority.Should().Be(EventPriority.High);
    }

    [Fact]
    public void Old_completed_build_new_to_the_list_produces_no_event()
    {
        var previous = Snapshot(T0, builds: []);
        var current = Snapshot(T1, builds: [Build("1", BuildState.Failed, T0.AddDays(-3))]);

        _comparer.Compare(previous, current).Should().BeEmpty();
    }

    [Fact]
    public void Event_keys_are_deterministic()
    {
        var previous = Snapshot(T0, [Issue(1)]);
        var current = Snapshot(T1, [Issue(1, WorkItemState.Closed, updatedAt: T0.AddSeconds(5)), Issue(2, createdAt: T0.AddSeconds(1))]);

        var first = _comparer.Compare(previous, current);
        var second = new SnapshotComparer().Compare(previous, current);

        first.Select(e => e.Key).Should().Equal(second.Select(e => e.Key));
        first.Select(e => e.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Comparing_different_repositories_throws()
    {
        var previous = Snapshot(T0);
        var other = Snapshot(T1) with { Repository = new RepositoryIdentity(RepositoryProviderKind.GitLab, "gitlab.com", "g", "p", "42") };

        var act = () => _comparer.Compare(previous, other);

        act.Should().Throw<ArgumentException>();
    }
}
