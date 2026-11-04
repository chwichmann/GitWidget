using GitStatusWidget.Application.Platform;

namespace GitStatusWidget.Desktop.Services;

/// <summary>Cached reduced-motion preference of the operating system (re-read at most once a minute).</summary>
public sealed class MotionPreference
{
    private readonly IReducedMotionDetector _detector;
    private bool _cached;
    private long _expires;

    public MotionPreference(IReducedMotionDetector detector) => _detector = detector;

    public bool ReduceMotion
    {
        get
        {
            if (Environment.TickCount64 >= _expires)
            {
                _cached = _detector.IsReducedMotionEnabled();
                _expires = Environment.TickCount64 + 60_000;
            }

            return _cached;
        }
    }
}
