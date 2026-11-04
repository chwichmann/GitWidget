using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestData;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Events;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.Tests.Services;

public sealed class ToastServiceTests
{
    private static readonly Guid RepositoryId = Guid.NewGuid();

    private static RepositoryChangesDetected Changes(bool catchUp, params RepositoryChangeEvent[] events) => new(RepositoryId, events, catchUp);

    [Fact]
    public void Catch_up_changes_never_produce_toasts()
    {
        var changes = Changes(true, Data.Event(ChangeEventType.BuildFailed, EventPriority.High));

        ToastService.Select(changes, "repo", enabled: true, EventPriority.Low).Should().BeEmpty();
    }

    [Fact]
    public void Disabled_toasts_produce_nothing()
    {
        var changes = Changes(false, Data.Event(ChangeEventType.BuildFailed, EventPriority.High));

        ToastService.Select(changes, "repo", enabled: false, EventPriority.Low).Should().BeEmpty();
    }

    [Fact]
    public void Events_below_the_minimum_priority_are_filtered()
    {
        var changes = Changes(false,
            Data.Event(ChangeEventType.IssueLabelsChanged, EventPriority.Low, number: 1),
            Data.Event(ChangeEventType.IssueClosed, EventPriority.Medium, number: 2),
            Data.Event(ChangeEventType.BuildFailed, EventPriority.High, number: 3));

        ToastService.Select(changes, "repo", true, EventPriority.High).Select(t => t.Headline).Should().Equal("Workflow run failed");
        ToastService.Select(changes, "repo", true, EventPriority.Medium).Should().HaveCount(2);
        ToastService.Select(changes, "repo", true, EventPriority.Low).Should().HaveCount(3);
    }

    [Fact]
    public void Only_low_priority_events_produce_no_toast_by_default()
    {
        var changes = Changes(false, Data.Event(ChangeEventType.IssueTitleChanged, EventPriority.Low));

        ToastService.Select(changes, "repo", true, new AppSettings().MinimumToastPriority).Should().BeEmpty();
    }

    [Fact]
    public void Toasts_are_ordered_by_priority_and_carry_content()
    {
        var changes = Changes(false,
            Data.Event(ChangeEventType.IssueClosed, EventPriority.Medium, number: 7, title: "Fix login"),
            Data.Event(ChangeEventType.BuildFailed, EventPriority.High, number: 3, title: "CI", detail: "main"));

        var toasts = ToastService.Select(changes, "octo/widget", true, EventPriority.Medium);

        toasts.Select(t => t.Headline).Should().Equal("Workflow run failed on main", "Issue #7 closed");
        toasts[0].Kind.Should().Be(ToastKind.Failure);
        toasts[0].Glyph.Should().Be("✕");
        toasts[1].Kind.Should().Be(ToastKind.Success);
        toasts[1].Title.Should().Be("Fix login");
        toasts[1].RepositoryName.Should().Be("octo/widget");
        toasts[1].Url.Should().Be(new Uri("https://github.com/octo/widget/issues/7"));
    }

    [Fact]
    public void Three_events_are_shown_individually()
    {
        var changes = Changes(false, Enumerable.Range(1, ToastService.BurstThreshold - 1)
            .Select(n => Data.Event(ChangeEventType.IssueCreated, EventPriority.Medium, number: n)).ToArray());

        ToastService.Select(changes, "repo", true, EventPriority.Medium).Should().HaveCount(3);
    }

    [Fact]
    public void Bursts_are_coalesced_into_one_summary()
    {
        var changes = Changes(false,
            Data.Event(ChangeEventType.IssueCreated, EventPriority.Medium, number: 1),
            Data.Event(ChangeEventType.IssueCreated, EventPriority.Medium, number: 2),
            Data.Event(ChangeEventType.IssueClosed, EventPriority.Medium, number: 3),
            Data.Event(ChangeEventType.IssueClosed, EventPriority.Medium, number: 4),
            Data.Event(ChangeEventType.IssueLabelsChanged, EventPriority.Low, number: 5, detail: "bug"));

        var toast = ToastService.Select(changes, "repo", true, EventPriority.Medium).Should().ContainSingle().Subject;

        toast.Headline.Should().Be("4 changes", "low-priority events below the threshold are not counted");
        toast.Title.Should().Be("Issue #1 opened · Issue #2 opened · Issue #3 closed");
        toast.Kind.Should().Be(ToastKind.Info);
        toast.Url.Should().BeNull();
    }

