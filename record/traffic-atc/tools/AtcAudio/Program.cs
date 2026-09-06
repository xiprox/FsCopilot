using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AtcAudio;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// AtcAudio — stage 3 of record/traffic-atc.
//
// Captures what one process (the ATC app) plays, encodes it with Opus, sends it over real UDP
// to localhost with optional loss/delay/jitter, and plays it back through a jitter buffer on a
// chosen output device. Host and receiver halves run in one process so the whole path can be
// exercised and measured on one machine.
//
//   AtcAudio --process <name|pid> [--out <device>] [--bitrate <bps>] [--vad <dBFS>|--no-vad]
//            [--buffer <ms>] [--loss <pct>] [--delay <ms>] [--jitter <ms>] [--port <n>] [--quiet]
//   AtcAudio --device-capture <device> ...      capture a whole output device instead (the fallback)
//   AtcAudio --list                             list output devices and candidate processes

var opts = Options.Parse(args);
if (opts is null) return 2;
if (opts.List) { Listing.Print(); return 0; }

var format = new WaveFormat(48000, 16, 1);       // Opus-native rate, mono; 20 ms = 960 samples
const int FrameSamples = 960;

// -- output device ------------------------------------------------------------------------
using var devices = new MMDeviceEnumerator();
var outDevice = Listing.FindOutput(devices, opts.Out) ?? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
Console.WriteLine($"Output: {outDevice.FriendlyName}");

// -- capture -------------------------------------------------------------------------------
IDisposable capture;
Action<byte[], int>? onPcm = null;
if (opts.DeviceCapture is { } devName)
{
    var dev = Listing.FindOutput(devices, devName) ?? throw new ArgumentException($"no output device matching '{devName}'");
    var loop = new WasapiLoopbackCapture(dev);
    Console.WriteLine($"Capture: device loopback of {dev.FriendlyName} ({loop.WaveFormat})");
    // Device loopback comes as float stereo at the device rate; fold to our format.
    var conv = new LoopbackConverter(loop.WaveFormat, format);
    loop.DataAvailable += (_, e) => { var (buf, n) = conv.Convert(e.Buffer, e.BytesRecorded); if (n > 0) onPcm?.Invoke(buf, n); };
    loop.StartRecording();
    capture = loop;
}
else
{
    var pid = Listing.ResolvePid(opts.Process!);
    Console.WriteLine($"Capture: process loopback of pid {pid} ({Process.GetProcessById(pid).ProcessName}), include tree");
    var loop = new ProcessLoopback(pid, format);
    loop.Data += (buf, n) => onPcm?.Invoke(buf, n);
    loop.Start();
    capture = loop;
    if (opts.SessionVolume >= 0)
        _ = Task.Delay(10000).ContinueWith(_ => Listing.SetSessionVolume(devices, pid, (float)opts.SessionVolume));
}

// -- host half: frame, gate, encode, send --------------------------------------------------
var host = new Host(opts, format, FrameSamples);
onPcm = host.OnPcm;

// The mixer level is applied before the process-loopback tap (Q10), so read it back and
// divide it out: what the host sends should not depend on how loud they like ATC locally.
if (opts.Process is not null && !opts.NoCompensate)
{
    var pid = Listing.ResolvePid(opts.Process);
    var compensator = new System.Timers.Timer(500) { AutoReset = true };
    compensator.Elapsed += (_, _) =>
    {
        try
        {
            var (vol, muted) = Listing.GetSessionVolume(devices, pid);
            host.SetSessionVolume(vol, muted);
        }
        catch { /* the session may not exist yet; try again next tick */ }
    };
    compensator.Start();
}

// -- receiver half: receive, simulate the wire, buffer, decode, play -----------------------
var receiver = new Receiver(opts, format, FrameSamples, outDevice);
receiver.Start();

Console.WriteLine($"Pipeline latency budget: capture ~10 + frame 20 + jitter buffer {opts.BufferMs} + output {Receiver.OutputLatencyMs} = ~{30 + opts.BufferMs + Receiver.OutputLatencyMs} ms" +
                  (opts.DelayMs > 0 ? $" + simulated {opts.DelayMs}±{opts.JitterMs} ms" : ""));
