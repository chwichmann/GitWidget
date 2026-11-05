using System.Reflection;

namespace GitStatusWidget.Desktop.Services;

/// <summary>Information about the running application.</summary>
public static class AppInfo
{
    /// <summary>Informational version without build metadata, e.g. <c>0.1.2</c> or <c>0.1.2-ci.57</c>.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
