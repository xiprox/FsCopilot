namespace FsCopilot.Simulation;

using System.Security.Cryptography;
using Connection;
using Network;

public class Coordinator : IDisposable
{
    // After this long without the peer, sync ends and held history is dropped.
    private static readonly TimeSpan DegradedTimeout = TimeSpan.FromMinutes(5);

    // 2000 events stay under a megabyte. The cap also bounds history held for an acker
    // that never returns.
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
    // Set by PeerLeft, cleared by any tick with a live link, so a third peer leaving
    // does not turn the next real outage into an end of sync.
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

        // Pointer-synced instruments are left out, or one press would actuate twice.
        _d.Add(_sim.Interactions
            .Where(i => !_ignore.Contains(i.Instrument) && !_pointer.Instruments.Contains(i.Instrument))
            .Subscribe(interact => _net.SendAll(interact)));
        _d.Add(_net.Stream<Interact>()
            .Where(i => !_pointer.Instruments.Contains(i.Instrument))
            .Subscribe(update => _sim.Set(update)));

        // Not gated on master, like Interact. Presses and drags share one stream, so
        // they arrive in capture order. Filtered on receive too, because a panel connects
        // before it learns its mode.
        _d.Add(panels.Events
            .Where(e => _pointer.Contains(e.Key))
            .Subscribe(e => SendPointer(e with { Session = _sessionId, Seq = NextSeq() })));
        _d.Add(_net.Stream<PointerEvent>()
            .Where(e => Fresh(e.Session, e.Seq))
            .Where(e => _pointer.Contains(e.Key))
            .Subscribe(panels.Send));
        _d.Add(_net.Stream<PointerAck>().Subscribe(OnAck));
        _d.Add(Observable.Interval(AckInterval).Subscribe(_ => SendAcks()));

        // live: a peer is connected. connecting: a join or handshake is in flight.
        // A dropped live link becomes degraded until the peer returns or the timeout ends it.
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

    public void EndSync()
    {
        lock (_stateLock)
        {
            _degradedTimer?.Dispose();
            _degradedTimer = null;
            _hadPeer = false;
            _peerLeft = false;
            // Forgotten too, so the departing peer's last tick is not read as a transition.
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
                    // Only on recovery. The receiver drops what it already applied.
                    if (recovered) ResendHistory();
                    break;

                case Link.Connecting:
                    // Mid-session this is an outage with a reconnect in flight, so the timeout runs.
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
            // Not before first contact: a new peer must not receive solo input.
            if (_hadPeer)
            {
                _history.Add(e);
                if (_history.Count > HistoryCap) _history.RemoveAt(0);
            }
        }
        _net.SendAll(e);
    }

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

    /// <summary>Only while live: an ack sent into a dropped link would be lost.</summary>
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

    /* Which panels the profile opted in. An entry without a '|' is an identifier and takes
     * every panel carrying it: the A220 declares DisplayUnits as ?config=[config], so the key
     * carries the livery and no profile can name it. An entry with one names a single panel,
     * for the aircraft that reuses an identifier across four. Routing is by full key either
     * way, so the left CTP reaches the left CTP. */
    private sealed class PointerFilter
    {
        public static readonly PointerFilter Empty = new([]);

        private readonly HashSet<string> _keys;
        private readonly HashSet<string> _identifiers;

        /// <summary>Interact carries the bare identifier, so the double-actuation guard
        /// matches on that.</summary>
        public HashSet<string> Instruments { get; }

        public PointerFilter(string[] entries)
        {
            _keys = [..entries.Where(e => e.Contains('|'))];
            _identifiers = [..entries.Where(e => !e.Contains('|'))];
            Instruments = entries.Select(Identifier).ToHashSet();
        }

        public bool Contains(string key) => _identifiers.Contains(Identifier(key)) || _keys.Contains(key);

        private static string Identifier(string key)
        {
            var i = key.IndexOf('|');
            return i < 0 ? key : key[..i];
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