Console.WriteLine("Ctrl-C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var proc = Process.GetCurrentProcess();
var cpuAt = proc.TotalProcessorTime; var clock = Stopwatch.StartNew(); var cpuClockAt = 0L;
while (!cts.IsCancellationRequested)
{
    try { await Task.Delay(5000, cts.Token); } catch (OperationCanceledException) { break; }
    var cpu = proc.TotalProcessorTime; var now = clock.ElapsedMilliseconds;
    var cpuPct = (cpu - cpuAt).TotalMilliseconds / Math.Max(1, now - cpuClockAt) * 100.0 / Environment.ProcessorCount;
    cpuAt = cpu; cpuClockAt = now;
    Console.WriteLine($"[{now / 1000,5}s] {host.Stats()} | {receiver.Stats()} | cpu {cpuPct:0.0}%");
}

capture.Dispose();
host.Dispose();
receiver.Dispose();
return 0;

// ---------------------------------------------------------------------------------------

sealed class Options
{
    public string? Process, DeviceCapture, Out;
    public bool List, Quiet, NoVad, NoCompensate;
    public int Bitrate = 24000, BufferMs = 60, Port = 47800;
    public double VadDb = -50, LossPct, DelayMs, JitterMs;
    public double SessionVolume = -1;   // Q10: set the target's mixer volume and see whether the tap follows

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--process": o.Process = Next(); break;
                    case "--device-capture": o.DeviceCapture = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--list": o.List = true; break;
                    case "--bitrate": o.Bitrate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--vad": o.VadDb = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--no-vad": o.NoVad = true; break;
                    case "--buffer": o.BufferMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--loss": o.LossPct = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--delay": o.DelayMs = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--jitter": o.JitterMs = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--session-volume": o.SessionVolume = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--no-compensate": o.NoCompensate = true; break;
                    case "--quiet": o.Quiet = true; break;
                    case "-h": case "--help": Usage(); return null;
                    default: throw new ArgumentException($"unknown option {args[i]}");
                }
            }
            if (!o.List && o.Process is null && o.DeviceCapture is null) throw new ArgumentException("--process or --device-capture required");
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message); Usage(); return null;
        }
        return o;
    }

    static void Usage() => Console.Error.WriteLine(
        "AtcAudio --process <name|pid> | --device-capture <device> [--out <device>] [--bitrate 24000] [--vad -50 | --no-vad]\n" +
        "         [--buffer 60] [--loss <pct>] [--delay <ms>] [--jitter <ms>] [--port 47800] [--session-volume <0..1>] [--quiet]\n" +
        "AtcAudio --list\n" +
        "  --session-volume  set the captured process's volume-mixer level after 10 s, to see whether the tap follows it (Q10)\n" +
        "  --no-compensate   do not divide the captured process's mixer level back out of the audio");
}

static class Listing
{
    public static void Print()
    {
        using var e = new MMDeviceEnumerator();
        Console.WriteLine("Output devices:");
        var idx = 0;
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            Console.WriteLine($"  [{idx++}] {d.FriendlyName}");
        Console.WriteLine("Known ATC apps running:");
        foreach (var p in Process.GetProcesses().Where(p => KnownAtcApps.Any(k => p.ProcessName.Contains(k, StringComparison.OrdinalIgnoreCase))).OrderBy(p => p.ProcessName))
            Console.WriteLine($"  {p.Id,6}  {p.ProcessName}");
        // What a "Choose another" list would show: processes that own an audio session on any
        // render device, i.e. things that make sound, rather than every process on the machine.
        Console.WriteLine("Processes with an audio session (state, mixer volume, mute):");
        var seen = new HashSet<(uint, string)>();
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var sessions = d.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var pid = s.GetProcessID;
                if (!seen.Add((pid, d.ID))) continue;
                string name;
                try { name = pid == 0 ? "(system sounds)" : Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
                var elev = pid == 0 ? "" : Elevation.Describe((int)pid);
                Console.WriteLine($"  {pid,6}  {name,-24} {s.State,-16} vol {s.SimpleAudioVolume.Volume:0.00} {(s.SimpleAudioVolume.Mute ? "muted" : "")}  {elev,-14} on {d.FriendlyName}");
            }
        }
    }

    static readonly string[] KnownAtcApps = ["BeyondATC", "SayIntentions", "Pilot2ATC", "PF3", "FSHud", "VoxATC"];

    public static MMDevice? FindOutput(MMDeviceEnumerator e, string? spec)
    {
        if (spec is null) return null;
        var all = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (int.TryParse(spec, out var idx) && idx >= 0 && idx < all.Count) return all[idx];
        return all.FirstOrDefault(d => d.FriendlyName.Contains(spec, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The volume-mixer level of a process: the lowest across its sessions, and whether any is muted.</summary>
    public static (float Volume, bool Muted) GetSessionVolume(MMDeviceEnumerator e, int pid)
    {
        var vol = 1f; var muted = false; var found = false;
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var sessions = d.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.GetProcessID != (uint)pid) continue;
                found = true;
                vol = Math.Min(vol, s.SimpleAudioVolume.Volume);
                muted |= s.SimpleAudioVolume.Mute;
            }
        }
        return found ? (vol, muted) : (1f, false);
    }

    /// <summary>Set the volume-mixer level of every audio session belonging to a process, on every render device.</summary>
    public static void SetSessionVolume(MMDeviceEnumerator e, int pid, float volume)
    {
        var hit = 0;
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var sessions = d.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.GetProcessID != (uint)pid) continue;
                s.SimpleAudioVolume.Volume = volume; hit++;
            }
        }
        Console.WriteLine($"  set mixer volume {volume:0.00} on {hit} session(s) of pid {pid}");
    }

    public static int ResolvePid(string spec)
    {
        if (int.TryParse(spec, out var pid)) return pid;
        var name = spec.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? spec[..^4] : spec;
        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0) procs = Process.GetProcesses().Where(p => p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (procs.Length == 0) throw new ArgumentException($"no process matching '{spec}'");
        // The oldest is the launcher / top of the tree; include-tree covers the rest.
        return procs.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).First().Id;
    }
}

