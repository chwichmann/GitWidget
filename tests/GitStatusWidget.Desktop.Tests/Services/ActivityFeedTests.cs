using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestData;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Domain.Events;

namespace GitStatusWidget.Desktop.Tests.Services;

public sealed class ActivityFeedTests
{
    [Fact]
    public void Entries_are_newest_first_including_catch_up_changes()
    {
        var hub = new RepositoryStatusHub();
        var configuration = Data.Config(displayName: "Widget");
        hub.Publish(Data.State(configuration));
        using var feed = new ActivityFeed(hub, new InlineUiDispatcher());

        hub.PublishChanges(new RepositoryChangesDetected(configuration.Id,
        [
            Data.Event(ChangeEventType.IssueClosed, number: 2, occurredAt: Data.T0.AddMinutes(2)),
            Data.Event(ChangeEventType.IssueCreated, number: 1, occurredAt: Data.T0.AddMinutes(1)),
        ], IsCatchUp: true));

        feed.Items.Select(i => i.Headline).Should().Equal("Issue #2 closed", "Issue #1 opened");
        feed.Items.Should().OnlyContain(i => i.RepositoryName == "Widget" && i.RepositoryId == configuration.Id);
    }

    [Fact]
    public void Feed_is_bounded()
    {
        var hub = new RepositoryStatusHub();
        using var feed = new ActivityFeed(hub, new InlineUiDispatcher());
        var events = Enumerable.Range(1, ActivityFeed.Capacity + 25)
            .Select(n => Data.Event(ChangeEventType.IssueCreated, number: n, occurredAt: Data.T0.AddSeconds(n)))
            .ToArray();

        hub.PublishChanges(new RepositoryChangesDetected(Guid.NewGuid(), events, IsCatchUp: false));

        feed.Items.Should().HaveCount(ActivityFeed.Capacity);
        feed.Items[0].Event.EntityNumber.Should().Be(ActivityFeed.Capacity + 25);
        feed.Items[0].RepositoryName.Should().BeEmpty("unknown repositories have no name");
    }

    [Fact]
    public void Dispose_unsubscribes()
    {
        var hub = new RepositoryStatusHub();
        var feed = new ActivityFeed(hub, new InlineUiDispatcher());
        feed.Dispose();

        hub.PublishChanges(new RepositoryChangesDetected(Guid.NewGuid(), [Data.Event(ChangeEventType.IssueCreated)], IsCatchUp: false));

        feed.Items.Should().BeEmpty();
    }
}
