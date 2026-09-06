using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace Traffic.Sim;

/// <summary>
/// One SimConnect session on the calling thread: open, then <see cref="Run"/> pumps messages and
/// calls <paramref name="tick"/> between them until the sim quits or the token cancels. Every
/// exception the sim reports is surfaced with the name of the call that caused it, because the
/// managed wrapper otherwise reduces them to a number.
/// </summary>
public sealed class SimLink : IDisposable
{
    private readonly AutoResetEvent _event = new(false);
    private readonly Dictionary<uint, string> _sent = new();
    private readonly SimConnect _sim;
    private bool _quit;

    public SimConnect Sim => _sim;
    public string SimName { get; private set; } = "?";
    public uint SimMajor { get; private set; }
    public string SimVersion { get; private set; } = "?";
    public bool IsMsfs2024 => SimMajor >= 12;

    public event Action<string, SIMCONNECT_EXCEPTION, uint>? Exception;

    // The sim's own frame rate, from the "Frame" system event, averaged since the last read.
    private double _fpsSum; private int _fpsCount; private double _fpsMin = double.MaxValue;
    private enum LinkEvt : uint { Frame = 0xFFFF0001 }

    /// <summary>Fires once per sim frame, after <see cref="EnableFrameRate"/>.</summary>
    public event Action? Frame;

    /// <summary>Start receiving the sim's frame rate; read it with <see cref="TakeFps"/>.</summary>
    public void EnableFrameRate()
    {
        _sim.OnRecvEventFrame += (_, f) =>
        {
            _fpsSum += f.fFrameRate; _fpsCount++; if (f.fFrameRate < _fpsMin) _fpsMin = f.fFrameRate;
            Frame?.Invoke();
        };
        Call("Subscribe Frame", s => s.SubscribeToSystemEvent(LinkEvt.Frame, "Frame"));
    }

    /// <summary>Average and minimum frame rate since the previous call, then reset.</summary>
    public (double Avg, double Min, int Frames) TakeFps()
    {
        var r = (_fpsCount > 0 ? _fpsSum / _fpsCount : 0, _fpsCount > 0 ? _fpsMin : 0, _fpsCount);
        _fpsSum = 0; _fpsCount = 0; _fpsMin = double.MaxValue;
        return r;
    }

    public SimLink(string appName)
    {
        _sim = new SimConnect(appName, IntPtr.Zero, 0, _event, 0);
        _sim.OnRecvOpen += (_, o) =>
        {
            SimName = o.szApplicationName;
            SimMajor = o.dwApplicationVersionMajor;
            SimVersion = $"{o.dwApplicationVersionMajor}.{o.dwApplicationVersionMinor} build {o.dwApplicationBuildMajor}.{o.dwApplicationBuildMinor}";
        };
        _sim.OnRecvQuit += (_, _) => _quit = true;
        _sim.OnRecvException += (_, e) =>
        {
            var call = _sent.TryGetValue(e.dwSendID, out var name) ? name : $"send#{e.dwSendID}";
            Exception?.Invoke(call, (SIMCONNECT_EXCEPTION)e.dwException, e.dwIndex);
        };
    }

    /// <summary>Blocks until the sim answers the open, or the timeout passes.</summary>
    public bool WaitOpen(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_event.WaitOne(50)) _sim.ReceiveMessage();
            if (SimMajor != 0) return true;
        }
        return false;
    }

    /// <summary>Run a SimConnect call and remember its packet id so an exception can be named.</summary>
    public void Call(string name, Action<SimConnect> call)
    {
        call(_sim);
        var id = _sim.GetLastSentPacketID();
        _sent[id] = name;
        if (_sent.Count > 4096)
            foreach (var old in _sent.Keys.Where(k => k < id - 2048).ToArray()) _sent.Remove(old);
    }

    public void Run(Action tick, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_quit)
        {
            try
            {
                if (_event.WaitOne(5)) _sim.ReceiveMessage();
                tick();
            }
            catch (COMException)
            {
                return; // the sim went away
            }
        }
    }

    public void Dispose()
    {
        try { _sim.Dispose(); } catch { /* ignore */ }
        _event.Dispose();
    }
}
