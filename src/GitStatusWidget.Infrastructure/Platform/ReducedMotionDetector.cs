using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitStatusWidget.Application.Platform;

namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>
/// Reads the OS reduced-motion preference with a short cache. Any failure is treated as
/// "no preference" (<see langword="false"/>).
/// </summary>
internal sealed class ReducedMotionDetector : IReducedMotionDetector
{
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(60);

    private readonly Func<bool?> _probe;
    private readonly TimeProvider _time;
    private readonly TimeSpan _cacheDuration;
    private readonly Lock _gate = new();
    private bool _cached;
    private long _expiresAt = long.MinValue;

    /// <summary>Creates a detector for the current OS.</summary>
    public ReducedMotionDetector()
        : this(ProbeCurrentOs, TimeProvider.System, DefaultCacheDuration)
    {
    }

    /// <summary>Creates a detector with an injected probe (tests).</summary>
    /// <param name="probe">Returns the OS value, or <see langword="null"/> when unknown.</param>
    /// <param name="time">Clock for the cache.</param>
    /// <param name="cacheDuration">How long a result is reused.</param>
    internal ReducedMotionDetector(Func<bool?> probe, TimeProvider time, TimeSpan cacheDuration)
    {
        _probe = probe;
        _time = time;
        _cacheDuration = cacheDuration;
    }

    public bool IsReducedMotionEnabled()
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (now < _expiresAt)
            {
                return _cached;
            }

            bool? value;
            try
            {
                value = _probe();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception or InvalidOperationException or IOException)
            {
                value = null;
            }

            _cached = value ?? false;
            _expiresAt = now + (long)(_cacheDuration.TotalSeconds * _time.TimestampFrequency);
            return _cached;
        }
    }

    internal static bool? ProbeCurrentOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsReducedMotion.ShouldReduceMotion();
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacReducedMotion.ShouldReduceMotion();
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxReducedMotion.ShouldReduceMotion();
        }

        return null;
    }
}

/// <summary>Windows: <c>SPI_GETCLIENTAREAANIMATION</c> (Settings → Accessibility → Visual effects → Animation effects).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsReducedMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool? ShouldReduceMotion()
    {
        int enabled = 1; // Win32 BOOL
        return SystemParametersInfo(SpiGetClientAreaAnimation, 0, &enabled, 0) ? enabled == 0 : null;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, void* pvParam, uint fWinIni);
}

/// <summary>macOS: <c>NSWorkspace.sharedWorkspace.accessibilityDisplayShouldReduceMotion</c> via the Objective-C runtime.</summary>
[SupportedOSPlatform("macos")]
internal static partial class MacReducedMotion
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    public static bool? ShouldReduceMotion()
    {
        // AppKit is already loaded in the Avalonia app; the explicit load covers tests and console hosts.
        if (!NativeLibrary.TryLoad(AppKit, out _))
        {
            return null;
        }

        nint workspaceClass = objc_getClass("NSWorkspace");
        if (workspaceClass == 0)
        {
            return null;
        }

        nint workspace = MsgSendPointer(workspaceClass, sel_registerName("sharedWorkspace"));
        return workspace == 0 ? null : MsgSendBool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion"));
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    // Typed, non-variadic objc_msgSend imports are correct on arm64 and x64.
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint MsgSendPointer(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool MsgSendBool(nint receiver, nint selector);
}

/// <summary>
/// Linux (best effort): XDG Desktop Portal <c>org.freedesktop.appearance reduced-motion</c>, then GNOME
/// <c>org.gnome.desktop.interface enable-animations</c>. Each helper process is bounded by a timeout.
/// </summary>
internal static class LinuxReducedMotion
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMilliseconds(1000);

    [SupportedOSPlatform("linux")]
    public static bool? ShouldReduceMotion()
    {
        string? portal = Run(
            "gdbus", "call", "--session", "--dest", "org.freedesktop.portal.Desktop",
            "--object-path", "/org/freedesktop/portal/desktop",
            "--method", "org.freedesktop.portal.Settings.ReadOne", "org.freedesktop.appearance", "reduced-motion");
        bool? fromPortal = ParsePortalReply(portal);
        if (fromPortal is not null)
        {
            return fromPortal;
        }

        return ParseGnomeEnableAnimations(Run("gsettings", "get", "org.gnome.desktop.interface", "enable-animations"));
    }

    /// <summary>Parses <c>(&lt;&lt;uint32 1&gt;&gt;,)</c>: 1 = reduce, 0 = no preference.</summary>
    /// <param name="reply">gdbus output.</param>
    /// <returns>The preference, or <see langword="null"/> when not present.</returns>
    internal static bool? ParsePortalReply(string? reply)
    {
        if (reply is null)
        {
            return null;
        }

        int index = reply.IndexOf("uint32 ", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        string rest = reply[(index + "uint32 ".Length)..];
        return rest.StartsWith('1') ? true : rest.StartsWith('0') ? false : null;
    }

    /// <summary>Parses gsettings output: <c>false</c> = reduce motion.</summary>
    /// <param name="output">gsettings output.</param>
    /// <returns>The preference, or <see langword="null"/>.</returns>
    internal static bool? ParseGnomeEnableAnimations(string? output) => output?.Trim() switch
    {
        "false" => true,
        "true" => false,
        _ => null,
    };

    private static string? Run(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProcessTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Exited in the meantime.
                }

                return null;
            }

            return process.ExitCode == 0 && output.Wait(ProcessTimeout) ? output.Result : null;
        }
        catch (Win32Exception)
        {
            return null; // Tool not installed.
        }
    }
}
