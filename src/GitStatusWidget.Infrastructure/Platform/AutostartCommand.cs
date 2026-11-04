using System.Reflection;

namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>The program and arguments a start-at-login entry runs.</summary>
/// <param name="Executable">Absolute path of the program to start.</param>
/// <param name="Arguments">Arguments, already split (never shell-parsed).</param>
internal sealed record AutostartCommand(string Executable, IReadOnlyList<string> Arguments)
{
    /// <summary>Executable followed by the arguments.</summary>
    public IEnumerable<string> AllArguments => [Executable, .. Arguments];

    /// <summary>
    /// Resolves the command for the running process: the published executable, or the
    /// <c>dotnet</c> host plus the entry assembly when started via <c>dotnet run</c> / <c>dotnet GitWidget.dll</c>.
    /// </summary>
    /// <returns>The command, or <see langword="null"/> when the executable path cannot be determined.</returns>
    public static AutostartCommand? ResolveCurrent()
    {
        string? entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        string? entryDll = entryName is null ? null : Path.Combine(AppContext.BaseDirectory, entryName + ".dll");
        return Resolve(Environment.ProcessPath, entryDll, File.Exists);
    }

    /// <summary>Resolution rule, separated for tests.</summary>
    /// <param name="processPath">Path of the running executable.</param>
    /// <param name="entryAssemblyPath">Path of the entry assembly (<c>.dll</c>), if known.</param>
    /// <param name="fileExists">File existence check.</param>
    /// <returns>The command, or <see langword="null"/>.</returns>
    internal static AutostartCommand? Resolve(string? processPath, string? entryAssemblyPath, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
        {
            return null;
        }

        bool isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (!isDotnetHost)
        {
            return new AutostartCommand(processPath, [PlatformIdentity.AutostartArgument]);
        }

        if (string.IsNullOrWhiteSpace(entryAssemblyPath) || !Path.IsPathFullyQualified(entryAssemblyPath) || !fileExists(entryAssemblyPath))
        {
            return null;
        }

        return new AutostartCommand(processPath, [entryAssemblyPath, PlatformIdentity.AutostartArgument]);
    }
}