    [Fact]
    public void Burst_with_a_failed_build_is_a_failure_summary()
    {
        var changes = Changes(false,
            Data.Event(ChangeEventType.IssueCreated, EventPriority.Medium, number: 1),
            Data.Event(ChangeEventType.IssueCreated, EventPriority.Medium, number: 2),
            Data.Event(ChangeEventType.IssueCreated, EventPriority.Medium, number: 3),
            Data.Event(ChangeEventType.BuildFailed, EventPriority.High, number: 4));

        var toast = ToastService.Select(changes, "repo", true, EventPriority.Medium).Should().ContainSingle().Subject;

        toast.Kind.Should().Be(ToastKind.Failure);
    }

    [Theory]
    [InlineData(ChangeEventType.IssueClosed, ToastKind.Success)]
    [InlineData(ChangeEventType.ChangeRequestMerged, ToastKind.Success)]
    [InlineData(ChangeEventType.BuildSucceeded, ToastKind.Success)]
    [InlineData(ChangeEventType.MilestoneClosed, ToastKind.Success)]
    [InlineData(ChangeEventType.BuildFailed, ToastKind.Failure)]
    [InlineData(ChangeEventType.BuildCanceled, ToastKind.Warning)]
    [InlineData(ChangeEventType.IssueCreated, ToastKind.Info)]
    [InlineData(ChangeEventType.ChangeRequestOpened, ToastKind.Info)]
    public void Toast_kind_follows_the_event_type(ChangeEventType type, ToastKind kind) =>
        ToastViewModel.KindOf(type).Should().Be(kind);

    [Fact]
    public void Every_toast_kind_has_a_glyph()
    {
        // State is never conveyed by color alone (architecture §17).
        foreach (var kind in Enum.GetValues<ToastKind>())
        {
            new ToastViewModel("r", "h", "t", kind, null).Glyph.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Gitlab_wording_is_used_for_gitlab_events()
    {
        var changes = Changes(false, Data.Event(ChangeEventType.ChangeRequestMerged, EventPriority.Medium, number: 9, provider: RepositoryProviderKind.GitLab));

        ToastService.Select(changes, "repo", true, EventPriority.Medium).Single().Headline.Should().Be("Merge request !9 merged");
    }

    [Fact]
    public void Live_changes_from_the_hub_are_presented_with_the_repository_name_and_settings()
    {
        var hub = new RepositoryStatusHub();
        var configuration = Data.Config(id: RepositoryId, displayName: "Widget");
        hub.Publish(Data.State(configuration));
        var controller = new FakeSynchronizationController(new AppSettings { MinimumToastPriority = EventPriority.High });
        var presenter = new RecordingToastPresenter();
        var ui = new InlineUiDispatcher();
        using var service = new ToastService(hub, controller, ui, () => presenter);

        hub.PublishChanges(Changes(false,
            Data.Event(ChangeEventType.IssueClosed, EventPriority.Medium),
            Data.Event(ChangeEventType.BuildFailed, EventPriority.High)));
        hub.PublishChanges(Changes(true, Data.Event(ChangeEventType.BuildFailed, EventPriority.High, number: 2)));

        presenter.Shown.Should().ContainSingle();
        presenter.Shown[0].RepositoryName.Should().Be("Widget");
        presenter.Shown[0].Kind.Should().Be(ToastKind.Failure);
    }

    [Fact]
    public void Nothing_is_posted_to_the_ui_when_toasts_are_disabled()
    {
        var hub = new RepositoryStatusHub();
        var presenter = new RecordingToastPresenter();
        var ui = new InlineUiDispatcher();
        using var service = new ToastService(hub, new FakeSynchronizationController(new AppSettings { ToastsEnabled = false }), ui, () => presenter);

        hub.PublishChanges(Changes(false, Data.Event(ChangeEventType.BuildFailed, EventPriority.High)));

        ui.PostCount.Should().Be(0);
        presenter.Shown.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_unsubscribes_from_the_hub()
    {
        var hub = new RepositoryStatusHub();
        var presenter = new RecordingToastPresenter();
        var service = new ToastService(hub, new FakeSynchronizationController(), new InlineUiDispatcher(), () => presenter);

        service.Dispose();
        hub.PublishChanges(Changes(false, Data.Event(ChangeEventType.BuildFailed, EventPriority.High)));

        presenter.Shown.Should().BeEmpty();
    }
}
