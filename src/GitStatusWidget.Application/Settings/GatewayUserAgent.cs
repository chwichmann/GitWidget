namespace GitStatusWidget.Application.Settings;

/// <summary>
/// User-Agent suggested when an access gateway only admits git clients (architecture §9.4, issue #10). It is only ever
/// offered to the user, never applied automatically.
/// </summary>
public static class GatewayUserAgent
{
    /// <summary>git product token that the observed gateway admits.</summary>
    public const string GitProduct = "git/2.47.0";

    /// <summary>Suggested User-Agent, e.g. <c>GitWidget/0.1.2 git/2.47.0</c>.</summary>
    /// <param name="appVersion">Running GitWidget version.</param>
    /// <returns>The suggestion.</returns>
    public static string Suggest(string appVersion) =>
        $"GitWidget/{(string.IsNullOrWhiteSpace(appVersion) ? "0.1" : appVersion.Trim())} {GitProduct}";
}
