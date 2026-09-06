namespace FsCopilot.Simulation;

using System.IO;
using Network;

public enum TrafficCategory : byte
{
    Airplane = 0,
    Helicopter = 1,
    GroundVehicle = 2,
    Other = 3
}

/// <summary>
/// Sent once per object when the host first sees it, and again to every peer that joins later.
/// <see cref="Index"/> is the host's compact id for the object; every state and the remove refer
/// to it. Reliable.
/// </summary>
public record TrafficIdentity(
    string Host,
    ushort Index,
    string Title,
    string Livery,
    string Tail,
    string Airline,
    string FlightNumber,
    string Model,
    TrafficCategory Category)
{
    public bool IsAircraft => Category is TrafficCategory.Airplane or TrafficCategory.Helicopter;

    public static TrafficCategory CategoryOf(string category) => category switch
    {
        "Airplane" => TrafficCategory.Airplane,
        "Helicopter" => TrafficCategory.Helicopter,
        "GroundVehicle" => TrafficCategory.GroundVehicle,
        _ => TrafficCategory.Other
    };

    public class Codec : IPacketCodec<TrafficIdentity>
    {
        public void Encode(TrafficIdentity p, BinaryWriter bw)
        {
            bw.Write(p.Host);
            bw.Write(p.Index);
            bw.Write(p.Title);
            bw.Write(p.Livery);
            bw.Write(p.Tail);
            bw.Write(p.Airline);
            bw.Write(p.FlightNumber);
            bw.Write(p.Model);
            bw.Write((byte)p.Category);
        }

        public TrafficIdentity Decode(BinaryReader br) => new(
            br.ReadString(), br.ReadUInt16(), br.ReadString(), br.ReadString(), br.ReadString(),
            br.ReadString(), br.ReadString(), br.ReadString(), (TrafficCategory)br.ReadByte());
    }
}

/// <summary>
/// One object's pose and animation state, in real units. <see cref="AgeMs"/> is how old the
/// sample already was when the host sent it: 0 for a fresh one, the quiet time for the sample
/// re-sent ahead of a change, so the receiver can place both on its own clock without ever
/// needing the host's.
/// </summary>
public readonly record struct TrafficState(
    ushort Index,
    ushort AgeMs,
    double Lat,
    double Lon,
    double Alt,
    double Hdg,
    double Pitch,
    double Bank,
    double Gs,
    double Vs,
    double VbX,
    double VbY,
    double VbZ,
    double RotX,
    double RotY,
    double RotZ,
    bool OnGround,
    byte GearPct,
    byte FlapsIndex,
    byte Lights,
    byte Engines)
{
    public const int Bytes = 43;

    public int EngineCount => Engines >> 4 & 7;
    public int EngineMask => Engines & 0xF;

    public static TrafficState From(in ObjectState s, ushort age) => new(
        0, age,
        s.Lat, s.Lon, s.Alt, s.HeadingTrue, s.Pitch, s.Bank, s.GroundSpeed, s.VerticalSpeed,
        s.VelBodyX, s.VelBodyY, s.VelBodyZ, s.RotX, s.RotY, s.RotZ,
        s.OnGround != 0,
        (byte)Math.Clamp(Math.Round(s.GearHandle * 100), 0, 100),
        (byte)Math.Clamp(s.FlapsIndex, 0, 255),
        (byte)s.LightMask,
        (byte)(Math.Clamp(s.NumEngines, 0, 7) << 4 | s.EngineMask));

    public DriveState ToDrive() => new()
    {
        Lat = Lat, Lon = Lon, Alt = Alt, Pitch = Pitch, Bank = Bank, HeadingTrue = Hdg,
        VelBodyX = VbX, VelBodyY = VbY, VelBodyZ = VbZ, RotX = RotX, RotY = RotY, RotZ = RotZ
    };

    // Quantisation: position to 1e-7 deg (~1 cm), angles to 0.01 deg, speeds to 0.1, rates to
    // 0.001 rad/s - all an order below the host's change thresholds, so the wire never
    // reintroduces motion the gate suppressed.
    internal static void Write(BinaryWriter bw, in TrafficState s)
    {
        bw.Write(s.Index);
        bw.Write(s.AgeMs);
        bw.Write((int)Math.Round(s.Lat * 1e7));
        bw.Write((int)Math.Round(s.Lon * 1e7));
        bw.Write((float)s.Alt);
        bw.Write((ushort)(Math.Round(((s.Hdg % 360 + 360) % 360) * 100) % 36000));
        bw.Write(I16(s.Pitch * 100));
        bw.Write(I16(s.Bank * 100));
        bw.Write(I16(s.Gs * 10));
        bw.Write(I16(s.Vs));
        bw.Write(I16(s.VbX * 10));
        bw.Write(I16(s.VbY * 10));
        bw.Write(I16(s.VbZ * 10));
        bw.Write(I16(s.RotX * 1000));
        bw.Write(I16(s.RotY * 1000));
        bw.Write(I16(s.RotZ * 1000));
        bw.Write((byte)(s.OnGround ? 1 : 0));
        bw.Write(s.GearPct);
        bw.Write(s.FlapsIndex);
        bw.Write(s.Lights);
        bw.Write(s.Engines);
    }

    internal static TrafficState Read(BinaryReader br) => new(
        br.ReadUInt16(),
        br.ReadUInt16(),
        br.ReadInt32() / 1e7,
        br.ReadInt32() / 1e7,
        br.ReadSingle(),
        br.ReadUInt16() / 100.0,
        br.ReadInt16() / 100.0,
        br.ReadInt16() / 100.0,
        br.ReadInt16() / 10.0,
        br.ReadInt16(),
        br.ReadInt16() / 10.0,
        br.ReadInt16() / 10.0,
        br.ReadInt16() / 10.0,
        br.ReadInt16() / 1000.0,
        br.ReadInt16() / 1000.0,
        br.ReadInt16() / 1000.0,
        br.ReadByte() != 0,
        br.ReadByte(),
        br.ReadByte(),
        br.ReadByte(),
        br.ReadByte());

    private static short I16(double v) => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
}

