namespace FsCopilot.Simulation;

using System.Security.Cryptography;
using Connection;
using Network;

public class Coordinator : IDisposable
{
    // A dropped link is treated as a recoverable outage for this long; after that the
    // sync is considered over, held pointer history is discarded and slaves unlock.
    private static readonly TimeSpan DegradedTimeout = TimeSpan.FromMinutes(5);

    // Pointer history is everything the peers have not acknowledged yet (PointerAck):
    // the receiver acks its high-water mark every few seconds, the sender drops what
    // every acker has, and on recovery it resends the rest in Seq order. The receiver's
    // (Session, Seq) dedupe then covers the peer that kept running, and the ack floor
    // covers the one that restarted. The cap catches the acker that goes quiet for
    // good - a third peer that left mid-session keeps its last ack as the floor - and
    // is otherwise slack: ~60 B presses and <=2.4 KB drags keep 2000 under a megabyte.
    private const int HistoryCap = 2000;
    private static readonly TimeSpan AckInterval = TimeSpan.FromSeconds(3);

    private readonly INetwork _net;
    private readonly MasterSwitch _masterSwitch;
    private readonly SimClient _sim;
    private readonly PanelServer _panels;
    private readonly CompositeDisposable _d = new();
    private CompositeDisposable _cSubs = new();
    private HashSet<string> _ignore = [];
    private volatile PointerFilter _pointer = PointerFilter.Empty;

    private readonly ulong _sessionId;
    private int _pointerSeq;
    private readonly object _stateLock = new();
    private readonly List<PointerEvent> _history = [];        // our unacked events, Seq ascending
    private readonly Dictionary<ulong, uint> _acks = new();   // acker session -> last Seq of ours it has
    private readonly Dictionary<ulong, uint> _lastSeq = new(); // sender session -> last Seq applied
    private readonly Dictionary<ulong, uint> _acked = new();   // sender session -> last Seq we acked
    private bool _hadPeer;
    // A peer announced it was leaving. Spent when the link goes down, and cleared by
    // any tick that still shows a live link, so a third peer leaving a three-way
    // session does not turn the next real outage into a sync end.
    private bool _peerLeft;
    private Link _link = Link.None;
    private volatile string _syncState = SyncState.None;
    private IDisposable? _degradedTimer;

