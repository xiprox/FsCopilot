namespace FsCopilot.Audio;

using Network;
using Simulation;

/// <summary>
/// The hosting side of ATC audio: which app to capture, and capturing it while this peer
/// hosts. The target is the user's preferred app if it is running, else the first known app
/// found; the process is watched, not the audio - loopback goes silent when its process
/// exits, so exit is detected by the process and capture re-attached when it returns.
/// Attaching to a silent app works, so nothing waits for ATC to speak first.
/// </summary>
public sealed class AtcHost : IDisposable
{
    public enum Phase { Waiting, Capturing, Closed, Unsupported }

    public sealed record Status(Phase Phase, string? App, bool MixerMuted);

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    private readonly INetwork _net;
    private readonly ShareSwitch _share;
    private readonly CompositeDisposable _d = new();
    private readonly BehaviorSubject<Status> _status = new(new Status(Phase.Waiting, null, false));
    private readonly BehaviorSubject<IReadOnlyList<AtcApps.App>> _detected = new([]);
    private readonly BehaviorSubject<IReadOnlyList<AtcApps.App>> _sessions = new([]);
    private readonly Lock _lock = new();
    private AtcCapture? _capture;
    private string? _captureExe;
    private string? _closedApp;
    private uint _seq;
    private volatile bool _active;
    private volatile bool _unsupported;

    /// <summary>Exe name the user picked, or null for whichever known app is running. A preference, not a lock.</summary>
    public string? PreferredApp { get; set; }
    /// <summary>While true, <see cref="Sessions"/> is refreshed: the "Choose another" list is open.</summary>
    public bool ShowSessions { get; set; }

    public IObservable<Status> CurrentStatus => _status.DistinctUntilChanged();
    public IObservable<IReadOnlyList<AtcApps.App>> Detected => _detected;
    public IObservable<IReadOnlyList<AtcApps.App>> Sessions => _sessions;

    public AtcHost(INetwork net, ShareSwitch share, Settings settings)
    {
        _net = net;
        _share = share;
        PreferredApp = settings.AtcApp;

        _d.Add(share.Host(ShareSwitch.Feature.Atc)
            .Subscribe(host =>
            {
                var active = host == share.SelfId;
                if (active == _active) return;
                _active = active;
                Log.Information("[Atc] Hosting {State}", active ? "started" : "stopped");
                if (!active) Detach();
                else Tick();
            }));

        _d.Add(Observable.Interval(Poll).StartWith(0L).Subscribe(_ => Tick()));
    }

    public void Dispose()
    {
        _d.Dispose();
        Detach();
        _status.OnCompleted();
        _detected.OnCompleted();
        _sessions.OnCompleted();
    }

    /// <summary>The app that would be, or is being, captured right now.</summary>
    public AtcApps.App? Target(IReadOnlyList<AtcApps.App> detected)
    {
        if (PreferredApp is { } preferred)
        {
            var p = AtcApps.Oldest(preferred);
            if (p is not null) return new AtcApps.App(preferred, p.Id, preferred);
        }
        return detected.Count > 0 ? detected[0] : null;
    }

    private void Tick()
    {
        try
        {
            var detected = AtcApps.DetectedKnown();
            _detected.OnNext(detected);
            if (ShowSessions) _sessions.OnNext(AtcApps.AudioSessionProcesses());

            lock (_lock)
            {
                if (!_active) return;

                if (_capture is { } capture)
                {
                    bool exited;
                    try { exited = Process.GetProcessById(capture.Pid).HasExited; }
                    catch { exited = true; }
                    var target = Target(detected);
                    if (!exited && target?.Pid == capture.Pid)
                    {
                        _status.OnNext(new Status(Phase.Capturing, _captureExe, capture.MixerMuted));
                        return;
                    }
                    Log.Information("[Atc] {App} {Why}", _captureExe, exited ? "closed" : "is no longer the target");
                    _closedApp = exited ? _captureExe : null;
                    Detach();
                }

                var next = Target(detected);
                if (next is null)
                {
                    _status.OnNext(_closedApp is { } closed ? new Status(Phase.Closed, closed, false) : new Status(Phase.Waiting, null, false));
                    return;
                }
                Attach(next);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "[Atc] Host tick failed");
        }
    }

    private void Attach(AtcApps.App app)
    {
        try
        {
            _capture = new AtcCapture(app.Pid, Send);
            _captureExe = app.Exe;
            _closedApp = null;
            _unsupported = false;
            Log.Information("[Atc] Capturing {App} (pid {Pid})", app.Exe, app.Pid);
            _status.OnNext(new Status(Phase.Capturing, app.Exe, false));
        }
        catch (Exception e)
        {
            // Process loopback needs Windows 10 2004 or later; anything else is logged once.
            if (!_unsupported) Log.Warning(e, "[Atc] Could not capture {App}", app.Exe);
            _unsupported = true;
            _capture = null;
            _status.OnNext(new Status(Phase.Unsupported, app.Exe, false));
        }
    }

    private void Detach()
    {
        lock (_lock)
        {
            _capture?.Dispose();
            _capture = null;
            _captureExe = null;
        }
    }

    private void Send(byte[] opus, int length)
    {
        var frame = new byte[length];
        Array.Copy(opus, frame, length);
        try { _net.SendAll(new AtcFrame(_share.SelfId, _seq++, frame), Delivery.Unreliable); }
        catch (Exception e) { Log.Error(e, "[Atc] Could not send a frame"); }
    }
}
