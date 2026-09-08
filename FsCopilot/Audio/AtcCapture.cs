namespace FsCopilot.Audio;

using Concentus;
using Concentus.Enums;
using NAudio.Wave;

/// <summary>
/// The host's audio pipeline for one process: process loopback → 20 ms frames → RMS gate with
/// hangover and one frame of pre-roll → mixer-level compensation → Opus → <c>send</c>. Measured
/// on one machine at ~130 ms capture-to-speaker with the receiver's buffer, ~22 kbps while ATC
/// talks and nothing while it does not.
/// </summary>
public sealed class AtcCapture : IDisposable
{
    public const int SampleRate = 48_000;
    public const int FrameSamples = 960;          // 20 ms
    public const int Bitrate = 24_000;
    public const double GateDbfs = -50;
    private const int HangoverFrames = 15;        // 300 ms after the last frame above the gate
    private const float MaxGain = 10f;            // +20 dB; below a 10 % mixer level the audio is too quantised to rescue
    private static readonly TimeSpan MixerPoll = TimeSpan.FromMilliseconds(500);

    private readonly int _pid;
    private readonly Action<byte[], int> _send;
    private readonly ProcessLoopback _loopback;
    private readonly IOpusEncoder _encoder;
    private readonly short[] _frame = new short[FrameSamples];
    private readonly short[] _previous = new short[FrameSamples];
    private readonly byte[] _encoded = new byte[AtcFrame.MaxOpusBytes];
    private readonly Timer _mixer;
    private int _fill;
    private bool _havePrevious, _wasActive;
    private int _hangover;
    private volatile float _gain = 1f;

    public int Pid => _pid;
    public float MixerVolume { get; private set; } = 1f;
    public bool MixerMuted { get; private set; }
    /// <summary>Peak level of the last frame, dBFS after compensation.</summary>
    public double PeakDbfs { get; private set; } = -120;
    public long FramesSent { get; private set; }

    public AtcCapture(int pid, Action<byte[], int> send)
    {
        _pid = pid;
        _send = send;
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = Bitrate;
        _encoder.UseVBR = true;
        _loopback = new ProcessLoopback(pid, new WaveFormat(SampleRate, 16, 1));
        _loopback.Data += OnPcm;
        _loopback.Start();
        _mixer = new Timer(_ => ReadMixer(), null, TimeSpan.Zero, MixerPoll);
    }

    public void Dispose()
    {
        _mixer.Dispose();
        _loopback.Dispose();
    }

    // The mixer level is applied before the process-loopback tap (measured), so it is read back
    // and divided out: what the host sends must not depend on how loud they like ATC locally.
    private void ReadMixer()
    {
        try
        {
            var (volume, muted) = AtcApps.SessionVolume(_pid);
            MixerVolume = volume;
            MixerMuted = muted;
            _gain = volume > 0 ? Math.Min(MaxGain, 1f / volume) : 1f;
        }
        catch { /* the session may not exist yet */ }
    }

    private void OnPcm(byte[] pcm, int bytes)
    {
        var samples = bytes / 2;
        var gain = _gain;
        for (var i = 0; i < samples; i++)
        {
            var s = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8);
            if (gain != 1f) s = (short)Math.Clamp(s * gain, short.MinValue, short.MaxValue);
            _frame[_fill++] = s;
            if (_fill == FrameSamples) { Frame(); _fill = 0; }
        }
    }

    private void Frame()
    {
        double sum = 0; var peak = 0;
        foreach (var s in _frame) { sum += (double)s * s; peak = Math.Max(peak, Math.Abs((int)s)); }
        var rmsDb = 20 * Math.Log10(Math.Sqrt(sum / FrameSamples) / 32768.0 + 1e-9);
        PeakDbfs = 20 * Math.Log10(peak / 32768.0 + 1e-9);

        var active = rmsDb > GateDbfs;
        if (active) _hangover = HangoverFrames;
        else if (_hangover > 0) { _hangover--; active = true; }

        if (active && !_wasActive && _havePrevious) Send(_previous);   // pre-roll: the frame the onset started in
        if (active) Send(_frame);
        _wasActive = active;
        Array.Copy(_frame, _previous, FrameSamples);
        _havePrevious = true;
    }

    private void Send(short[] pcm)
    {
        var n = _encoder.Encode(pcm, FrameSamples, _encoded, _encoded.Length);
        FramesSent++;
        _send(_encoded, n);
    }
}