/// <summary>
/// A batch of states from one host. Unreliable, so it must stay under the MTU: at most
/// <see cref="MaxPerPacket"/> states, which with the header is ~450 bytes against LiteNetLib's
/// initial 508. <see cref="Seq"/> lets the receiver count gaps. <see cref="HostMs"/> is the
/// host's clock at send time: the receiver estimates its offset from the least-delayed
/// packets, so sample times are exact and network jitter only decides which packets are late.
/// </summary>
public record TrafficStates(string Host, uint Seq, uint HostMs, TrafficState[] States)
{
    public const int MaxPerPacket = 10;

    public class Codec : IPacketCodec<TrafficStates>
    {
        public void Encode(TrafficStates p, BinaryWriter bw)
        {
            if (p.States.Length > MaxPerPacket) throw new InvalidDataException($"{p.States.Length} states in one packet");
            bw.Write(p.Host);
            bw.Write(p.Seq);
            bw.Write(p.HostMs);
            bw.Write((byte)p.States.Length);
            foreach (ref readonly var s in p.States.AsSpan()) TrafficState.Write(bw, in s);
        }

        public TrafficStates Decode(BinaryReader br)
        {
            var host = br.ReadString();
            var seq = br.ReadUInt32();
            var hostMs = br.ReadUInt32();
            var count = br.ReadByte();
            if (count > MaxPerPacket) throw new InvalidDataException($"{count} states in one packet");
            var states = new TrafficState[count];
            for (var i = 0; i < count; i++) states[i] = TrafficState.Read(br);
            return new(host, seq, hostMs, states);
        }
    }
}

/// <summary>The object left the host's sim. Reliable.</summary>
public record TrafficRemove(string Host, ushort Index)
{
    public class Codec : IPacketCodec<TrafficRemove>
    {
        public void Encode(TrafficRemove p, BinaryWriter bw)
        {
            bw.Write(p.Host);
            bw.Write(p.Index);
        }

        public TrafficRemove Decode(BinaryReader br) => new(br.ReadString(), br.ReadUInt16());
    }
}