/// <summary>Folds device-loopback audio (float, any rate, any channels) into our 48 kHz mono 16-bit.</summary>
sealed class LoopbackConverter(WaveFormat from, WaveFormat to)
{
    readonly BufferedWaveProvider _in = new(from) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };
    IWaveProvider? _chain;
    byte[] _out = new byte[to.AverageBytesPerSecond];

    public (byte[], int) Convert(byte[] data, int bytes)
    {
        _in.AddSamples(data, 0, bytes);
        if (_chain is null)
        {
            var sp = _in.ToSampleProvider();
            if (from.Channels == 2) sp = sp.ToMono();
            if (from.SampleRate != to.SampleRate) sp = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sp, to.SampleRate);
            _chain = sp.ToWaveProvider16();
        }
        var want = (int)((long)_in.BufferedBytes * to.AverageBytesPerSecond / from.AverageBytesPerSecond) / to.BlockAlign * to.BlockAlign;
        if (want > _out.Length) _out = new byte[want];
        var n = want > 0 ? _chain.Read(_out, 0, want) : 0;
        return (_out, n);
    }
}

/// <summary>The host half: 20 ms frames, RMS gate with hangover, Opus, UDP.</summary>
sealed class Host : IDisposable
{
    const int HangoverFrames = 15;   // 300 ms after the last frame above threshold

    readonly Options _opts;
    readonly int _frameSamples;
    readonly short[] _frame;
    readonly short[] _previous;      // one frame of pre-roll, so an onset inside a frame is not clipped
    bool _havePrevious, _wasActive;
    int _frameFill;
    readonly IOpusEncoder _enc;
    readonly byte[] _encoded = new byte[1275];
    readonly UdpClient _udp = new();
    readonly IPEndPoint _to;
    uint _seq;
    int _hangover;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    long _framesIn, _framesSent, _bytesSent; double _peakDb = -120, _rmsDbSum; int _rmsCount;
    long _statAt;
    volatile float _gain = 1f; volatile bool _sourceMuted; float _sessionVolume = 1f;

    const float MaxGain = 10f;   // +20 dB; below a 10 % mixer level the audio is too quantised to rescue

    public void SetSessionVolume(float volume, bool muted)
    {
        _sessionVolume = volume; _sourceMuted = muted;
        _gain = volume > 0 ? Math.Min(MaxGain, 1f / volume) : 1f;
    }

    public Host(Options opts, WaveFormat format, int frameSamples)
    {
        _opts = opts; _frameSamples = frameSamples;
        _frame = new short[frameSamples];
        _previous = new short[frameSamples];
        _enc = OpusCodecFactory.CreateEncoder(format.SampleRate, format.Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _enc.Bitrate = opts.Bitrate;
        _enc.UseVBR = true;
        _to = new IPEndPoint(IPAddress.Loopback, opts.Port);
    }

    public void OnPcm(byte[] pcm, int bytes)
    {
        var samples = bytes / 2;
        var gain = _gain;
        for (var i = 0; i < samples; i++)
        {
            var s = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8);
            if (gain != 1f) s = (short)Math.Clamp(s * gain, short.MinValue, short.MaxValue);
            _frame[_frameFill++] = s;
            if (_frameFill == _frameSamples) { Frame(); _frameFill = 0; }
        }
    }

