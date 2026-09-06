namespace FsCopilot.Simulation;

using Connection;
using Microsoft.FlightSimulator.SimConnect;
using Network;

/// <summary>
/// The hosting side of traffic sharing: polls the sim for every AI aircraft within 200 km (the
/// SDK's cap, and further than anyone looks), diffs consecutive polls for arrivals and
/// departures, reads each newcomer's identity once, and sends what the <see cref="TrafficGate"/>
/// lets through in batches. Active while this peer is the traffic host. Every table lives on
/// the <see cref="SimTraffic"/> thread; the network and the timers post in.
/// </summary>
public sealed class TrafficHost : IDisposable
{
    private const int PollMs = 500;
    private const uint RadiusMeters = 200_000;
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LiveryWait = TimeSpan.FromSeconds(2);
    private const int StatsEveryMs = 30_000;

    private sealed class Tracked
    {
        public ushort Index;
        public readonly TrafficGate Gate = new(Heartbeat);
        public bool IsUser;
        public TrafficIdentity? Identity;
        public ObjectIdentity? Raw;         // identity waiting for its livery reply on 2024
        public long RawAt;
        public bool LiveryKnown;
        public string Livery = "";
    }

    private sealed class Poll
    {
        public long StartedAt;
        public bool Complete;
    }

    private readonly SimTraffic _sim;
    private readonly INetwork _net;
    private readonly ShareSwitch _share;
    private readonly TrafficReceiver _receiver;
    private readonly TrafficOptions _opts;
    private readonly CompositeDisposable _d = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private volatile bool _active;

    // SimTraffic thread only.
    private readonly Dictionary<uint, Tracked> _tracked = new();
    private readonly HashSet<uint> _seenThisPoll = [];
    private readonly Dictionary<uint, Poll> _polls = new();
    // States waiting to go out, each with the moment it was read: a batch is stamped when it
    // is sent, and the sample's age must count from the read, not from the send, or every
    // segment inherits the poll's 0-90 ms of scatter as a speed wobble.
    private readonly List<(TrafficState State, long ReadAt)> _batch = [];
    private ushort _nextIndex;
    private uint _seq;
    private long _lastStatsAt;
    private int _samplesSeen, _samplesSent, _pollsDone, _pollsIncomplete;
    private double _pollMsSum, _pollMsMax;

    public TrafficHost(SimTraffic sim, INetwork net, ShareSwitch share, TrafficReceiver receiver, TrafficOptions opts)
    {
        _sim = sim;
        _net = net;
        _share = share;
        _receiver = receiver;
        _opts = opts;

        _d.Add(sim.Configure(s =>
        {
            ObjectState.Define(s, TrafficDef.State);
            ObjectIdentity.Define(s, TrafficDef.Identity);
            if (sim.IsMsfs2024) LiveryProbe.Define(s, TrafficDef.Livery);
        }, _ => { }));

        sim.ByType += OnByType;
        sim.ObjectData += OnObjectData;
        sim.Exception += OnException;
        sim.Disconnected += Clear;

        _d.Add(share.Host(ShareSwitch.Feature.Traffic)
            .Subscribe(host =>
            {
                var active = host == share.SelfId;
                if (active == _active) return;
                _active = active;
                Log.Information("[Traffic] Hosting {State}", active ? "started" : "stopped");
                if (!active) sim.Post(_ => Clear());
            }));

        _d.Add(Observable.Interval(TimeSpan.FromMilliseconds(PollMs))
            .Subscribe(_ => { if (_active) sim.Post(PollTick); }));

        _d.Add(share.PeerJoined
            .Subscribe(_ => { if (_active) sim.Post(_ => ResendAll()); }));
    }

    public void Dispose()
    {
        _d.Dispose();
        _sim.ByType -= OnByType;
        _sim.ObjectData -= OnObjectData;
        _sim.Exception -= OnException;
        _sim.Disconnected -= Clear;
    }

    // -- polling ----------------------------------------------------------------------------

    private void PollTick(SimConnect sim)
    {
        var now = _clock.ElapsedMilliseconds;
        FinishPoll(now, forced: true);
        StartPoll(sim, now);
        if (now - _lastStatsAt >= StatsEveryMs) Stats(now);
    }

