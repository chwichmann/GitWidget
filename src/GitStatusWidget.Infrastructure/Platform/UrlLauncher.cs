using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using GitStatusWidget.Application.Platform;

namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>
/// Opens validated web URLs without a shell: Windows <c>ShellExecute</c> on the URL string only,
/// macOS <c>/usr/bin/open &lt;url&gt;</c>, Linux <c>xdg-open &lt;url&gt;</c>, each with the URL as a
/// single argv entry (architecture §23.3).
/// </summary>
internal sealed class UrlLauncher : IUrlLauncher
{
    /// <summary>Upper bound for a launchable URL.</summary>
    internal const int MaxUrlLength = 8192;

    public bool TryOpen(Uri uri, bool allowHttp)
    {
        if (!TryGetLaunchableUrl(uri, allowHttp, out string? url))
        {
            return false;
        }

        ProcessStartInfo? startInfo = CreateStartInfo(url);
        if (startInfo is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates <paramref name="uri"/>: absolute, <c>https</c> (or <c>http</c> when allowed), has a host,
    /// no user info, no whitespace or control characters, bounded length.
    /// </summary>
    /// <param name="uri">Candidate URL.</param>
    /// <param name="allowHttp">Whether plain <c>http</c> is acceptable.</param>
    /// <param name="url">The normalized URL to pass to the OS.</param>
    /// <returns><see langword="true"/> when the URL may be launched.</returns>
    internal static bool TryGetLaunchableUrl(Uri? uri, bool allowHttp, [NotNullWhen(true)] out string? url)
    {
        url = null;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        bool schemeAllowed = uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp);
        if (!schemeAllowed || uri.IsFile || uri.IsUnc)
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (ContainsWhitespaceOrControl(uri.OriginalString))
        {
            return false;
        }

        string candidate = uri.AbsoluteUri;
        if (candidate.Length > MaxUrlLength
            || ContainsWhitespaceOrControl(candidate)
            || !(candidate.StartsWith("https://", StringComparison.Ordinal) || (allowHttp && candidate.StartsWith("http://", StringComparison.Ordinal))))
        {
            return false;
        }

        url = candidate;
        return true;
    }

    /// <summary>Builds the start info for the current OS; the URL is never parsed by a shell.</summary>
    /// <param name="url">A URL accepted by <see cref="TryGetLaunchableUrl"/>.</param>
    /// <returns>The start info, or <see langword="null"/> on unsupported operating systems.</returns>
    internal static ProcessStartInfo? CreateStartInfo(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            // ShellExecuteEx with a validated https:/http: URL can only reach the registered protocol handler.
            return new ProcessStartInfo(url) { UseShellExecute = true };
        }

        string? opener = OperatingSystem.IsMacOS() ? "/usr/bin/open"
            : OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() ? "xdg-open"
            : null;
        if (opener is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(opener)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    private static bool ContainsWhitespaceOrControl(string value)
    {
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '​' or '‎' or '‏' or '‪' or '‫' or '‬' or '‭' or '‮' or '﻿')
            {
                return true;
            }
        }

        return false;
    }
}
