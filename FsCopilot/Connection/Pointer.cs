namespace FsCopilot.Connection;

using Network;

public enum PointerKind : byte
{
    Press = 0,
    Drag = 1
}

/// <summary>
/// One pointer gesture on a pointer-synced panel: a press (or press-and-hold), or a drag
/// with its full sampled path, sent at mouse-up. One type for both, not two: the two
/// kinds share one sequence space, and a type is a stream - two types would ride two
/// observables with independent scheduling hops, so a drag could be processed after the
/// press that followed it and be dropped as a duplicate. One type is one ordered stream
/// from the wire to the panel socket.
/// Coordinates are fractions of the instrument element's bounding rect on the capturing
/// machine - the receiver resolves them against its own rect, so neither side needs to
/// know the other's resolution. They can legitimately fall slightly outside [0,1] and
/// are never clamped. For a drag, Down is the first path point and Up the last; each
/// path point's DtMs is the delta to the previous point (the capture side samples at
/// ~30 Hz and clamps replay steps to 250 ms, so ushort never saturates in practice).
/// Capture bounds the path at 240 points; decode rejects anything past 1024 as
/// malformed rather than allocating. A press has an empty path.
/// Session identifies one app run (random); Seq increases monotonically within it -
/// together they let the receiver drop duplicates when history is re-sent after a
/// reconnect, and notice gaps. Flags is reserved: adding a field later changes the
/// codec schema and breaks compatibility with every older build, flag bits do not.
/// </summary>
public record PointerEvent(string Key, ulong Session, uint Seq, byte Flags, PointerKind Kind, byte Button,
    ushort HoldMs, float DownX, float DownY, float UpX, float UpY, PointerEvent.Point[] Path)
{
    public readonly record struct Point(ushort DtMs, float X, float Y);

    public static readonly Point[] NoPath = [];

    public class Codec : IPacketCodec<PointerEvent>
    {
        private const int MaxPoints = 1024;

        public void Encode(PointerEvent packet, BinaryWriter bw)
        {
            bw.Write(packet.Key);
            bw.Write(packet.Session);
            bw.Write(packet.Seq);
            bw.Write(packet.Flags);
            bw.Write((byte)packet.Kind);
            bw.Write(packet.Button);
            bw.Write(packet.HoldMs);
            bw.Write(packet.DownX);
            bw.Write(packet.DownY);
            bw.Write(packet.UpX);
            bw.Write(packet.UpY);
            var count = Math.Min(packet.Path.Length, MaxPoints);
            bw.Write((ushort)count);
            for (var i = 0; i < count; i++)
            {
                bw.Write(packet.Path[i].DtMs);
                bw.Write(packet.Path[i].X);
                bw.Write(packet.Path[i].Y);
            }
        }

        public PointerEvent Decode(BinaryReader br)
        {
            var key = br.ReadString();
            var session = br.ReadUInt64();
            var seq = br.ReadUInt32();
            var flags = br.ReadByte();
            var kind = br.ReadByte();
            if (kind > (byte)PointerKind.Drag) throw new InvalidDataException($"Pointer kind {kind} rejected");
            var button = br.ReadByte();
            var hold = br.ReadUInt16();
            var downX = br.ReadSingle();
            var downY = br.ReadSingle();
            var upX = br.ReadSingle();
            var upY = br.ReadSingle();
            var count = br.ReadUInt16();
            if (count > MaxPoints) throw new InvalidDataException($"Drag path of {count} points rejected");
            var path = count == 0 ? NoPath : new Point[count];
            for (var i = 0; i < count; i++)
                path[i] = new Point(br.ReadUInt16(), br.ReadSingle(), br.ReadSingle());
            return new PointerEvent(key, session, seq, flags, (PointerKind)kind, button, hold,
                downX, downY, upX, upY, path);
        }
    }
}

/// <summary>
/// The receiver's acknowledgement: "I have your session up to Seq". Sent every few
/// seconds, and only when the receiver's high-water mark for that session has moved.
/// The sender keeps everything after the last ack - that is its whole history, no live
/// ring and no age window - and resends it when the link recovers; the receiver's
/// (Session, Seq) dedupe stays as the second line for the kept-running case. From is
/// the acker's own session id: both ids are per app run, so a restarted peer is a new
/// acker in every respect, and the sender resends from the lowest ack it holds, which
/// is what catches that peer up.
/// </summary>
public record PointerAck(ulong Session, uint Seq, ulong From)
{
    public class Codec : IPacketCodec<PointerAck>
    {
        public void Encode(PointerAck packet, BinaryWriter bw)
        {
            bw.Write(packet.Session);
            bw.Write(packet.Seq);
            bw.Write(packet.From);
        }

        public PointerAck Decode(BinaryReader br) => new(br.ReadUInt64(), br.ReadUInt32(), br.ReadUInt64());
    }
}
