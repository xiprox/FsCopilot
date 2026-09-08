namespace FsCopilot.Audio;

using Concentus;
using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>
/// The receiver's audio pipeline: a jitter buffer ordered by the host's sequence numbers,
/// Opus with packet-loss concealment for gaps, and WASAPI shared-mode output that follows the
/// default device. A talkspurt starts playing once 60 ms is buffered and ends when the buffer runs
/// dry, which is simply the host's gate having closed.
/// </summary>
public sealed class AtcPlayer : IDisposable
{
    public const int BufferMs = 60;
    public const int OutputLatencyMs = 50;
    private const int FrameMs = 20;
    private const int DeviceCheckMs = 2000;

    private readonly IOpusDecoder _decoder;
    private readonly short[] _pcm = new short[AtcCapture.FrameSamples];
    private readonly byte[] _pcmBytes = new byte[AtcCapture.FrameSamples * 2];
    private readonly Lock _lock = new();
    private readonly SortedDictionary<uint, byte[]> _buffer = new();
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private BufferedWaveProvider _out;
    private VolumeWaveProvider16 _volume;
    private WasapiOut? _player;
    private string? _deviceId;      // the endpoint the current output was opened on
    private volatile bool _stop;
    private bool _playing;
    private uint _next;
    private float _gain = 1f;
    private bool _muted;

    public long Played { get; private set; }
    public long Concealed { get; private set; }
    public long Late { get; private set; }

    public float Volume
    {
        get => _gain;
        set { _gain = Math.Clamp(value, 0, 1); Apply(); }
    }

    public bool Muted
    {
        get => _muted;
        set { _muted = value; Apply(); }
    }

    public AtcPlayer()
    {
        _decoder = OpusCodecFactory.CreateDecoder(AtcCapture.SampleRate, 1);
        _out = new BufferedWaveProvider(new WaveFormat(AtcCapture.SampleRate, 16, 1))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        _volume = new VolumeWaveProvider16(_out);
        Apply();
        Open();
        _thread = new Thread(Loop) { IsBackground = true, Name = "ATC audio" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(500);
        Close();
    }

    /// <summary>A frame from the network, any order.</summary>
    public void Enqueue(uint seq, byte[] opus)
    {
        lock (_lock)
        {
            if (_playing && seq < _next) { Late++; return; }
            _buffer[seq] = opus;
        }
    }

    private void Apply() => _volume.Volume = _muted ? 0 : _gain;

    private void Open()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _deviceId = device.ID;
            _player = new WasapiOut(device, AudioClientShareMode.Shared, true, OutputLatencyMs);
            _player.Init(_volume);
            _player.Play();
            Log.Information("[Atc] Playing on {Device}", device.FriendlyName);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Atc] Could not open the output device");
            _player = null;
        }
    }

    private void Close()
    {
        try { _player?.Stop(); _player?.Dispose(); } catch { /* ignore */ }
        _player = null;
        _deviceId = null;
    }

    /// <summary>
    /// True when the default output is no longer the endpoint we opened. Unplugging a device
    /// raises an error and heals itself through the playback catch; merely changing which device
    /// is default does not - WASAPI keeps rendering happily into the old one, so the only way to
    /// find out is to look. The card has no device picker, so nobody could correct it by hand.
    /// </summary>
    private bool DefaultDeviceChanged()
    {
        if (_deviceId is null) return false;
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID != _deviceId;
        }
        catch { return false; }   // no default device at all: leave the output alone
    }

    private void Loop()
    {
        var nextTick = _clock.ElapsedMilliseconds;
        var lastRetry = 0L;
        var lastDeviceCheck = _clock.ElapsedMilliseconds;
        while (!_stop)
        {
            var now = _clock.ElapsedMilliseconds;
            if (_player is null && now - lastRetry > 2000) { lastRetry = now; Open(); }
            else if (_player is not null && now - lastDeviceCheck > DeviceCheckMs)
            {
                lastDeviceCheck = now;
                if (DefaultDeviceChanged())
                {
                    Log.Information("[Atc] The default output changed; moving playback to it");
                    Close();
                    Open();
                }
            }
            if (now >= nextTick)
            {
                nextTick += FrameMs;
                if (nextTick < now - 500) nextTick = now;   // fell far behind (sleep, debugger): do not race to catch up
                Tick();
            }
            Thread.Sleep(2);
        }
    }

    private void Tick()
    {
        byte[]? opus; var conceal = false;
        lock (_lock)
        {
            if (!_playing)
            {
                if (_buffer.Count * FrameMs < BufferMs) return;
                _next = _buffer.Keys.First();
                _playing = true;
            }
            if (_buffer.Remove(_next, out opus)) { }
            else if (_buffer.Count > 0) conceal = true;      // a gap with later frames waiting
            else { _playing = false; return; }              // the talkspurt ended
            _next++;
        }
        try
        {
            var n = conceal ? _decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, AtcCapture.FrameSamples) : _decoder.Decode(opus, _pcm, AtcCapture.FrameSamples);
            if (conceal) Concealed++;
            Buffer.BlockCopy(_pcm, 0, _pcmBytes, 0, n * 2);
            _out.AddSamples(_pcmBytes, 0, n * 2);
            Played++;
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Atc] Playback error; reopening the output");
            Close();
        }
    }
}
