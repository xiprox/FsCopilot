namespace FsCopilot.Simulation;

using System.Globalization;
using Connection;
using Microsoft.FlightSimulator.SimConnect;
using Network;

/// <summary>
/// The receiving side of traffic sharing: creates the host's objects in this sim, releases
/// them from the AI engine and freezes them so the sim stops simulating them, then writes
/// each one's interpolated pose on every sim frame. Removes them when the host says so, when
/// the host stops or leaves, or when an object goes quiet. Also sweeps its own sim for AI
/// aircraft it did not create, which is the "switch off your own traffic" warning. Every table
/// lives on the <see cref="SimTraffic"/> thread.
/// </summary>
public sealed class TrafficReceiver : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);     // three heartbeats
    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForeignEvery = TimeSpan.FromSeconds(10);
    private const uint RadiusMeters = 200_000;

    private enum EVT : uint { FreezeLatLon = 1, FreezeAlt, FreezeAtt }
    private enum GRP : uint { Highest = 1 }   // SIMCONNECT_GROUP_PRIORITY_HIGHEST

    private sealed class Live
    {
        public required TrafficIdentity Identity;
        public uint SimId;
        public bool Requested, Fallback, Failed;
        public long FailedAt, LastStateAt;
        public readonly TrafficInterpolator Interp = new();
        public Appearance? LastAppearance;
        public int LastEngines = -1;
        public bool Measuring;
        public ushort Index => Identity.Index;
    }

    private readonly SimTraffic _sim;
    private readonly ShareSwitch _share;
    private readonly TrafficOptions _opts;
    private readonly CompositeDisposable _d = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly BehaviorSubject<int> _foreign = new(0);

    private volatile string? _host;   // the foreign peer whose traffic we accept, or null

    // SimTraffic thread only.
    private readonly Dictionary<ushort, Live> _byIndex = new();
    private readonly Dictionary<uint, Live> _byRequest = new();
    private readonly Dictionary<uint, Live> _bySim = new();
    private readonly HashSet<uint> _own = [];
    private readonly Dictionary<ushort, (long Arrival, TrafficState State)> _pending = new();
    private HashSet<string>? _knownTitles;
    // Replay window over the host's batch sequence: the relay can deliver a batch twice, and
    // Unreliable delivery can reorder. A duplicate or a late straggler older than the window
    // is dropped here so it never reaches the interpolator or the delay estimate.
    private uint _lastSeq; private bool _haveSeq; private ulong _seenWindow; private int _duplicates;
    private const int SeqWindow = 64;
    // Host clock → our clock. The least-delayed packet seen is the best estimate; the offset
    // creeps upward slowly so a route that got faster is followed rather than pinned.
    private double _hostOffset; private bool _haveOffset;
    private const double OffsetCreep = 0.002;
    private int _gaps, _foreignSeen, _frames, _writes;
    private long _lastStatsAt;
    // Diagnostics (--debug): the read-back smoothness score of every mover, the spacing of the
    // sim's frame events, and GC pauses - the three that told the stutters apart.
    private readonly MotionScore _score = new();
    private TimeSpan _gcPause;
    private long _lastFrameWall; private double _frameDtSum, _frameDtSqSum, _frameDtMax = 0, _frameDtMin = double.MaxValue; private int _frameDtCount;

    /// <summary>Non-FSC AI aircraft seen in this sim while receiving; the warning when above zero.</summary>
    public IObservable<int> ForeignAiCount => _foreign.DistinctUntilChanged().ObserveOn(TaskPoolScheduler.Default);

    /// <summary>Sim object ids this receiver created. SimTraffic thread only; the host's poll skips them.</summary>
    public IReadOnlySet<uint> OwnObjectIds => _own;

    public TrafficReceiver(SimTraffic sim, INetwork net, ShareSwitch share, TrafficOptions opts)
    {
        _sim = sim;
        _share = share;
        _opts = opts;

        _d.Add(sim.Configure(s =>
        {
            DriveState.Define(s, TrafficDef.Drive);
            Appearance.Define(s, TrafficDef.Appearance);
            for (var i = 1; i <= 4; i++) EngineState.Define(s, (TrafficDef)((uint)TrafficDef.Eng1 + i - 1), i);
            ForeignProbe.Define(s, TrafficDef.Foreign);
            if (opts.Debug) MeasureProbe.Define(s, TrafficDef.Measure);
            s.MapClientEventToSimEvent(EVT.FreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET");
            s.MapClientEventToSimEvent(EVT.FreezeAlt, "FREEZE_ALTITUDE_SET");
            s.MapClientEventToSimEvent(EVT.FreezeAtt, "FREEZE_ATTITUDE_SET");
            _knownTitles = null;
            if (sim.IsMsfs2024)
            {
                _knownTitles = [];
                s.EnumerateSimObjectsAndLiveries(TrafficReq.Liveries, SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT);
                s.EnumerateSimObjectsAndLiveries(TrafficReq.Liveries, SIMCONNECT_SIMOBJECT_TYPE.HELICOPTER);
            }
        }, _ => { }));

        sim.Assigned += OnAssigned;
        sim.ByType += OnByType;
        sim.ObjectData += OnMeasured;
        sim.Liveries += OnLiveries;
        sim.Exception += OnException;
        sim.Frame += Render;
        sim.Disconnected += Clear;

        _d.Add(share.Host(ShareSwitch.Feature.Traffic)
            .Subscribe(host =>
            {
                var foreign = host is not null && host != share.SelfId ? host : null;
                if (foreign == _host) return;
                Log.Information("[Traffic] Receiving from {Host}", foreign ?? "nobody");
                _host = foreign;
                _foreign.OnNext(0);
                sim.Post(RemoveAll);   // whatever the previous host left, including on host change
            }));

        _d.Add(net.Stream<TrafficIdentity>().Subscribe(p => { if (p.Host == _host) sim.Post(s => OnIdentity(s, p)); }));
        _d.Add(net.Stream<TrafficRemove>().Subscribe(p => { if (p.Host == _host) sim.Post(s => OnRemove(s, p)); }));
        _d.Add(net.Stream<TrafficStates>().Subscribe(p =>
        {
            if (p.Host != _host) return;
            var arrival = _clock.ElapsedMilliseconds;
            sim.Post(s => OnStates(s, p, arrival));
        }));

        _d.Add(Observable.Interval(TimeSpan.FromSeconds(1)).Subscribe(_ => { if (_host is not null) sim.Post(Maintain); }));
        _d.Add(Observable.Interval(ForeignEvery).Subscribe(_ => { if (_host is not null) sim.Post(ForeignPoll); }));
    }

    public void Dispose()
    {
        _d.Dispose();
        _sim.Assigned -= OnAssigned;
        _sim.ByType -= OnByType;
        _sim.ObjectData -= OnMeasured;
        _sim.Liveries -= OnLiveries;
        _sim.Exception -= OnException;
        _sim.Frame -= Render;
        _sim.Disconnected -= Clear;
        _foreign.OnCompleted();
    }

    /// <summary>Remove every object this receiver created. Runs on the SimTraffic thread if connected.</summary>
    public void RemoveAll() => _sim.Post(RemoveAll);

    // -- packets ----------------------------------------------------------------------------

    private void OnIdentity(SimConnect sim, TrafficIdentity p)
    {
        if (p.Host != _host) return;
        if (_byIndex.TryGetValue(p.Index, out var existing))
        {
            existing.Identity = p;   // a re-send for a late joiner; we already have it
            return;
        }
        var live = new Live { Identity = p };
        _byIndex[p.Index] = live;
        if (_pending.Remove(p.Index, out var pending))
        {
            live.Interp.Push(pending.Arrival - pending.State.AgeMs, in pending.State);
            live.LastStateAt = _clock.ElapsedMilliseconds;
            Create(sim, live);
        }
    }

    private void OnRemove(SimConnect sim, TrafficRemove p)
    {
        if (p.Host != _host) return;
        if (_byIndex.Remove(p.Index, out var live)) Remove(sim, live, "host");
        _pending.Remove(p.Index);
    }

    private void OnStates(SimConnect sim, TrafficStates p, long arrival)
    {
        if (p.Host != _host) return;
        if (!Accept(p.Seq)) { _duplicates++; return; }

        var offset = arrival - (double)p.HostMs;
        if (!_haveOffset || offset < _hostOffset) { _hostOffset = offset; _haveOffset = true; }
        else _hostOffset += (offset - _hostOffset) * OffsetCreep;
        var sentAt = (long)(p.HostMs + _hostOffset);   // when the batch left the host, on our clock

        foreach (var raw in p.States)
        {
            var s = raw;
            if (_opts.HasOffset)
            {
                double lat = s.Lat, lon = s.Lon;
                _opts.Apply(ref lat, ref lon, s.Hdg);
                s = s with { Lat = lat, Lon = lon };
            }

            if (!_byIndex.TryGetValue(s.Index, out var live))
            {
                _pending[s.Index] = (sentAt, s);   // identity still on its way
                continue;
            }
            live.Interp.Push(sentAt - s.AgeMs, in s);
            live.LastStateAt = arrival;
            if (_opts.Debug && live.SimId != 0) Measure(sim, live, s.Gs > 1);
            if (live.SimId != 0) Dress(sim, live, in s);
            else if (!live.Requested && !live.Failed) Create(sim, live);
        }
    }

    /// <summary>True for a sequence number not seen before within the window; counts gaps.</summary>
    private bool Accept(uint seq)
    {
        if (!_haveSeq) { _haveSeq = true; _lastSeq = seq; _seenWindow = 1; return true; }
        if (seq > _lastSeq)
        {
            var advance = seq - _lastSeq;
            if (advance > 1) _gaps += (int)(advance - 1);
            _seenWindow = advance >= SeqWindow ? 1 : (_seenWindow << (int)advance) | 1;
            _lastSeq = seq;
            return true;
        }
        var back = _lastSeq - seq;
        if (back >= SeqWindow) return false;           // too old to matter
        var bit = 1UL << (int)back;
        if ((_seenWindow & bit) != 0) return false;    // duplicate
        _seenWindow |= bit;
        if (_gaps > 0) _gaps--;                         // a straggler filled a gap
        return true;
    }

    // -- objects ----------------------------------------------------------------------------

    private void Create(SimConnect sim, Live live)
    {
        if (live.Interp.Latest is not { } s) return;
        var id = live.Identity;
        var title = id.Title;
        var livery = id.Livery;
        if (live.Fallback || (_knownTitles is { Count: > 0 } known && !known.Contains(title)))
        {
            var fallback = TrafficFallbacks.For(id.Category, _sim.IsMsfs2024);
            if (fallback is null) { live.Failed = true; live.FailedAt = _clock.ElapsedMilliseconds; return; }
            if (!live.Fallback) Log.Information("[Traffic] {Title} is not installed here; using {Fallback}", title, fallback);
            live.Fallback = true;
            title = fallback;
            livery = "";
        }

        var init = new SIMCONNECT_DATA_INITPOSITION
        {
            Latitude = s.Lat, Longitude = s.Lon, Altitude = s.Alt,
            Pitch = s.Pitch, Bank = s.Bank, Heading = s.Hdg,
            OnGround = (uint)(s.OnGround ? 1 : 0), Airspeed = (uint)Math.Max(0, s.Gs)
        };
        var req = (TrafficReq)((uint)TrafficReq.CreateBase + id.Index);
        var tail = id.Tail.Length > 0 ? id.Tail : id.Index.ToString(CultureInfo.InvariantCulture);
        // Offset mode means another instance is hosting from this same sim; mark our copies so
        // its poll can tell them from the originals. Never in normal use: callsigns stay real.
        if (_opts.HasOffset) tail = TrafficOptions.CopyMarker + tail;
        _byRequest[(uint)req] = live;
        live.Requested = true;
        var ex1 = _sim.IsMsfs2024;
        _sim.Call(sim, $"Create {id.Index}", x =>
        {
            if (id.IsAircraft)
            {
                if (ex1) x.AICreateNonATCAircraft_EX1(title, livery, tail, init, req);
                else x.AICreateNonATCAircraft(title, tail, init, req);
            }
            else
            {
                if (ex1) x.AICreateSimulatedObject_EX1(title, livery, init, req);
                else x.AICreateSimulatedObject(title, init, req);
            }
        });
    }

    private void OnAssigned(SimConnect sim, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID a)
    {
        if (!_byRequest.Remove(a.dwRequestID, out var live)) return;
        if (_byIndex.GetValueOrDefault(live.Index) != live)
        {
            // Removed while the create was in flight.
            _sim.Call(sim, $"Remove {live.Index}", x => x.AIRemoveObject(a.dwObjectID, (TrafficReq)((uint)TrafficReq.RemoveBase + live.Index)));
            return;
        }
        live.SimId = a.dwObjectID;
        _bySim[a.dwObjectID] = live;
        _own.Add(a.dwObjectID);

        _sim.Call(sim, $"Release {live.Index}", x => x.AIReleaseControl(a.dwObjectID, (TrafficReq)((uint)TrafficReq.ReleaseBase + live.Index)));
        foreach (var evt in new[] { EVT.FreezeLatLon, EVT.FreezeAlt, EVT.FreezeAtt })
            _sim.Call(sim, $"{evt} {live.Index}", x => x.TransmitClientEvent(a.dwObjectID, evt, 1, GRP.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY));

        if (live.Interp.Latest is { } s)
        {
            _sim.Call(sim, $"Drive {live.Index}", x => x.SetDataOnSimObject(TrafficDef.Drive, a.dwObjectID, SIMCONNECT_DATA_SET_FLAG.DEFAULT, s.ToDrive()));
            Dress(sim, live, in s);
        }
        Log.Debug("[Traffic] + {Index} {Title}{Fallback}", live.Index, live.Identity.Title, live.Fallback ? " (fallback)" : "");
    }

    private void Dress(SimConnect sim, Live live, in TrafficState s)
    {
        if (!live.Identity.IsAircraft) return;   // vehicles reject every datum here
        var ap = new Appearance
        {
            GearHandle = s.GearPct / 100.0, FlapsIndex = s.FlapsIndex,
            LightStrobe = s.Lights & 1, LightLanding = s.Lights >> 1 & 1, LightTaxi = s.Lights >> 2 & 1,
            LightBeacon = s.Lights >> 3 & 1, LightNav = s.Lights >> 4 & 1
        };
        if (live.LastAppearance is not { } last || !last.Equals(ap))
        {
            live.LastAppearance = ap;
            var id = live.SimId;
            _sim.Call(sim, $"Appearance {live.Index}", x => x.SetDataOnSimObject(TrafficDef.Appearance, id, SIMCONNECT_DATA_SET_FLAG.DEFAULT, ap));
        }
        if (s.EngineCount == 0 || s.EngineMask == live.LastEngines) return;
        live.LastEngines = s.EngineMask;
        for (var i = 1; i <= Math.Min(4, s.EngineCount); i++)
        {
            var def = (TrafficDef)((uint)TrafficDef.Eng1 + i - 1);
            var state = new EngineState { Combustion = s.EngineMask >> (i - 1) & 1 };
            var id = live.SimId;
            _sim.Call(sim, $"Engine{i} {live.Index}", x => x.SetDataOnSimObject(def, id, SIMCONNECT_DATA_SET_FLAG.DEFAULT, state));
        }
    }

    /// <summary>
    /// Every sim frame: write each moving object's interpolated pose for this instant. The wall
    /// clock, deliberately: a render clock stepped by the sim's reported frame rate was tried
    /// and drifted from real time between re-syncs, which showed as a rhythmic jump that grew
    /// with speed.
    /// </summary>
    private void Render(SimConnect sim, float frameRate)
    {
        _frames++;
        var wall = _clock.ElapsedMilliseconds;
        if (_lastFrameWall != 0)
        {
            double dt = wall - _lastFrameWall;
            _frameDtSum += dt; _frameDtSqSum += dt * dt; _frameDtCount++;
            _frameDtMax = Math.Max(_frameDtMax, dt); _frameDtMin = Math.Min(_frameDtMin, dt);
        }
        _lastFrameWall = wall;
        if (_bySim.Count == 0) return;
        var now = wall;
        foreach (var live in _bySim.Values)
        {
            if (!live.Interp.TryRender(now, out var d)) continue;
            sim.SetDataOnSimObject(TrafficDef.Drive, live.SimId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, d);
            _writes++;
        }
    }

    private void Remove(SimConnect sim, Live live, string why)
    {
        if (live.SimId != 0)
        {
            var id = live.SimId;
            _bySim.Remove(id);
            _own.Remove(id);
            _sim.Call(sim, $"Remove {live.Index}", x => x.AIRemoveObject(id, (TrafficReq)((uint)TrafficReq.RemoveBase + live.Index)));
            live.SimId = 0;
            Log.Debug("[Traffic] - {Index} {Title} ({Why})", live.Index, live.Identity.Title, why);
        }
        else if (live.Requested)
        {
            // The create is in flight; OnAssigned sees the index is gone and removes it.
        }
    }

    private void RemoveAll(SimConnect sim)
    {
        foreach (var live in _byIndex.Values.ToArray()) Remove(sim, live, "all");
        _byIndex.Clear();
        _pending.Clear();
        _haveSeq = false;
        _haveOffset = false;
        _seenWindow = 0;
    }

    private void Clear()
    {
        // The connection is gone and took every object with it.
        _byIndex.Clear();
        _byRequest.Clear();
        _bySim.Clear();
        _own.Clear();
        _pending.Clear();
        _haveSeq = false;
    }

    // -- upkeep -----------------------------------------------------------------------------

    private void Maintain(SimConnect sim)
    {
        var now = _clock.ElapsedMilliseconds;
        foreach (var live in _byIndex.Values.ToArray())
        {
            if (live.SimId != 0 && now - live.LastStateAt > StaleAfter.TotalMilliseconds)
            {
                _byIndex.Remove(live.Index);
                Remove(sim, live, "stale");
            }
            else if (live.Failed && now - live.FailedAt > RetryFailedAfter.TotalMilliseconds)
            {
                live.Failed = false;
                live.Requested = false;
                live.Fallback = false;
                Create(sim, live);
            }
        }
        foreach (var (index, pending) in _pending.ToArray())
            if (now - pending.Arrival > PendingFor.TotalMilliseconds) _pending.Remove(index);

        if (now - _lastStatsAt >= 30_000)
        {
            _lastStatsAt = now;
            Log.Debug("[Traffic] receiving: {Objects} objects ({Failed} failed, {Fallback} fallback), {Gaps} packet gaps, {Dup} duplicates, {Foreign} foreign, {Frames} frames, {Writes} writes",
                _bySim.Count, _byIndex.Values.Count(l => l.Failed), _byIndex.Values.Count(l => l.Fallback), _gaps, _duplicates, _foreign.Value, _frames, _writes);
            _duplicates = 0;
            if (_opts.Debug && _frameDtCount > 0)
            {
                var mean = _frameDtSum / _frameDtCount;
                var sd = Math.Sqrt(Math.Max(0, _frameDtSqSum / _frameDtCount - mean * mean));
                var pause = GC.GetTotalPauseDuration();
                Log.Debug("[Traffic] frames: {Mean:0.0} ms mean, {Sd:0.0} ms sd, {Min:0} min, {Max:0} max; {Score}; GC gen0/1/2 {G0}/{G1}/{G2}, paused {Pause:0} ms of the last 30 s",
                    mean, sd, _frameDtMin, _frameDtMax, _score.Report(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), (pause - _gcPause).TotalMilliseconds);
                _gcPause = pause;
                _frameDtSum = 0; _frameDtSqSum = 0; _frameDtCount = 0; _frameDtMax = 0; _frameDtMin = double.MaxValue;
            }
            _gaps = 0; _frames = 0; _writes = 0;
        }
    }

    // -- diagnostics ------------------------------------------------------------------------

    /// <summary>Read a mover's position back every frame while it moves, for the smoothness score.</summary>
    private void Measure(SimConnect sim, Live live, bool moving)
    {
        if (moving == live.Measuring) return;
        live.Measuring = moving;
        var period = moving ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER;
        var id = live.SimId;
        _sim.Call(sim, $"Measure {live.Index}", x => x.RequestDataOnSimObject((TrafficReq)((uint)TrafficReq.MeasureBase + live.Index), TrafficDef.Measure, id, period, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0));
        if (!moving) _score.Forget(id);
    }

    private void OnMeasured(SimConnect sim, SIMCONNECT_RECV_SIMOBJECT_DATA d)
    {
        if (d.dwRequestID < (uint)TrafficReq.MeasureBase || d.dwData is not { Length: > 0 }) return;
        var m = (MeasureProbe)d.dwData[0];
        _score.Add(d.dwObjectID, m.Lat, m.Lon);
        if (m.FreezeLatLon == 0 || m.FreezeAlt == 0 || m.FreezeAtt == 0)
            Log.Warning("[Traffic] Object {Id} is not frozen ({LatLon}{Alt}{Att})", d.dwObjectID, m.FreezeLatLon, m.FreezeAlt, m.FreezeAtt);
    }

    private void ForeignPoll(SimConnect sim)
    {
        _foreignSeen = 0;
        _sim.Call(sim, "Poll Foreign", x => x.RequestDataOnSimObjectType(TrafficReq.Foreign, TrafficDef.Foreign, RadiusMeters, SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT));
    }

    private void OnByType(SimConnect sim, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE d)
    {
        if (d.dwRequestID != (uint)TrafficReq.Foreign) return;
        if (d.dwoutof == 0 || d.dwData is not { Length: > 0 }) { _foreign.OnNext(0); return; }
        var probe = (ForeignProbe)d.dwData[0];
        if (probe.IsUser == 0 && !_own.Contains(d.dwObjectID)) _foreignSeen++;
        if (d.dwentrynumber >= d.dwoutof) _foreign.OnNext(_host is null ? 0 : _foreignSeen);
    }

    private void OnLiveries(SimConnect sim, SIMCONNECT_RECV_ENUMERATE_SIMOBJECT_AND_LIVERY_LIST list)
    {
        if (list.dwRequestID != (uint)TrafficReq.Liveries || _knownTitles is null) return;
        foreach (var entry in list.rgData)
            if (entry is SIMCONNECT_ENUMERATE_SIMOBJECT_LIVERY livery && !string.IsNullOrEmpty(livery.AircraftTitle))
                _knownTitles.Add(livery.AircraftTitle);
        if (list.dwEntryNumber >= list.dwOutOf - 1)
            Log.Debug("[Traffic] {Count} aircraft titles installed", _knownTitles.Count);
    }

    private void OnException(string call, SIMCONNECT_EXCEPTION ex, uint index)
    {
        if (!call.StartsWith("Create ")) { Log.Warning("[Traffic] {Call}: {Exception} (index {Index})", call, ex, index); return; }
        if (!ushort.TryParse(call.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var objIndex)) return;
        if (!_byIndex.TryGetValue(objIndex, out var live)) return;
        _byRequest.Remove((uint)TrafficReq.CreateBase + objIndex);
        live.Requested = false;
        if (!live.Fallback)
        {
            Log.Information("[Traffic] Could not create {Title} ({Exception}); trying the fallback", live.Identity.Title, ex);
            live.Fallback = true;
            _sim.Post(s => Create(s, live));
        }
        else
        {
            Log.Warning("[Traffic] Could not create {Title} even as a fallback ({Exception})", live.Identity.Title, ex);
            live.Failed = true;
            live.FailedAt = _clock.ElapsedMilliseconds;
        }
    }
}