    void Frame()
    {
        _framesIn++;
        double sum = 0; int peak = 0;
        foreach (var s in _frame) { sum += (double)s * s; peak = Math.Max(peak, Math.Abs((int)s)); }
        var rmsDb = 20 * Math.Log10(Math.Sqrt(sum / _frameSamples) / 32768.0 + 1e-9);
        var peakDb = 20 * Math.Log10(peak / 32768.0 + 1e-9);
        _peakDb = Math.Max(_peakDb, peakDb); _rmsDbSum += rmsDb; _rmsCount++;

        var active = _opts.NoVad || rmsDb > _opts.VadDb;
        if (active) _hangover = HangoverFrames;
        else if (_hangover > 0) { _hangover--; active = true; }

        if (active && !_wasActive && _havePrevious) Send(_previous);   // pre-roll: the frame the onset started in
        if (active) Send(_frame);
        _wasActive = active;
        Array.Copy(_frame, _previous, _frameSamples); _havePrevious = true;
    }

    void Send(short[] pcm)
    {
        var n = _enc.Encode(pcm, _frameSamples, _encoded, _encoded.Length);
        var packet = new byte[10 + n];
        BitConverter.TryWriteBytes(packet.AsSpan(0), _seq++);
        BitConverter.TryWriteBytes(packet.AsSpan(4), (uint)Environment.TickCount64);   // shared clock for the latency figure
        BitConverter.TryWriteBytes(packet.AsSpan(8), (ushort)n);
        Array.Copy(_encoded, 0, packet, 10, n);
        _udp.Send(packet, packet.Length, _to);
        _framesSent++; _bytesSent += packet.Length + 28;   // + UDP/IP header
    }

