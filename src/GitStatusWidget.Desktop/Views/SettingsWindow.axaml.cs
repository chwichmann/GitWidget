using Avalonia.Controls;
using GitStatusWidget.Desktop.Controls;
using GitStatusWidget.Desktop.ViewModels;

namespace GitStatusWidget.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly DragReorder _reorder;
    private readonly DragReorder _milestoneReorder;

    public SettingsWindow()
    {
        InitializeComponent();

        // The order is saved together with the other settings (Save).
        _reorder = new DragReorder(RepositoryList, (from, to) => (DataContext as SettingsViewModel)?.MoveTo(from, to));
        _milestoneReorder = new DragReorder(MilestoneList, (from, to) => (DataContext as SettingsViewModel)?.Selected?.MoveMilestone(from, to));
    }
}
