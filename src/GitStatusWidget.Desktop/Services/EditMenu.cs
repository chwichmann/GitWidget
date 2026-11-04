using Avalonia.Controls;
using Avalonia.Input;

namespace GitStatusWidget.Desktop.Services;

/// <summary>Text editing commands offered by the application Edit menu.</summary>
public enum EditAction
{
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    SelectAll,
}

/// <summary>
/// Native application Edit menu for macOS (issue #1). GitWidget runs as a menu-bar agent app without an
/// application menu; AppKit delivers Cmd+C/V/X/A/Z as menu key equivalents, so without an Edit menu these
/// shortcuts never reach text fields. The menu is not visible for agent apps, but its key equivalents work.
/// </summary>
public static class EditMenu
{
    /// <summary>Creates the Edit menu bar; <paramref name="activeTopLevel"/> returns the focused window.</summary>
    public static NativeMenu Create(Func<TopLevel?> activeTopLevel)
    {
        ArgumentNullException.ThrowIfNull(activeTopLevel);
        var edit = new NativeMenu();
        Add(edit, "Undo", "Cmd+Z", EditAction.Undo, activeTopLevel);
        Add(edit, "Redo", "Cmd+Shift+Z", EditAction.Redo, activeTopLevel);
        edit.Items.Add(new NativeMenuItemSeparator());
        Add(edit, "Cut", "Cmd+X", EditAction.Cut, activeTopLevel);
        Add(edit, "Copy", "Cmd+C", EditAction.Copy, activeTopLevel);
        Add(edit, "Paste", "Cmd+V", EditAction.Paste, activeTopLevel);
        Add(edit, "Select All", "Cmd+A", EditAction.SelectAll, activeTopLevel);

        var bar = new NativeMenu();
        bar.Items.Add(new NativeMenuItem("Edit") { Menu = edit });
        return bar;
    }

    /// <summary>Runs an edit action on the focused text box of <paramref name="topLevel"/>.</summary>
    /// <returns><see langword="true"/> when a text box handled it.</returns>
    public static bool Execute(TopLevel? topLevel, EditAction action)
    {
        if (topLevel?.FocusManager?.GetFocusedElement() is not TextBox textBox)
        {
            return false;
        }

        switch (action)
        {
            case EditAction.Undo:
                textBox.Undo();
                break;
            case EditAction.Redo:
                textBox.Redo();
                break;
            case EditAction.Cut:
                textBox.Cut();
                break;
            case EditAction.Copy:
                textBox.Copy();
                break;
            case EditAction.Paste:
                textBox.Paste();
                break;
            case EditAction.SelectAll:
                textBox.SelectAll();
                break;
            default:
                return false;
        }

        return true;
    }

    private static void Add(NativeMenu menu, string header, string gesture, EditAction action, Func<TopLevel?> activeTopLevel)
    {
        var item = new NativeMenuItem(header) { Gesture = KeyGesture.Parse(gesture) };
        item.Click += (_, _) => Execute(activeTopLevel(), action);
        menu.Items.Add(item);
    }
}
