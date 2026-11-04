namespace GitStatusWidget.Application.Platform;

/// <summary>
/// Reads the operating system's reduced-motion preference (Windows "Animation effects",
/// macOS "Reduce motion", XDG portal "reduced-motion" / GNOME "enable-animations").
/// </summary>
public interface IReducedMotionDetector
{
    /// <summary>
    /// Returns whether the user asked the OS to reduce motion. Returns <see langword="false"/> when the
    /// setting cannot be read. Results are cached briefly, so calling this per animation is cheap.
    /// </summary>
    /// <returns><see langword="true"/> when animations should be reduced.</returns>
    bool IsReducedMotionEnabled();
}