    public string Stats()
    {
        var now = _clock.ElapsedMilliseconds; var dt = Math.Max(1, now - _statAt) / 1000.0; _statAt = now;
        var mixer = _sourceMuted ? "MUTED in mixer" : _gain != 1f ? $"mixer {_sessionVolume:0.00} → gain x{_gain:0.0}" : "mixer 1.00";
        var s = $"in {_framesIn} frames, peak {_peakDb:0} dBFS, rms {(_rmsCount > 0 ? _rmsDbSum / _rmsCount : -120):0} dBFS ({mixer}), sent {_framesSent} ({(_framesIn > 0 ? 100.0 * _framesSent / _framesIn : 0):0}%), {_bytesSent * 8 / dt / 1000:0.0} kbps";
        _framesIn = 0; _framesSent = 0; _bytesSent = 0; _peakDb = -120; _rmsDbSum = 0; _rmsCount = 0;
        return s;
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>The receiver half: UDP in, simulated wire, jitter buffer, Opus decode with PLC, WASAPI out.</summary>
sealed class Receiver : IDisposable
{
    public const int OutputLatencyMs = 50;

    readonly Options _opts;
    readonly int _frameSamples;
    readonly int _frameMs;
    readonly UdpClient _udp;
    readonly IOpusDecoder _dec;
    readonly BufferedWaveProvider _out;
    readonly WasapiOut _player;
    readonly Thread _rxThread, _playThread;
    readonly Random _rng = new();
    readonly object _lock = new();
    readonly PriorityQueue<(uint Seq, uint Stamp, byte[] Opus), long> _wire = new();   // simulated delay: deliver at time
    readonly SortedDictionary<uint, (uint Stamp, byte[] Opus)> _buffer = new();
    double _latencySum; int _latencyCount;
    readonly short[] _pcm;
    readonly byte[] _pcmBytes;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile bool _stop;
    bool _playing; uint _next;

    long _received, _dropped, _late, _plc, _underruns, _played; int _depth;

    public Receiver(Options opts, WaveFormat format, int frameSamples, MMDevice device)
    {
        _opts = opts; _frameSamples = frameSamples; _frameMs = frameSamples * 1000 / format.SampleRate;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, opts.Port));
        _dec = OpusCodecFactory.CreateDecoder(format.SampleRate, format.Channels);
        _pcm = new short[frameSamples]; _pcmBytes = new byte[frameSamples * 2];
        _out = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };
        _player = new WasapiOut(device, AudioClientShareMode.Shared, true, OutputLatencyMs);
        _player.Init(_out);
        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "AtcAudio rx" };
        _playThread = new Thread(PlayLoop) { IsBackground = true, Name = "AtcAudio play" };
    }

    public void Start() { _player.Play(); _rxThread.Start(); _playThread.Start(); }

    void ReceiveLoop()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop)
        {
            byte[] data;
            try { data = _udp.Receive(ref from); } catch { if (_stop) return; continue; }
            if (data.Length < 10) continue;
            _received++;
            if (_opts.LossPct > 0 && _rng.NextDouble() * 100 < _opts.LossPct) { _dropped++; continue; }
            var seq = BitConverter.ToUInt32(data, 0);
            var stamp = BitConverter.ToUInt32(data, 4);
            var len = BitConverter.ToUInt16(data, 8);
            var opus = new byte[len]; Array.Copy(data, 10, opus, 0, len);
            var at = _clock.ElapsedMilliseconds + (long)(_opts.DelayMs + _rng.NextDouble() * _opts.JitterMs);
            lock (_lock) _wire.Enqueue((seq, stamp, opus), at);
        }
    }

    void PlayLoop()
    {
        var nextTick = _clock.ElapsedMilliseconds;
        while (!_stop)
        {
            var now = _clock.ElapsedMilliseconds;
            // Move whatever the simulated wire has delivered into the jitter buffer.
            lock (_lock)
            {
                while (_wire.TryPeek(out var pkt, out var at) && at <= now)
                {
                    _wire.Dequeue();
                    if (_playing && pkt.Seq < _next) { _late++; continue; }
                    _buffer[pkt.Seq] = (pkt.Stamp, pkt.Opus);
                }
                _depth = _buffer.Count;
            }
            if (now >= nextTick)
            {
                nextTick += _frameMs;
                Tick();
            }
            Thread.Sleep(2);
        }
    }

    void Tick()
    {
        (uint Stamp, byte[] Opus) pkt = default; var plc = false;
        lock (_lock)
        {
            if (!_playing)
            {
                // Start a talkspurt once the buffer holds the target depth.
                if (_buffer.Count * _frameMs < _opts.BufferMs) return;
                _next = _buffer.Keys.First();
                _playing = true;
            }
            if (_buffer.Remove(_next, out pkt)) { }
            else if (_buffer.Count > 0) plc = true;          // a gap with later packets waiting: conceal it
            else { _playing = false; _underruns++; return; } // nothing left: the talkspurt ended (or the wire stalled)
            _next++;
        }
        var n = plc ? _dec.Decode(ReadOnlySpan<byte>.Empty, _pcm, _frameSamples) : _dec.Decode(pkt.Opus, _pcm, _frameSamples);
        if (plc) _plc++;
        Buffer.BlockCopy(_pcm, 0, _pcmBytes, 0, n * 2);
        _out.AddSamples(_pcmBytes, 0, n * 2);
        _played++;
        if (!plc)
        {
            // Capture stamp to the moment this frame will actually leave the speaker: what is
            // queued ahead of it in the output buffer plus the device's own latency.
            var latency = (uint)Environment.TickCount64 - pkt.Stamp + _out.BufferedDuration.TotalMilliseconds + OutputLatencyMs;
            _latencySum += latency; _latencyCount++;
        }
    }

    public string Stats()
    {
        var lat = _latencyCount > 0 ? $"{_latencySum / _latencyCount:0}" : "-";
        _latencySum = 0; _latencyCount = 0;
        return $"rx {_received} (dropped {_dropped}, late {_late}), played {_played}, plc {_plc}, underruns {_underruns}, depth {_depth} pk, latency ~{lat} ms";
    }

    public void Dispose()
    {
        _stop = true;
        try { _udp.Close(); } catch { /* ignore */ }
        _player.Stop(); _player.Dispose();
    }
}

/// <summary>
/// Whether a process runs elevated. Process loopback on an elevated process from a
/// non-elevated FS Copilot is expected to be refused, so the picker should say so up front.
/// An elevated process refuses TOKEN_QUERY to a non-elevated caller of the same user, which
/// is itself the answer when the query cannot be made.
/// </summary>
static class Elevation
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint TOKEN_QUERY = 0x0008;
    const int TokenElevation = 20;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int returned);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static string Describe(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return "no access";
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out var token))
                return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 5 ? "elevated?" : "token n/a";   // 5 = ACCESS_DENIED
            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _)
                    ? elevated != 0 ? "ELEVATED" : "not elevated"
                    : "token n/a";
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(h); }
    }
}
