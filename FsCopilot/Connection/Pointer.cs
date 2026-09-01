namespace FsCopilot.Connection;

using Network;

/// <summary>
/// One press (or press-and-hold) on a pointer-synced panel. Coordinates are fractions of
/// the instrument element's bounding rect on the capturing machine - the receiver resolves
/// them against its own rect, so neither side needs to know the other's resolution. They
/// can legitimately fall slightly outside [0,1] and are never clamped.
/// Session identifies one app run (random); Seq increases monotonically within it - together
/// they let the receiver drop duplicates when history is re-sent after a reconnect, and
/// notice gaps. Flags is reserved: adding a field later changes the codec schema and breaks
/// compatibility with every older build, flag bits do not.
/// </summary>
public record PointerPress(string Key, ulong Session, uint Seq, byte Flags, byte Button,
    ushort HoldMs, float DownX, float DownY, float UpX, float UpY)
{
    public class Codec : IPacketCodec<PointerPress>
    {
        public void Encode(PointerPress packet, BinaryWriter bw)
        {
            bw.Write(packet.Key);
            bw.Write(packet.Session);
            bw.Write(packet.Seq);
            bw.Write(packet.Flags);
            bw.Write(packet.Button);
            bw.Write(packet.HoldMs);
            bw.Write(packet.DownX);
            bw.Write(packet.DownY);
            bw.Write(packet.UpX);
            bw.Write(packet.UpY);
        }

        public PointerPress Decode(BinaryReader br) => new(
            br.ReadString(), br.ReadUInt64(), br.ReadUInt32(), br.ReadByte(), br.ReadByte(),
            br.ReadUInt16(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
    }
}

/// <summary>
/// One complete drag gesture, sent at mouse-up with its full sampled path. Each point's
/// DtMs is the delta to the previous point (the capture side samples at ~30 Hz and clamps
/// replay steps to 250 ms, so ushort never saturates in practice). Capture bounds the path
/// at 240 points; decode rejects anything past 1024 as malformed rather than allocating.
/// </summary>
public record PointerDrag(string Key, ulong Session, uint Seq, byte Flags, byte Button,
    PointerDrag.Point[] Path)
{
    public readonly record struct Point(ushort DtMs, float X, float Y);

    public class Codec : IPacketCodec<PointerDrag>
    {
        private const int MaxPoints = 1024;

        public void Encode(PointerDrag packet, BinaryWriter bw)
        {
            bw.Write(packet.Key);
            bw.Write(packet.Session);
            bw.Write(packet.Seq);
            bw.Write(packet.Flags);
            bw.Write(packet.Button);
            var count = Math.Min(packet.Path.Length, MaxPoints);
            bw.Write((ushort)count);
            for (var i = 0; i < count; i++)
            {
                bw.Write(packet.Path[i].DtMs);
                bw.Write(packet.Path[i].X);
                bw.Write(packet.Path[i].Y);
            }
        }

        public PointerDrag Decode(BinaryReader br)
        {
            var key = br.ReadString();
            var session = br.ReadUInt64();
            var seq = br.ReadUInt32();
            var flags = br.ReadByte();
            var button = br.ReadByte();
            var count = br.ReadUInt16();
            if (count > MaxPoints) throw new InvalidDataException($"Drag path of {count} points rejected");
            var path = new Point[count];
            for (var i = 0; i < count; i++)
                path[i] = new Point(br.ReadUInt16(), br.ReadSingle(), br.ReadSingle());
            return new PointerDrag(key, session, seq, flags, button, path);
        }
    }
}
