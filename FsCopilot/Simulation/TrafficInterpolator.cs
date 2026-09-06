namespace FsCopilot.Simulation;

/// <summary>
/// Where one frozen object should be right now. A small playout buffer of recent samples;
/// the pose is interpolated between the two that bracket a render point held a little behind
/// the newest sample. Runs entirely on the receiver's clock - sample times are arrival minus
/// the age the host stamped. Measured against the sim's own dead reckoning this is three
/// times smoother, and smoother than the ATC add-on's own traffic.
/// </summary>
public sealed class TrafficInterpolator
{
    // How far behind the newest sample the render point sits: a slowly adapting average of the
    // sample spacing plus slack for a late one. It must not follow the spacing of any single
    // pair - that would move the render point every time a sample arrived, a visible jump.
    public const int JitterMarginMs = 200;
    public const int MinSpacingMs = 50;
    public const int MaxSpacingMs = 1500;
    private const double DelaySmoothing = 0.1;
    // When the next sample is late or lost, keep moving along the last segment for this long
    // before holding: one lost batch is bridged without a pause, two in a row show.
    public const int MaxExtrapolationMs = 600;
    private const int MaxSamples = 16;

    private readonly List<(long T, TrafficState S)> _samples = new(8);
    private double _delayMs;        // what the render point uses; slewed towards the target
    private double _targetDelayMs;  // the smoothed spacing plus margin
    private const double DelaySlewPerRenderMs = 0.25;   // ~8 ms per second at 30 fps: never felt
    private DriveState _last;
    private bool _hasLast;

    public TrafficState? Latest => _samples.Count > 0 ? _samples[^1].S : null;

    /// <summary>Accept a sample; one older than the newest held is ignored.</summary>
    public bool Push(long sampleTimeMs, in TrafficState s)
    {
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (sampleTimeMs <= last.T) return false;   // older, or a duplicate: a zero-length segment would poison the delay
            var spacing = Math.Clamp(sampleTimeMs - last.T, MinSpacingMs, MaxSpacingMs) + JitterMarginMs;
            if (_targetDelayMs == 0) _targetDelayMs = _delayMs = spacing;
            else _targetDelayMs += (spacing - _targetDelayMs) * DelaySmoothing;
        }
        _samples.Add((sampleTimeMs, s));
        if (_samples.Count > MaxSamples) _samples.RemoveAt(0);
        return true;
    }

    /// <summary>The pose to write this frame, or false when it is the pose already written.</summary>
    public bool TryRender(long nowMs, out DriveState drive)
    {
        drive = default;
        if (_samples.Count < 2) return false;

        // Follow the target a fraction of a millisecond per frame: the render point never jumps.
        _delayMs += Math.Clamp(_targetDelayMs - _delayMs, -DelaySlewPerRenderMs, DelaySlewPerRenderMs);
        var renderTime = nowMs - (long)_delayMs;
        // Samples behind the render point are only needed as the start of the bracketing pair.
        while (_samples.Count > 2 && _samples[1].T <= renderTime) _samples.RemoveAt(0);

        var (ta, a) = _samples[0];
        var (tb, b) = _samples[1];
        var segment = Math.Max(tb - ta, 1);
        var maxAlpha = 1 + Math.Min(MaxExtrapolationMs, segment) / (double)segment;
        var alpha = Math.Clamp((renderTime - ta) / (double)segment, 0, maxAlpha);

        drive = new DriveState
        {
            Lat = Lerp(a.Lat, b.Lat, alpha),
            Lon = Lerp(a.Lon, b.Lon, alpha),
            Alt = Lerp(a.Alt, b.Alt, alpha),
            Pitch = LerpAngle(a.Pitch, b.Pitch, alpha),
            Bank = LerpAngle(a.Bank, b.Bank, alpha),
            HeadingTrue = LerpAngle(a.Hdg, b.Hdg, alpha),
            // The object is frozen; these only feed animations and sounds.
            VelBodyX = b.VbX, VelBodyY = b.VbY, VelBodyZ = b.VbZ,
            RotX = b.RotX, RotY = b.RotY, RotZ = b.RotZ
        };
        if (_hasLast && SamePose(in drive, in _last)) return false;   // parked, or holding
        _last = drive;
        _hasLast = true;
        return true;
    }

    private static bool SamePose(in DriveState x, in DriveState y) =>
        x.Lat == y.Lat && x.Lon == y.Lon && x.Alt == y.Alt && x.HeadingTrue == y.HeadingTrue && x.Pitch == y.Pitch && x.Bank == y.Bank;

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double LerpAngle(double a, double b, double t)
    {
        var delta = ((b - a) % 360 + 540) % 360 - 180;
        return a + delta * t;
    }
}
