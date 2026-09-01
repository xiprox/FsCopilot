namespace FsCopilot.Simulation;

using System.Security.Cryptography;
using Connection;
using Network;

public class Coordinator : IDisposable
{
    // A dropped link is treated as a recoverable outage for this long; after that the
    // session is considered over, held pointer history is discarded and slaves unlock.
    private static readonly TimeSpan DegradedTimeout = TimeSpan.FromMinutes(5);

    // Pointer history kept while the session is live, to cover reconnect races: per key,
    // bounded by count and, on re-send, by age. While degraded, everything from the
    // outage start is kept instead (bounded by DegradedTimeout ending the session), so
    // a slave that was locked for the whole gap replays the master's inputs completely.
    private const int LiveHistoryPerKey = 50;
    private const int DegradedHistoryPerKey = 2000;
    private static readonly TimeSpan LiveHistoryMaxAge = TimeSpan.FromSeconds(60);

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
    private readonly Dictionary<string, List<(object Packet, DateTime At)>> _history = new();
    private readonly Dictionary<ulong, uint> _lastSeq = new();
    private bool _accumulate;
    private bool _hadPeer;
    private string _session = SessionState.None;
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
        _net.RegisterPacket<PointerPress, PointerPress.Codec>();
        _net.RegisterPacket<PointerDrag, PointerDrag.Codec>();

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
            .Where(i => !_ignore.Contains(i.Instrument) && !_pointer.Instruments.Contains(i.Instrument))
            .Subscribe(update => _sim.Set(update)));

        // Pointer sync is symmetric like Interact - never gated on master. The profile
        // filter runs on both ends: outbound it is the opt-in, inbound it defends
        // against a peer whose profile differs.
        _d.Add(panels.Presses
            .Where(p => _pointer.Keys.Contains(p.Key))
            .Subscribe(p => SendPointer(p with { Session = _sessionId, Seq = NextSeq() })));
        _d.Add(panels.Drags
            .Where(d => _pointer.Keys.Contains(d.Key))
            .Subscribe(d => SendPointer(d with { Session = _sessionId, Seq = NextSeq() })));
        _d.Add(_net.Stream<PointerPress>()
            .Where(p => Fresh(p.Session, p.Seq))
            .Where(p => _pointer.Keys.Contains(p.Key))
            .Subscribe(panels.Send));
        _d.Add(_net.Stream<PointerDrag>()
            .Where(d => Fresh(d.Session, d.Seq))
            .Where(d => _pointer.Keys.Contains(d.Key))
            .Subscribe(panels.Send));

        // The session state machine behind the panel overlays: none -> live on the first
        // peer, live -> degraded when the last peer drops, back to live on reconnect
        // (re-sending held history), and degraded -> none when the outage outlives the
        // timeout. An intentional Leave calls EndSession directly.
        _d.Add(net.Peers
            .Select(peers => peers.Count > 0)
            .DistinctUntilChanged()
            .Subscribe(OnPeersChanged));
        _d.Add(masterSwitch.Master
            .DistinctUntilChanged()
            .Subscribe(_ => { lock (_stateLock) _panels.SetSession(_session, _masterSwitch.IsMaster); }));
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

    /// <summary>The user left the session on purpose: no outage to bridge, so held pointer
    /// history is dropped and panels return to their resting state.</summary>
    public void EndSession()
    {
        lock (_stateLock)
        {
            _degradedTimer?.Dispose();
            _degradedTimer = null;
            _hadPeer = false;
            _accumulate = false;
            _session = SessionState.None;
            _history.Clear();
            _panels.SetSession(_session, _masterSwitch.IsMaster);
        }
    }

    private void OnPeersChanged(bool hasPeers)
    {
        lock (_stateLock)
        {
            if (hasPeers)
            {
                _degradedTimer?.Dispose();
                _degradedTimer = null;
                var recovered = _session == SessionState.Degraded;
                _hadPeer = true;
                _session = SessionState.Live;
                _panels.SetSession(_session, _masterSwitch.IsMaster);
                // Re-send held history; the receiver drops what it already has by
                // (Session, Seq). After an outage everything held is sent - the slave was
                // locked for the whole gap, so ordered replay reconstructs sync exactly.
                // Otherwise only recent events go, to cover the reconnect race without
                // replaying stale input into a panel that moved on.
                ResendHistory(includeAll: recovered);
                _accumulate = false;
            }
            else if (_hadPeer && _session == SessionState.Live)
            {
                _session = SessionState.Degraded;
                _accumulate = true;
                _panels.SetSession(_session, _masterSwitch.IsMaster);
                _degradedTimer = Observable.Timer(DegradedTimeout).Subscribe(_ =>
                {
                    Log.Warning("[Pointer] Peer did not return within {Timeout}; session over, panels may be desynced", DegradedTimeout);
                    EndSession();
                });
            }
        }
    }

    private uint NextSeq() => (uint)Interlocked.Increment(ref _pointerSeq);

    private void SendPointer(PointerPress p) { Record(p.Key, p); _net.SendAll(p); }
    private void SendPointer(PointerDrag d) { Record(d.Key, d); _net.SendAll(d); }

    private void Record(string key, object packet)
    {
        lock (_stateLock)
        {
            if (!_history.TryGetValue(key, out var list)) _history[key] = list = [];
            list.Add((packet, DateTime.UtcNow));
            var cap = _accumulate ? DegradedHistoryPerKey : LiveHistoryPerKey;
            if (list.Count > cap) list.RemoveAt(0);
        }
    }

    private void ResendHistory(bool includeAll)
    {
        List<(object Packet, DateTime At)> entries;
        lock (_stateLock)
        {
            entries = _history.Values.SelectMany(l => l).OrderBy(e => e.At).ToList();
        }
        var cutoff = DateTime.UtcNow - LiveHistoryMaxAge;
        foreach (var (packet, at) in entries)
        {
            if (!includeAll && at < cutoff) continue;
            switch (packet)
            {
                case PointerPress p: _net.SendAll(p); break;
                case PointerDrag d: _net.SendAll(d); break;
            }
        }
        if (entries.Count > 0) Log.Debug("[Pointer] Re-sent {Count} held events after reconnect", entries.Count);
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
