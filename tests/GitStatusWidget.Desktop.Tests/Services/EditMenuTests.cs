using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using GitStatusWidget.Desktop.Services;

namespace GitStatusWidget.Desktop.Tests.Services;

/// <summary>Issue #1: Cmd+V etc. must work in text fields of the menu-bar agent app on macOS.</summary>
public sealed class EditMenuTests
{
    [AvaloniaFact]
    public void Menu_offers_all_edit_actions_with_command_key_gestures()
    {
        var bar = EditMenu.Create(() => null);

        var edit = bar.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Edit").Menu!;
        var items = edit.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToDictionary(i => i.Header!, i => i.Gesture);

        items.Keys.Should().BeEquivalentTo(["Undo", "Redo", "Cut", "Copy", "Paste", "Select All"]);
        items["Paste"].Should().Be(new KeyGesture(Key.V, KeyModifiers.Meta));
        items["Copy"].Should().Be(new KeyGesture(Key.C, KeyModifiers.Meta));
        items["Redo"].Should().Be(new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift));
    }

    [AvaloniaFact]
    public async Task Paste_goes_into_the_focused_text_box_also_for_password_fields()
    {
        var token = new TextBox { PasswordChar = '•' };
        var window = new Window { Content = token };
        window.Show();
        token.Focus();
        await window.Clipboard!.SetTextAsync("ghp_exampletoken");

        EditMenu.Execute(window, EditAction.Paste).Should().BeTrue();
        await WaitUntilAsync(() => token.Text == "ghp_exampletoken");

        token.Text.Should().Be("ghp_exampletoken");
        window.Close();
    }

    [AvaloniaFact]
    public void Select_all_and_undo_act_on_the_focused_text_box()
    {
        var box = new TextBox { Text = "group/project" };
        var window = new Window { Content = box };
        window.Show();
        box.Focus();

        EditMenu.Execute(window, EditAction.SelectAll).Should().BeTrue();

        box.SelectedText.Should().Be("group/project");
        window.Close();
    }

    [AvaloniaFact]
    public void Without_a_focused_text_box_nothing_happens()
    {
        var window = new Window { Content = new Button() };
        window.Show();

        EditMenu.Execute(window, EditAction.Paste).Should().BeFalse();
        EditMenu.Execute(null, EditAction.Paste).Should().BeFalse();
        window.Close();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
