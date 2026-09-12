namespace FsCopilot.Simulation;

using System.Security.Cryptography;
using Connection;
using Network;

public class Coordinator : IDisposable
{
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
            .Subscribe(e => _net.SendAll(e with { Session = _sessionId, Seq = NextSeq() })));
        _d.Add(_net.Stream<PointerEvent>()
            .Where(e => _pointer.Keys.Contains(e.Key))
            .Subscribe(panels.Send));
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

    private uint NextSeq() => (uint)Interlocked.Increment(ref _pointerSeq);

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
