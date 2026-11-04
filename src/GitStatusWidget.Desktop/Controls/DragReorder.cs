using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace GitStatusWidget.Desktop.Controls;

/// <summary>
/// Reorders the items of an <see cref="ItemsControl"/> by dragging them with the mouse. Items move live while the
/// pointer passes over other items; no system drag &amp; drop is involved, so it behaves the same on all platforms.
/// </summary>
/// <remarks>Lives as long as its list (handlers are attached to the list itself), so it needs no disposal.</remarks>
public sealed class DragReorder
{
    /// <summary>Pointer travel (DIP) before a press becomes a drag, so clicks and taps keep working.</summary>
    public const double Threshold = 6;

    private readonly ItemsControl _list;
    private readonly Action<int, int> _move;
    private readonly Action<int>? _dropped;
    private int _index = -1;
    private Point _start;
    private bool _dragging;

    /// <param name="list">The list whose items are reordered.</param>
    /// <param name="move">Moves the item at the first index to the second index in the bound collection.</param>
    /// <param name="dropped">Called with the final index when a drag ends.</param>
    public DragReorder(ItemsControl list, Action<int, int> move, Action<int>? dropped = null)
    {
        _list = list;
        _move = move;
        _dropped = dropped;
        _list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Raised when a drag starts.</summary>
    public event EventHandler? DragStarted;

    /// <summary>Whether a drag is in progress.</summary>
    public bool IsDragging => _dragging;

    /// <summary>True right after a drag ended, until the next press (lets tap handlers ignore the release).</summary>
    public bool JustDragged { get; private set; }

    /// <summary>Index of the item whose row contains <paramref name="y"/> (list coordinates), clamped to the list.</summary>
    internal int IndexAt(double y)
    {
        var count = _list.ItemCount;
        for (var i = 0; i < count; i++)
        {
            if (_list.ContainerFromIndex(i) is not { } container || container.TranslatePoint(default, _list) is not { } topLeft)
            {
                continue;
            }

            if (y < topLeft.Y + container.Bounds.Height)
            {
                return i;
            }
        }

        return count - 1;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        JustDragged = false;
        _index = -1;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed || e.Source is not Visual source)
        {
            return;
        }

        // Presses on buttons or text boxes inside an item keep their normal meaning.
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            return;
        }

        var container = source.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => _list.IndexFromContainer(c) >= 0);
        if (container is null)
        {
            return;
        }

        _index = _list.IndexFromContainer(container);
        _start = e.GetPosition(_list);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_index < 0 || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(_list);
        if (!_dragging)
        {
            if (Math.Abs(position.Y - _start.Y) < Threshold && Math.Abs(position.X - _start.X) < Threshold)
            {
                return;
            }

            _dragging = true;
            e.Pointer.Capture(_list);
            _list.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            DragStarted?.Invoke(this, EventArgs.Empty);
        }

        var target = IndexAt(position.Y);
        if (target >= 0 && target != _index)
        {
            _move(_index, target);
            _index = target;

            // Re-measure now so the next pointer move sees the new item positions (containers may be recreated).
            _list.UpdateLayout();
        }

        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            _index = -1;
            return;
        }

        e.Handled = true;
        e.Pointer.Capture(null);
        End();
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragging)
        {
            End();
        }
    }

    private void End()
    {
        var index = _index;
        _dragging = false;
        _index = -1;
        JustDragged = true;
        _list.Cursor = null;
        _dropped?.Invoke(index);
    }
}