    private void StartPoll(SimConnect sim, long now)
    {
        _polls.Clear();
        _seenThisPoll.Clear();
        Request(sim, TrafficReq.PollAircraft, SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT, now);
        Request(sim, TrafficReq.PollHelicopter, SIMCONNECT_SIMOBJECT_TYPE.HELICOPTER, now);
        if (_opts.Ground) Request(sim, TrafficReq.PollGround, SIMCONNECT_SIMOBJECT_TYPE.GROUND, now);
    }

    private void Request(SimConnect sim, TrafficReq req, SIMCONNECT_SIMOBJECT_TYPE type, long now)
    {
        _polls[(uint)req] = new Poll { StartedAt = now };
        _sim.Call(sim, $"Poll {type}", s => s.RequestDataOnSimObjectType(req, TrafficDef.State, RadiusMeters, type));
    }

    /// <summary>Close the poll in flight: objects that did not appear in a complete poll are gone.</summary>
    private void FinishPoll(long now, bool forced)
    {
        if (_polls.Count == 0) return;
        if (!_polls.Values.All(p => p.Complete))
        {
            if (!forced) return;
            // An incomplete poll says nothing about who is gone; skip the diff.
            _pollsIncomplete++;
            _polls.Clear();
            Flush();
            return;
        }

        foreach (var (id, tracked) in _tracked.ToArray())
        {
            if (_seenThisPoll.Contains(id)) continue;
            _tracked.Remove(id);
            if (tracked.Identity is not null) Send(new TrafficRemove(_share.SelfId, tracked.Index), reliable: true);
        }
        _polls.Clear();
        Flush();
    }

    private void OnByType(SimConnect sim, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE d)
    {
        if (!_active) return;
        if (d.dwRequestID == (uint)TrafficReq.Foreign) return;   // the receiver's poll, same connection
        if (!_polls.TryGetValue(d.dwRequestID, out var poll)) return;

        var now = _clock.ElapsedMilliseconds;
        if (d.dwoutof == 0 || d.dwData is not { Length: > 0 })
        {
            Complete(poll, now);
            return;
        }

        var st = (ObjectState)d.dwData[0];
        var id = d.dwObjectID;
        _samplesSeen++;
        _seenThisPoll.Add(id);

        if (!_tracked.TryGetValue(id, out var tracked))
        {
            tracked = new Tracked { Index = _nextIndex++, IsUser = st.IsUser != 0 };
            _tracked[id] = tracked;
            if (!tracked.IsUser && !_receiver.OwnObjectIds.Contains(id))
            {
                _sim.Call(sim, $"Identity {id}", s => s.RequestDataOnSimObject(TrafficReq.Identity, TrafficDef.Identity, id, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0));
                if (_sim.IsMsfs2024)
                    _sim.Call(sim, $"Livery {id}", s => s.RequestDataOnSimObject(TrafficReq.Livery, TrafficDef.Livery, id, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0));
                else tracked.LiveryKnown = true;
            }
        }

        // Identity precedes any state; a livery that never answers is given up on after a while.
        if (tracked.Identity is null && tracked.Raw is { } raw && now - tracked.RawAt > LiveryWait.TotalMilliseconds)
            Announce(tracked, raw);

        if (tracked.Identity is not null && !tracked.IsUser)
        {
            Span<(ObjectState State, ushort AgeMs)> decided = stackalloc (ObjectState, ushort)[2];
            var n = tracked.Gate.Decide(in st, now, decided);
            for (var i = 0; i < n; i++)
            {
                _batch.Add((TrafficState.From(in decided[i].State, decided[i].AgeMs) with { Index = tracked.Index }, now));
                _samplesSent++;
                if (_batch.Count >= TrafficStates.MaxPerPacket) Flush();
            }
        }

        if (d.dwentrynumber >= d.dwoutof) Complete(poll, now);
    }

    private void Complete(Poll poll, long now)
    {
        if (poll.Complete) return;
        poll.Complete = true;
        var ms = now - poll.StartedAt;
        _pollMsSum += ms;
        _pollMsMax = Math.Max(_pollMsMax, ms);
        _pollsDone++;
        if (_polls.Values.All(p => p.Complete)) FinishPoll(now, forced: false);
    }

    // -- identity ---------------------------------------------------------------------------

