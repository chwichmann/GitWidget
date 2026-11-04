namespace GitStatusWidget.Application.Notifications;

/// <summary>
/// Bounded, insertion-ordered set of recently published event keys (architecture §14.2). Prevents duplicate
/// animations after a restart or a repeated provider response. Not thread-safe; owned by one repository.
/// </summary>
public sealed class EventLedger
{
    /// <summary>Default capacity per repository.</summary>
    public const int DefaultCapacity = 500;

    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>Creates a ledger.</summary>
    /// <param name="capacity">Maximum number of keys retained.</param>
    /// <param name="keys">Initial keys, oldest first.</param>
    public EventLedger(int capacity = DefaultCapacity, IEnumerable<string>? keys = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        foreach (var key in keys ?? [])
        {
            TryAdd(key);
        }
    }

    /// <summary>Maximum number of keys.</summary>
    public int Capacity { get; }

    /// <summary>Number of keys currently retained.</summary>
    public int Count => _keys.Count;

    /// <summary>Keys, oldest first.</summary>
    public IReadOnlyList<string> Keys => [.. _order];

    /// <summary>Whether the key was already recorded.</summary>
    /// <param name="key">Event key.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool Contains(string key) => _keys.Contains(key);

    /// <summary>Records a key; evicts the oldest key when full.</summary>
    /// <param name="key">Event key.</param>
    /// <returns><see langword="true"/> when the key was new.</returns>
    public bool TryAdd(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!_keys.Add(key))
        {
            return false;
        }

        _order.AddLast(key);
        while (_order.Count > Capacity)
        {
            _keys.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }

        return true;
    }
}
