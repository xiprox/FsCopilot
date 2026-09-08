namespace FsCopilot.Simulation;

/// <summary>
/// The host's change gate for one object. A sample goes out only when it differs from the last
/// one sent beyond small thresholds, or when the heartbeat is due - the heartbeat being loss
/// insurance on an unreliable channel, not a rate. When a change follows a run of suppressed
/// samples, the last suppressed one goes first with its own age, so the receiver knows when the
/// quiet period ended and does not stretch the first step of motion across it. At a busy gate
/// this passes about a fifth of the samples. Each sample says whether it came from a change or
/// from standing still, because the two mean opposite things to the receiver's playout delay.
/// <para>
/// The channel is unreliable, and the one loss that shows is an object's last sample: the next
/// poll replaces a lost sample of a moving object inside the receiver's playout buffer, but a
/// lost "stopped here" leaves the receiver extrapolating past the stop and holding there until
/// the heartbeat. So the sample that ends a run of motion is sent again on the next
/// <see cref="Repeats"/> polls, unchanged and marked quiet. A receiver that has it drops the
/// copy as a duplicate of the same instant; one that lost it gets the stop where it happened.
/// </para>
/// </summary>
public sealed class TrafficGate(TimeSpan heartbeat)
{
    public const int Repeats = 2;

    private readonly long _heartbeatMs = (long)heartbeat.TotalMilliseconds;
    private ObjectState? _sent;
    private long _sentAt;
    private ObjectState? _held;
    private long _heldAt;
    private int _repeatsLeft;

    /// <summary>
    /// Decide what to send for this sample: nothing, the sample, or the held sample followed by
    /// the sample. Ages are relative to <paramref name="now"/>.
    /// </summary>
    public int Decide(in ObjectState sample, long now, Span<(ObjectState State, ushort AgeMs, bool Quiet)> output)
    {
        if (_sent is not { } sent)
        {
            Sent(sample, now, fresh: true);
            output[0] = (sample, 0, false);
            return 1;
        }

        var changed = Changed(sent, sample);
        var heartbeat = now - _sentAt >= _heartbeatMs;
        if (!changed && !heartbeat)
        {
            _held = sample;
            _heldAt = now;
            if (_repeatsLeft == 0) return 0;
            // The stop, again: the sent sample with its real age, so it lands on the same instant.
            _repeatsLeft--;
            output[0] = (sent, (ushort)Math.Min(now - _sentAt, ushort.MaxValue), true);
            return 1;
        }

        var n = 0;
        // The held sample is where the object stopped being still, not a step of motion, so it
        // is marked quiet; so is a heartbeat. Only a sample the thresholds caught says anything
        // about how fast this object is being sampled while it moves.
        if (changed && _held is { } held && _heldAt > _sentAt)
            output[n++] = (held, (ushort)Math.Min(now - _heldAt, ushort.MaxValue), true);
        output[n++] = (sample, 0, !changed);
        Sent(sample, now, fresh: changed);
        return n;
    }

    /// <summary>Make the next sample go out regardless; for a peer that joined late.</summary>
    public void ForceResend() => _sentAt = long.MinValue / 2;

    /// <param name="fresh">A sample the thresholds caught, or the first: the kind whose loss would show if it were the last.</param>
    private void Sent(in ObjectState sample, long now, bool fresh)
    {
        _sent = sample;
        _sentAt = now;
        _held = null;
        _repeatsLeft = fresh ? Repeats : 0;
    }

    public static bool Changed(in ObjectState a, in ObjectState b) =>
        Math.Abs(a.Lat - b.Lat) > 1e-6 || Math.Abs(a.Lon - b.Lon) > 1e-6 ||   // ~0.1 m
        Math.Abs(a.Alt - b.Alt) > 0.5 ||
        Math.Abs(a.HeadingTrue - b.HeadingTrue) > 0.1 || Math.Abs(a.Pitch - b.Pitch) > 0.1 || Math.Abs(a.Bank - b.Bank) > 0.1 ||
        Math.Abs(a.GroundSpeed - b.GroundSpeed) > 0.5 ||
        a.OnGround != b.OnGround || a.FlapsIndex != b.FlapsIndex || Math.Abs(a.GearHandle - b.GearHandle) > 0.05 ||
        a.EngineMask != b.EngineMask || a.NumEngines != b.NumEngines ||
        a.LightMask != b.LightMask;
}