    public Coordinator(SimClient sim, INetwork net, MasterSwitch masterSwitch, PanelServer panels)
    {
        _net = net;
        _masterSwitch = masterSwitch;
        _sim = sim;
        _panels = panels;
        var sw = Stopwatch.StartNew();

        Span<byte> sessionBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(sessionBytes);
        var sessionId = BitConverter.ToUInt64(sessionBytes);
        _sessionId = sessionId;

        net.RegisterPacket<Update, Update.Codec>();
        net.RegisterPacket<Interact, InteractCodec>();

        _sim.Register<Physics>();
        _sim.Register<Surfaces>();
        _net.RegisterPacket<Physics, Physics.Codec>();
        _net.RegisterPacket<Surfaces, Surfaces.Codec>();
        _net.RegisterPacket<PointerEvent, PointerEvent.Codec>();
        _net.RegisterPacket<PointerAck, PointerAck.Codec>();

        _d.Add(sim.Aircraft.Take(1).Subscribe(_ => AddLink((ref Physics physics) =>
        {
            physics.SessionId = sessionId;
            physics.TimeMs = (uint)sw.ElapsedMilliseconds;
        })));

        _d.Add(sim.Aircraft.Take(1).Subscribe(_ => AddLink((ref Surfaces surfaces) =>
        {
            surfaces.SessionId = sessionId;
            surfaces.TimeMs = (uint)sw.ElapsedMilliseconds;
        })));

        // Instruments synced by pointer are excluded from the element-name path in both
        // directions - one press must not actuate twice.
        _d.Add(_sim.Interactions
            .Where(i => !_ignore.Contains(i.Instrument) && !_pointer.Instruments.Contains(i.Instrument))
            .Subscribe(interact => _net.SendAll(interact)));
        _d.Add(_net.Stream<Interact>()
            .Where(i => !_pointer.Instruments.Contains(i.Instrument))
            .Subscribe(update => _sim.Set(update)));

        // Pointer sync is symmetric like Interact - never gated on master. Outbound the
        // profile filter is the opt-in. Inbound it is what gates delivery: a panel
        // helloes its key before it has been told its mode, so the socket is registered
        // either way and routing alone would deliver into a panel still in events mode.
        // Presses and drags share one stream in each direction, so Seq follows capture
        // order and arrival order is delivery order - a second stream would be a second
        // scheduling hop and could overtake.
        _d.Add(panels.Events
            .Where(e => _pointer.Keys.Contains(e.Key))
            .Subscribe(e => SendPointer(e with { Session = _sessionId, Seq = NextSeq() })));
        _d.Add(_net.Stream<PointerEvent>()
            .Where(e => Fresh(e.Session, e.Seq))
            .Where(e => _pointer.Keys.Contains(e.Key))
            .Subscribe(panels.Send));
        // Acks mean "received by the app", not "applied by the panel"; app -> panel is
        // loopback and the panel's own missed counter covers that hop.
        _d.Add(_net.Stream<PointerAck>().Subscribe(OnAck));
        _d.Add(Observable.Interval(AckInterval).Subscribe(_ => SendAcks()));

        // The sync state machine behind the panel overlays. The link is what the
        // transport shows: live (a connected peer), connecting (a handshake or a join
        // in flight, nobody connected yet), or none. Sync follows it: none ->
        // connecting -> live on first contact; live -> degraded when the last peer
        // drops (connecting instead, if a reconnect is already in flight), back to live
        // on recovery with the unacked history resent, and to none when the outage
        // outlives the timeout. A failed first attempt goes connecting -> none, never
        // degraded: it was never live. An intentional Leave calls EndSync directly.
        _d.Add(Observable.CombineLatest(net.Peers, net.Connecting,
                (peers, joining) => peers.Any(p => p.Connected) ? Link.Live
                    : joining || peers.Count > 0 ? Link.Connecting
                    : Link.None)
            .Subscribe(OnLink));
        _d.Add(net.PeerLeft.Subscribe(_ => { lock (_stateLock) _peerLeft = true; }));
        _d.Add(masterSwitch.Master
            .DistinctUntilChanged()
            .Subscribe(_ => { lock (_stateLock) _panels.SetSync(_syncState, _masterSwitch.IsMaster); }));
    }

    public void Dispose()
    {
        _d.Dispose();
        _cSubs.Dispose();
    }

    public void Load(Definitions definitions)
    {
        if (!_cSubs.IsDisposed) _cSubs.Dispose();
        _ignore.Clear();
        _cSubs = new();
        foreach (var def in definitions) AddLink(def);
        foreach (var i in definitions.Ignore) _ignore.Add(i);
        _pointer = new PointerFilter(definitions.Pointer);
        _panels.Configure(definitions.Pointer);
    }

    /// <summary>The user left on purpose: no outage to bridge, so held pointer
    /// history is dropped and panels return to their resting state.</summary>
    public void EndSync()
    {
        lock (_stateLock)
        {
            _degradedTimer?.Dispose();
            _degradedTimer = null;
            _hadPeer = false;
            _peerLeft = false;
            // Forget the link too, so the next tick that still shows the departing peer
            // is not a transition, and the next live one is.
            _link = Link.None;
            _syncState = SyncState.None;
            _history.Clear();
            _acks.Clear();
            _panels.SetSync(_syncState, _masterSwitch.IsMaster);
        }
    }

    private enum Link { None, Connecting, Live }

