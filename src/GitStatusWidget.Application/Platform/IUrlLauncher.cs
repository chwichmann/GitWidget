namespace GitStatusWidget.Application.Platform;

/// <summary>
/// Opens a validated web URL in the user's default browser without a shell and without a window.
/// </summary>
public interface IUrlLauncher
{
    /// <summary>
    /// Opens <paramref name="uri"/> if it is an absolute <c>https</c> URL (or <c>http</c> when
    /// <paramref name="allowHttp"/> is set) with a host, without user info, whitespace or control characters.
    /// </summary>
    /// <param name="uri">The URL to open; treat provider URLs as untrusted.</param>
    /// <param name="allowHttp">Allow plain <c>http</c> (only for explicitly configured internal hosts).</param>
    /// <returns><see langword="true"/> when the URL passed validation and the browser launch was started.</returns>
    bool TryOpen(Uri uri, bool allowHttp);
}
