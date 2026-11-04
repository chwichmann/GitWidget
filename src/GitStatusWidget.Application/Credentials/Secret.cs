namespace GitStatusWidget.Application.Credentials;

/// <summary>
/// Wraps a secret value so it is never printed by accident (<see cref="ToString"/> is redacted,
/// and the type is not serializable by design).
/// </summary>
public sealed class Secret
{
    private readonly string _value;

    /// <summary>Creates a secret.</summary>
    /// <param name="value">The secret value; must not be empty.</param>
    public Secret(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value.Trim();
    }

    /// <summary>Returns the raw value. Use only to build an outgoing request header.</summary>
    /// <returns>The secret value.</returns>
    public string Reveal() => _value;

    /// <inheritdoc />
    public override string ToString() => "***";
}
