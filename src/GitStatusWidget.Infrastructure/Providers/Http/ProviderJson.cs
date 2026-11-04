using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitStatusWidget.Infrastructure.Providers.Http;

/// <summary>
/// JSON options for provider DTOs: snake_case, tolerant of unknown properties, no polymorphic type metadata
/// (architecture §23.4).
/// </summary>
internal static class ProviderJson
{
    public static JsonSerializerOptions SnakeCase { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static JsonSerializerOptions CamelCase { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
}