    private void OnLink(Link link)
    {
        lock (_stateLock)
        {
            if (link == Link.Live) _peerLeft = false;
            if (link == _link) return;
            _link = link;
            switch (link)
            {
                case Link.Live:
                    _degradedTimer?.Dispose();
                    _degradedTimer = null;
                    var recovered = _hadPeer;
                    _hadPeer = true;
                    _syncState = SyncState.Live;
                    _panels.SetSync(_syncState, _masterSwitch.IsMaster);
                    // Recovery replays; first contact does not. Everything held is what
                    // no acker had when the link dropped - the slave was locked for the
                    // whole gap, so ordered replay reconstructs sync exactly, and a peer
                    // that kept running drops what it already applied by (Session, Seq).
                    if (recovered) ResendHistory();
                    break;

                case Link.Connecting:
                    // Before first contact the lock keeps input off the panels while
                    // there is no peer to send it to - up to ~10 s on the joiner. Mid-
                    // session it is an outage with a reconnect in flight, and the
                    // outage clock runs regardless of how the attempt ends.
                    _syncState = SyncState.Connecting;
                    _panels.SetSync(_syncState, _masterSwitch.IsMaster);
                    if (_hadPeer) StartDegradedTimer();
                    break;

                case Link.None:
                    if (!_hadPeer)
                    {
                        _syncState = SyncState.None;
                        _panels.SetSync(_syncState, _masterSwitch.IsMaster);
                        break;
                    }
                    if (_peerLeft)
                    {
                        // "Left", not "lost": no outage to bridge, nothing to wait for.
                        // Nothing else changes - the remaining pilot keeps the role they
                        // had, which is upstream's behaviour and a separate piece of work.
                        Log.Information("[Pointer] Peer left; sync ended");
                        EndSync();
                        break;
                    }
                    _syncState = SyncState.Degraded;
                    _panels.SetSync(_syncState, _masterSwitch.IsMaster);
                    StartDegradedTimer();
                    break;
            }
        }
    }

    private void StartDegradedTimer()
    {
        if (_degradedTimer != null) return;
        _degradedTimer = Observable.Timer(DegradedTimeout).Subscribe(_ =>
        {
            Log.Warning("[Pointer] Peer did not return within {Timeout}; sync ended, panels may be desynced", DegradedTimeout);
            EndSync();
        });
    }

    private uint NextSeq() => (uint)Interlocked.Increment(ref _pointerSeq);

    private void SendPointer(PointerEvent e)
    {
        lock (_stateLock)
        {
            // Nothing is held while sync has never been live: there is nobody to replay it to,
            // and a first contact must not receive the local pilot's solo input.
            if (_hadPeer)
            {
                _history.Add(e);
                if (_history.Count > HistoryCap) _history.RemoveAt(0);
            }
        }
        _net.SendAll(e);
    }

    /// <summary>A peer has our session up to ack.Seq: drop what every acker has.</summary>
    private void OnAck(PointerAck ack)
    {
        if (ack.Session != _sessionId) return;
        lock (_stateLock)
        {
            _acks[ack.From] = _acks.TryGetValue(ack.From, out var prev) ? Math.Max(prev, ack.Seq) : ack.Seq;
            var floor = _acks.Values.Min();
            var n = 0;
            while (n < _history.Count && _history[n].Seq <= floor) n++;
            if (n > 0) _history.RemoveRange(0, n);
        }
    }

    /// <summary>Acknowledges each sender session whose high-water mark moved since the
    /// last ack. Only while live: an ack sent into a dropped link is lost with it, and
    /// marking it as sent would leave the sender holding more than it needs to.</summary>
    private void SendAcks()
    {
        if (_syncState != SyncState.Live) return;
        List<PointerAck> due = [];
        lock (_lastSeq)
        {
            foreach (var (session, seq) in _lastSeq)
            {
                if (_acked.TryGetValue(session, out var acked) && acked == seq) continue;
                _acked[session] = seq;
                due.Add(new PointerAck(session, seq, _sessionId));
            }
        }
        foreach (var ack in due) _net.SendAll(ack);
    }