    private void OnObjectData(SimConnect sim, SIMCONNECT_RECV_SIMOBJECT_DATA d)
    {
        if (!_active || d.dwData is not { Length: > 0 }) return;
        if (!_tracked.TryGetValue(d.dwObjectID, out var tracked)) return;

        switch ((TrafficReq)d.dwRequestID)
        {
            case TrafficReq.Identity:
                var raw = (ObjectIdentity)d.dwData[0];
                if (raw.IsUser != 0) { tracked.IsUser = true; return; }
                // A copy injected into this same sim by another instance in offset mode.
                if (raw.AtcId is not null && raw.AtcId.StartsWith(TrafficOptions.CopyMarker, StringComparison.Ordinal)) { tracked.IsUser = true; return; }
                tracked.Raw = raw;
                tracked.RawAt = _clock.ElapsedMilliseconds;
                if (tracked.LiveryKnown) Announce(tracked, raw);
                break;
            case TrafficReq.Livery:
                tracked.Livery = ((LiveryProbe)d.dwData[0]).LiveryName ?? "";
                tracked.LiveryKnown = true;
                if (tracked.Identity is null && tracked.Raw is { } pending) Announce(tracked, pending);
                break;
        }
    }

    private void Announce(Tracked tracked, in ObjectIdentity raw)
    {
        tracked.Identity = new TrafficIdentity(
            _share.SelfId, tracked.Index,
            raw.Title ?? "", tracked.Livery, raw.AtcId ?? "", raw.AtcAirline ?? "", raw.AtcFlightNumber ?? "", raw.AtcModel ?? "",
            TrafficIdentity.CategoryOf(raw.Category ?? ""));
        tracked.Raw = null;
        Send(tracked.Identity, reliable: true);
        Log.Debug("[Traffic] + {Index} {Title} [{Tail}]", tracked.Index, tracked.Identity.Title, tracked.Identity.Tail);
    }

    /// <summary>A peer joined: it has none of the identities and needs a full state pass.</summary>
    private void ResendAll()
    {
        var n = 0;
        foreach (var tracked in _tracked.Values)
        {
            if (tracked.Identity is null) continue;
            Send(tracked.Identity, reliable: true);
            tracked.Gate.ForceResend();
            n++;
        }
        Log.Information("[Traffic] Re-sent {Count} identities for a new peer", n);
    }

    // -- sending ----------------------------------------------------------------------------

    private void Flush()
    {
        if (_batch.Count == 0) return;
        var now = _clock.ElapsedMilliseconds;
        var states = new TrafficState[_batch.Count];
        for (var i = 0; i < states.Length; i++)
        {
            var (state, readAt) = _batch[i];
            states[i] = state with { AgeMs = (ushort)Math.Min(state.AgeMs + (now - readAt), ushort.MaxValue) };
        }
        Send(new TrafficStates(_share.SelfId, _seq++, (uint)now, states), reliable: false);
        _batch.Clear();
    }

    private void Send<T>(T packet, bool reliable) where T : notnull
    {
        // States carry their own sequence numbers, so they go Unreliable rather than Sequenced:
        // on a shared sequenced channel a state arriving after a newer physics packet is dropped,
        // which over a relay was a quarter of them.
        try { _net.SendAll(packet, reliable ? Delivery.Reliable : Delivery.Unreliable); }
        catch (Exception e) { Log.Error(e, "[Traffic] Could not send {Packet}", typeof(T).Name); }
    }

    private void OnException(string call, SIMCONNECT_EXCEPTION ex, uint index)
    {
        if (!_active) return;
        if (call.StartsWith("Poll ") || call.StartsWith("Identity ") || call.StartsWith("Livery "))
            Log.Warning("[Traffic] {Call}: {Exception} (index {Index})", call, ex, index);
    }

    private void Clear()
    {
        _tracked.Clear();
        _seenThisPoll.Clear();
        _polls.Clear();
        _batch.Clear();
    }

    private void Stats(long now)
    {
        _lastStatsAt = now;
        var objects = _tracked.Values.Count(t => t.Identity is not null);
        Log.Debug("[Traffic] host: {Objects} objects, polls {Polls} (avg {Avg:0.0} ms, max {Max:0} ms, {Incomplete} incomplete), sent {Sent}/{Seen} samples ({Pct:0}%)",
            objects, _pollsDone, _pollsDone > 0 ? _pollMsSum / _pollsDone : 0, _pollMsMax, _pollsIncomplete,
            _samplesSent, _samplesSeen, _samplesSeen > 0 ? 100.0 * _samplesSent / _samplesSeen : 0);
        _pollsDone = 0; _pollsIncomplete = 0; _pollMsSum = 0; _pollMsMax = 0; _samplesSeen = 0; _samplesSent = 0;
    }
}
