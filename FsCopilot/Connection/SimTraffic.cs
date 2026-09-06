namespace FsCopilot.Connection;

using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.FlightSimulator.SimConnect;

/// <summary>
/// A SimConnect connection of its own for AI traffic, beside the ones <see cref="SimClient"/>
/// opens for the user aircraft. Traffic needs three things those do not offer: one-shot jobs
/// (<see cref="Post"/>) rather than definitions replayed on reconnect, the sim's exceptions
/// named by the call that caused them (AI creates fail routinely on unknown titles), and the
/// per-frame drive of injected objects run synchronously inside the "Frame" event on this
/// thread. Closing the connection also removes every AI object it created, which is the
/// cleanup path of last resort.
///
/// Threading: every event here is raised on the SimTraffic thread, inside <c>ReceiveMessage</c>.
/// Handlers and posted jobs may call the <see cref="SimConnect"/> they are handed; nothing else
/// may touch it.
/// </summary>
public sealed class SimTraffic : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reconnectTask;
    private readonly BehaviorSubject<bool> _connected = new(false);

    private readonly List<Action<SimConnect>> _configure = [];
    private readonly Lock _cfgLock = new();
    private readonly Channel<Action<SimConnect>> _jobs =
        Channel.CreateUnbounded<Action<SimConnect>>(new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

    // Packet id → the name given to Call(); read and written on the SimTraffic thread only.
    private readonly Dictionary<uint, string> _sent = new();
    private const int SentCap = 4096;

    private readonly string _appName;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _openTimeout = TimeSpan.FromSeconds(5);

    private volatile bool _opened;
    private volatile bool _quit;

    public IObservable<bool> Connected => _connected.ObserveOn(TaskPoolScheduler.Default);
    public bool IsConnected => _connected.Value;

    /// <summary>MSFS 2024 reports application version 12; 2020 reports 11.</summary>
    public bool IsMsfs2024 { get; private set; }
    public string SimVersion { get; private set; } = "?";

    public event Action<SimConnect, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE>? ByType;
    public event Action<SimConnect, SIMCONNECT_RECV_SIMOBJECT_DATA>? ObjectData;
    public event Action<SimConnect, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID>? Assigned;
    public event Action<SimConnect, SIMCONNECT_RECV_ENUMERATE_SIMOBJECT_AND_LIVERY_LIST>? Liveries;
    /// <summary>Once per sim frame, with the sim's frame rate. Events can arrive in batches when
    /// the sim outpaces this thread, so a handler pacing motion per frame should count frames
    /// rather than read the clock.</summary>
    public event Action<SimConnect, float>? Frame;
    /// <summary>(name given to <see cref="Call"/>, the exception, the datum index the sim blamed).</summary>
    public event Action<string, SIMCONNECT_EXCEPTION, uint>? Exception;
    /// <summary>The connection is gone; every object it created went with it.</summary>
    public event Action? Disconnected;

    public SimTraffic(string appName)
    {
        _appName = appName;
        _reconnectTask = Task.Factory.StartNew(
            () => AutoReconnect(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _jobs.Writer.TryComplete();
        try { _reconnectTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _connected.OnCompleted();
        _cts.Dispose();
    }

    /// <summary>
    /// Run on every connection, now if connected and again after each reconnect, until the
    /// returned handle is disposed - the same contract as <see cref="SimConnectConsumer.Configure"/>.
    /// </summary>
    public IDisposable Configure(Action<SimConnect> configure, Action<SimConnect> deconfigure)
    {
        lock (_cfgLock) _configure.Add(configure);
        Post(configure);

        return Disposable.Create(() =>
        {
            lock (_cfgLock) _configure.Remove(configure);
            Post(deconfigure);
        });
    }

    /// <summary>
    /// Run once on the SimTraffic thread. Returns false, and runs nothing, while disconnected:
    /// a job queued for a connection that does not exist yet would be stale by the time it ran.
    /// </summary>
    public bool Post(Action<SimConnect> job)
    {
        if (!_connected.Value) return false;
        return _jobs.Writer.TryWrite(job);
    }

    /// <summary>
    /// Make a SimConnect call and remember its packet id, so that if the sim rejects it the
    /// <see cref="Exception"/> event can say which call it was. SimTraffic thread only.
    /// </summary>
    public void Call(SimConnect sim, string name, Action<SimConnect> call)
    {
        call(sim);
        var id = sim.GetLastSentPacketID();
        _sent[id] = name;
        if (_sent.Count <= SentCap) return;
        foreach (var old in _sent.Keys.Where(k => k < id - SentCap / 2).ToArray()) _sent.Remove(old);
    }

    private async Task AutoReconnect(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_reconnectDelay, ct);
                Connect(ct);
            }
            catch (OperationCanceledException) { /* ignore */ }
            catch (System.Exception) { /* ignore */ }
        }
    }

    private void Connect(CancellationToken ct)
    {
        try
        {
            using var evt = new AutoResetEvent(false);
            using var sim = new SimConnect(_appName, IntPtr.Zero, 0, evt, 0);
            _opened = false;
            _quit = false;
            _sent.Clear();

            sim.OnRecvOpen += (_, o) =>
            {
                IsMsfs2024 = o.dwApplicationVersionMajor >= 12;
                SimVersion = $"{o.dwApplicationVersionMajor}.{o.dwApplicationVersionMinor} build {o.dwApplicationBuildMajor}.{o.dwApplicationBuildMinor}";
                _opened = true;
            };
            sim.OnRecvQuit += (_, _) => _quit = true;
            sim.OnRecvException += (_, e) =>
            {
                var name = _sent.TryGetValue(e.dwSendID, out var n) ? n : $"send#{e.dwSendID}";
                Exception?.Invoke(name, (SIMCONNECT_EXCEPTION)e.dwException, e.dwIndex);
            };
            sim.OnRecvSimobjectDataBytype += (s, d) => ByType?.Invoke(s, d);
            sim.OnRecvSimobjectData += (s, d) => ObjectData?.Invoke(s, d);
            sim.OnRecvAssignedObjectId += (s, a) => Assigned?.Invoke(s, a);
            sim.OnRecvEnumerateSimobjectAndLiveryList += (s, l) => Liveries?.Invoke(s, l);
            sim.OnRecvEventFrame += (s, f) =>
            {
                if (f.uEventID == (uint)EVT.Frame) Frame?.Invoke(s, f.fFrameRate);
            };

            // The Open reply is the first message; the version in it is needed before
            // anything else, so wait for it rather than for the event alone.
            var opening = Stopwatch.StartNew();
            while (!_opened && opening.Elapsed < _openTimeout && !ct.IsCancellationRequested)
            {
                if (evt.WaitOne(50)) sim.ReceiveMessage();
            }
            if (!_opened) return;

            sim.SubscribeToSystemEvent(EVT.Frame, "Frame");
            _connected.OnNext(true);
            Log.Information("[Traffic] Connected ({Version}, MSFS {Sim})", SimVersion, IsMsfs2024 ? "2024" : "2020");

            lock (_cfgLock)
            {
                foreach (var action in _configure)
                {
                    try { action(sim); }
                    catch (System.Exception e) { Log.Error(e, "[Traffic] Initialization error"); }
                }

                // Configure() also posted each of these; the replay above already ran them.
                while (_jobs.Reader.TryRead(out _))
                {
                }
            }

            var stallWatch = Stopwatch.StartNew();
            var lastLoop = stallWatch.ElapsedMilliseconds;
            while (!ct.IsCancellationRequested && !_quit)
            {
                while (_jobs.Reader.TryRead(out var job))
                {
                    var t0 = stallWatch.ElapsedMilliseconds;
                    try { job(sim); }
                    catch (COMException) { return; }
                    catch (System.Exception e) { Log.Error(e, "[Traffic] Job error"); }
                    var took = stallWatch.ElapsedMilliseconds - t0;
                    if (took > 15) Log.Debug("[Traffic] slow job {Job} took {Ms} ms", job.Method.Name, took);
                }

                var waited = stallWatch.ElapsedMilliseconds;
                if (waited - lastLoop > 50) Log.Debug("[Traffic] thread stalled {Ms} ms between loops", waited - lastLoop);
                lastLoop = waited;

                if (!evt.WaitOne(5)) { lastLoop = stallWatch.ElapsedMilliseconds; continue; }

                var r0 = stallWatch.ElapsedMilliseconds;
                try { sim.ReceiveMessage(); }
                catch (COMException) { return; }
                catch (System.Exception e) { Log.Error(e, "[Traffic] Handler error"); }
                var rTook = stallWatch.ElapsedMilliseconds - r0;
                if (rTook > 15) Log.Debug("[Traffic] slow ReceiveMessage took {Ms} ms", rTook);
                lastLoop = stallWatch.ElapsedMilliseconds;
            }
        }
        finally
        {
            if (_connected.Value)
            {
                Log.Information("[Traffic] Disconnected");
                _connected.OnNext(false);
                try { Disconnected?.Invoke(); }
                catch (System.Exception e) { Log.Error(e, "[Traffic] Disconnect handler error"); }
            }
        }
    }

    // Beside SimConnectConsumer's AircraftLoaded = MaxValue - 1: far from any id SimClient allocates.
    private enum EVT : uint { Frame = uint.MaxValue - 2 }
}