    private void ResendHistory()
    {
        PointerEvent[] entries;
        lock (_stateLock) entries = _history.OrderBy(e => e.Seq).ToArray();
        foreach (var e in entries) _net.SendAll(e);
        if (entries.Length > 0) Log.Debug("[Pointer] Re-sent {Count} unacknowledged events after reconnect", entries.Length);
    }

    private bool Fresh(ulong session, uint seq)
    {
        lock (_lastSeq)
        {
            if (_lastSeq.TryGetValue(session, out var last))
            {
                if (seq <= last) return false; // already applied; history re-sends overlap by design
                if (seq > last + 1)
                    Log.Warning("[Pointer] {Lost} events from the peer never arrived - panels may be desynced", seq - last - 1);
            }
            _lastSeq[session] = seq;
            return true;
        }
    }

    private sealed class PointerFilter
    {
        public static readonly PointerFilter Empty = new([]);

        public HashSet<string> Keys { get; }
        public HashSet<string> Instruments { get; }

        public PointerFilter(string[] keys)
        {
            Keys = [..keys];
            // pointer: entries are full keys (identifier|query); Interact carries the bare
            // identifier, so the double-actuation guard matches on the prefix.
            Instruments = keys
                .Select(k => { var i = k.IndexOf('|'); return i < 0 ? k : k[..i]; })
                .ToHashSet();
        }
    }

    private void AddLink<TPacket>(RefAction<TPacket> modify)
        where TPacket : unmanaged
    {
        _d.Add(_sim.Stream<TPacket>()
            .Sample(TimeSpan.FromMilliseconds(50), new EventLoopScheduler()) // 20 fps
            .Where(_ => _masterSwitch.IsMaster)
            .Subscribe(update =>
            {
                modify(ref update);
                try { _net.SendAll(update, true); }
                catch (Exception e) { Log.Error(e, "[Coordinator] Error while sending packet {Packet}", typeof(TPacket).Name); }
            }, ex => { Log.Fatal(ex, "[Coordinator] Error while processing a message from sim"); }));

        _d.Add(_net.Stream<TPacket>()
            .Where(_ => !_masterSwitch.IsMaster)
            .Subscribe(update =>
            {
                try { _sim.Set(update); }
                catch (Exception e) { Log.Error(e, "[Coordinator] Error processing packet {Packet}", typeof(TPacket).Name); }
            }, ex => { Log.Fatal(ex, "[Coordinator] Error while processing a message from client"); }));
    }

    private void AddLink(Definition def)
    {
        var master = !def.Shared;
        object? currentValue = null;
        var getVar = def.Get;

        var simRx = _sim.Stream(getVar, def.Units);
        if (master) simRx = simRx.Sample(TimeSpan.FromMilliseconds(30), DefaultScheduler.Instance); // 33 fps
        simRx = simRx
            .Do(value => currentValue = value)
            .Where(_ => !master || _masterSwitch.IsMaster);
        if (getVar[0] == 'H') simRx = simRx.Delay(TimeSpan.FromMilliseconds(500));
        if (!master) simRx = simRx.Where(_ => !Skip.Should(getVar));

        _cSubs.Add(simRx
            .Subscribe(value =>
            {
                if (!master && def.Skip != null) Skip.Next(def.Skip);
                _net.SendAll(new Update(getVar, value), unreliable: master);
                Log.Verbose("[PACKET] SENT {Name} {Value}", getVar, value);
            }));

        _cSubs.Add(_net.Stream<Update>()
            .Where(update => update.Name == getVar)
            .Do(update => Log.Verbose("[PACKET] RECV {Name} {Value}", getVar, update.Value))
            .Where(_ => !master || !_masterSwitch.IsMaster)
            .Subscribe(update =>
            {
                if (master)
                {
                    var set = def.ParseSet(update.Value, currentValue ?? update.Value, out var units, out var values);
                    if (values.Length == 0) return;
                    _sim.Set(set, units, values);
                }
                else
                {
                    var expression = def.Set(update.Value, currentValue ?? update.Value);
                    if (expression.Contains(">K:#"))
                    {
                        var set = def.ParseSet(update.Value, currentValue ?? update.Value, out var units, out var values);
                        if (values.Length == 0) return;
                        Skip.Next(getVar);
                        _sim.Set(set, units, values);
                    }
                    else
                    {
                        if ((expression.Contains(">K:") || expression.Contains(">B:"))
                            && expression.Contains("TOGGLE", StringComparison.OrdinalIgnoreCase)
                            && update.Value.Equals(currentValue)) return;
                        Skip.Next(getVar);
                        _sim.Execute(expression);
                    }
                }
            }));
    }

