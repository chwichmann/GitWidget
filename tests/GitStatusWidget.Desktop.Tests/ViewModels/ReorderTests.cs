using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using GitStatusWidget.Application.Platform;
using GitStatusWidget.Application.Projection;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Desktop.Controls;
using GitStatusWidget.Desktop.Services;
using GitStatusWidget.Desktop.Tests.TestData;
using GitStatusWidget.Desktop.Tests.TestDoubles;
using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Health;
using Microsoft.Extensions.Time.Testing;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

/// <summary>Reordering repositories: Settings buttons and drag &amp; drop, widget and dashboard drag &amp; drop.</summary>
public sealed class ReorderTests
{
    private readonly FakeTimeProvider _time = new(Data.T0);
    private readonly RepositoryStatusHub _hub = new();
    private readonly InlineUiDispatcher _ui = new();
    private readonly RepositoryConfiguration _a = Data.Config(name: "a", sortOrder: 0);
    private readonly RepositoryConfiguration _b = Data.Config(name: "b", sortOrder: 1);
    private readonly RepositoryConfiguration _c = Data.Config(name: "c", sortOrder: 2);

    private (FakeSynchronizationController Controller, FakeSettingsStore Store, SettingsService Service) Settings(params RepositoryConfiguration[] repositories)
    {
        var controller = new FakeSynchronizationController(new AppSettings { Repositories = repositories });
        var store = new FakeSettingsStore();
        return (controller, store, new SettingsService(store, controller, new FakeAutostartService()));
    }

    private void PublishAll(params RepositoryConfiguration[] repositories)
    {
        foreach (var repository in repositories)
        {
            _hub.Publish(Data.State(repository, Data.Snapshot(repository)));
        }
    }

    [Fact]
    public void Reorder_keeps_unlisted_repositories_in_their_slots()
    {
        var disabled = Data.Config(name: "disabled", sortOrder: 1) with { IsEnabled = false };
        var b = _b with { SortOrder = 2 };
        var c = _c with { SortOrder = 3 };

        var result = SettingsService.Reorder([_a, disabled, b, c], [c.Id, _a.Id, b.Id]);

        result.Select(r => r.RepositoryName).Should().Equal("c", "disabled", "a", "b");
        result.Select(r => r.SortOrder).Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public void Settings_move_buttons_keep_the_moved_repository_selected()
    {
        var (_, _, service) = Settings(_a, _b, _c);
        var vm = new SettingsViewModel(service, new FakeCredentialStore(), new CredentialStoreStatus("Test", true, null),
            new FakeProviderResolver(), new FakeRepositoryCache(), _hub);
        var moved = vm.Repositories[0];
        vm.Selected = moved;

        vm.MoveDownCommand.Execute(null);
        vm.MoveDownCommand.Execute(null);

        vm.Repositories.Select(r => r.RepositoryPath).Should().Equal("octo/b", "octo/c", "octo/a");
        vm.Selected.Should().BeSameAs(moved);

        vm.MoveTo(2, 0);
        vm.Selected.Should().BeSameAs(moved);
    }

    [Fact]
    public async Task Widget_drag_reorder_is_not_disturbed_by_refreshes_and_is_saved_on_drop()
    {
        var (controller, store, service) = Settings(_a, _b, _c);
        PublishAll(_a, _b, _c);
        using var vm = new WidgetViewModel(_hub, new DashboardProjector(_time), controller, _ui, new FakeNavigator(), _time, service);

        vm.BeginReorder();
        vm.MoveRow(0, 2);
        _hub.Publish(Data.State(_a, Data.Snapshot(_a), RepositoryHealthState.Stale)); // background refresh mid-drag
        vm.Rows.Select(r => r.DisplayName).Should().Equal("octo/b", "octo/c", "octo/a");

        await vm.CommitReorderAsync();

        var saved = store.Saved.Single().Repositories;
        saved.OrderBy(r => r.SortOrder).Select(r => r.RepositoryName).Should().Equal("b", "c", "a");
        controller.Applied.Should().ContainSingle();

        // The engine republishes the configurations with the new order: rows stay as dropped.
        PublishAll([.. saved]);
        vm.Rows.Select(r => r.DisplayName).Should().Equal("octo/b", "octo/c", "octo/a");
    }

    [Fact]
    public async Task Dashboard_selector_follows_the_saved_order_and_keeps_its_selection_while_dragging()
    {
        var (controller, store, service) = Settings(_a, _b, _c);
        PublishAll(_c, _a, _b); // published out of order
        var feed = new ActivityFeed(_hub, _ui);
        using var vm = new DashboardViewModel(_hub, new DashboardProjector(_time), controller, _ui, new FakeNavigator(), feed, _time, service);
        vm.Repositories.Select(r => r.Name).Should().Equal("octo/a", "octo/b", "octo/c");

        var selected = vm.Repositories[0];
        vm.SelectedRepository = selected;
        vm.BeginReorder();
        vm.MoveRepository(0, 2);

        vm.SelectedRepository.Should().BeSameAs(selected);
        vm.Projection.Should().NotBeNull();
        await vm.CommitReorderAsync();
        store.Saved.Single().Repositories.OrderBy(r => r.SortOrder).Select(r => r.RepositoryName).Should().Equal("b", "c", "a");
    }

    [AvaloniaFact]
    public void Dragging_an_item_with_the_mouse_moves_it_and_reports_the_drop()
    {
        var items = new ObservableCollection<string>(["a", "b", "c"]);
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<string>((text, _) => new Border { Height = 30, Background = Avalonia.Media.Brushes.White, Child = new TextBlock { Text = text } }),
        };
        var window = new Window { Width = 200, Height = 200, Content = list };
        window.Show();
        int? dropped = null;
        var reorder = new DragReorder(list, items.Move, index => dropped = index);

        window.MouseDown(new Point(20, 15), MouseButton.Left);
        window.MouseMove(new Point(20, 30), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(20, 75), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(20, 75), MouseButton.Left);

        items.Should().Equal("b", "c", "a");
        dropped.Should().Be(2);
        reorder.JustDragged.Should().BeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public void A_click_without_movement_is_not_a_drag()
    {
        var items = new ObservableCollection<string>(["a", "b"]);
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<string>((text, _) => new Border { Height = 30, Background = Avalonia.Media.Brushes.White, Child = new TextBlock { Text = text } }),
        };
        var window = new Window { Width = 200, Height = 200, Content = list };
        window.Show();
        var reorder = new DragReorder(list, items.Move);

        window.MouseDown(new Point(20, 15), MouseButton.Left);
        window.MouseMove(new Point(22, 17), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(22, 17), MouseButton.Left);

        items.Should().Equal("a", "b");
        reorder.JustDragged.Should().BeFalse();
        window.Close();
    }
}
