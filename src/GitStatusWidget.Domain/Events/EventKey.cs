using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GitStatusWidget.Domain.Events;

/// <summary>
/// Builds deterministic event keys from
/// <c>RepositoryExternalId + EventType + EntityExternalId + RelevantState + UpdatedAt</c>.
/// </summary>
public static class EventKey
{
    /// <summary>
    /// Creates a stable, compact key (hex SHA-256 prefix) for an event.
    /// </summary>
    /// <param name="repositoryExternalId">Repository external id.</param>
    /// <param name="type">Event type.</param>
    /// <param name="entityExternalId">Entity external id.</param>
    /// <param name="relevantState">State fragment that distinguishes repeated events (for example the new title).</param>
    /// <param name="updatedAt">Entity update timestamp.</param>
    /// <returns>A 32 character lowercase hexadecimal key.</returns>
    public static string Create(
        string repositoryExternalId,
        ChangeEventType type,
        string entityExternalId,
        string relevantState,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(repositoryExternalId);
        ArgumentNullException.ThrowIfNull(entityExternalId);
        ArgumentNullException.ThrowIfNull(relevantState);

        var raw = string.Join(
            '\u001f',
            repositoryExternalId,
            type.ToString(),
            entityExternalId,
            relevantState,
            updatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