    private record Update(string Name, object Value)
    {
        public class Codec : IPacketCodec<Update>
        {
            public void Encode(Update packet, BinaryWriter bw)
            {
                bw.Write(packet.Name);
                var v = packet.Value;
                var type = Type.GetTypeCode(v.GetType());
                bw.Write((byte)type);
                switch (type)
                {
                    case TypeCode.String: bw.Write((string)v); break;
                    case TypeCode.Int32: bw.Write((int)v); break;
                    case TypeCode.Int64: bw.Write((long)v); break;
                    case TypeCode.Double: bw.Write((double)v); break;
                    case TypeCode.Single: bw.Write((float)v); break;
                    case TypeCode.Boolean: bw.Write((bool)v); break;
                    case TypeCode.Int16: bw.Write((short)v); break;
                    case TypeCode.UInt16: bw.Write((ushort)v); break;
                    case TypeCode.UInt32: bw.Write((uint)v); break;
                    case TypeCode.UInt64: bw.Write((ulong)v); break;
                    case TypeCode.Decimal: bw.Write((decimal)v); break;
                    case TypeCode.SByte: bw.Write((sbyte)v); break;
                    case TypeCode.Byte: bw.Write((byte)v); break;
                    default: throw new NotSupportedException( $"Data.Value type '{v.GetType().FullName}' is not supported by codec");
                }
            }

            public Update Decode(BinaryReader br)
            {
                var name = br.ReadString();
                var t = (TypeCode)br.ReadByte();
                object value = t switch
                {
                    TypeCode.String => br.ReadString(),
                    TypeCode.Int32 => br.ReadInt32(),
                    TypeCode.Int64 => br.ReadInt64(),
                    TypeCode.Double => br.ReadDouble(),
                    TypeCode.Single => br.ReadSingle(),
                    TypeCode.Boolean => br.ReadBoolean(),
                    TypeCode.Int16 => br.ReadInt16(),
                    TypeCode.UInt16 => br.ReadUInt16(),
                    TypeCode.UInt32 => br.ReadUInt32(),
                    TypeCode.UInt64 => br.ReadUInt64(),
                    TypeCode.Decimal => br.ReadDecimal(),
                    TypeCode.SByte => br.ReadSByte(),
                    TypeCode.Byte => br.ReadByte(),
                    TypeCode.DateTime => DateTime.FromBinary(br.ReadInt64()),
                    _ => throw new NotSupportedException($"Неизвестный TypeCode={t}")
                };

                return new(name, value);
            }
        }
    }

    private class InteractCodec : IPacketCodec<Interact>
    {
        public void Encode(Interact packet, BinaryWriter bw)
        {
            bw.Write(packet.Instrument);
            bw.Write(packet.Event);
            bw.Write(packet.Id);
            bw.Write(packet.Value != null);
            if (packet.Value != null) bw.Write(packet.Value);
        }

        public Interact Decode(BinaryReader br)
        {
            var instrument = br.ReadString();
            var @event = br.ReadString();
            var id = br.ReadString();
            var hasValue = br.ReadBoolean();
            var value = hasValue ? br.ReadString() : null;
            return new(instrument, @event, id, value);
        }
    }

    delegate void RefAction<T>(ref T value);
}
