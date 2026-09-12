namespace FsCopilot.Connection;

using Network;

/*
 * The pointer-sync wire, complete, in one place.
 *
 *   PointerEvent   Key       string  full panel key: identifier, or identifier|query
 *                  Session   u64     random per app run of the sender
 *                  Seq       u32     monotonic per sender run; one space for presses and drags
 *                  Flags     u8      reserved - flag bits do not change the schema, fields do
 *                  Kind      u8      0 press, 1 drag
 *                  Button    u8
 *                  HoldMs    u16     press: down to up; drag: 0
 *                  GapMs     u16     idle time before this gesture on the capturing side, <= 1000
 *                  DownX/Y   f32 x2  rect fractions; press: the down point, drag: the first path point
 *                  UpX/Y     f32 x2  press: the up point, drag: the last path point
 *                  Path      u16 count, then (u16 DtMs, f32 X, f32 Y) each; empty for a press
 *   PointerAck     Session   u64     the sender session being acknowledged
 *                  Seq       u32     "I have your session up to here"
 *                  From      u64     the acker's own session, so a restarted peer is a new acker
 *
 * Both are registered after Surfaces in the Coordinator. Not a packet, but part of the
 * same change: the direct-path disconnect carries a "left" payload (P2PNetwork) and the
 * relay's LinkClosed code is read, so a peer that left is told from one that was lost.
 *
 * Codecs.Schema hashes every registered type, so this build refuses every older one
 * ("Both sides must use the same FS Copilot version"). Intended and unavoidable; it is
 * why Session, Seq and Flags are on the wire from day one. The relay does not care -
 * it checks that the two peers match each other, not that they match the relay.
 */

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
/// GapMs is the idle time before this gesture on the capturing side - since the previous
/// gesture's end, capped at 1000 - and the receiver's replay queue preserves it: a panel
/// that loads something after a click needs the time the pilot gave it, and the pilot's
/// pacing is the only honest source of that number. A resend burst arrives all at once,
/// so arrival time tells the panel nothing.
/// Session identifies one app run (random); Seq increases monotonically within it -
/// together they let the receiver drop duplicates when history is re-sent after a
/// reconnect, and notice gaps. Flags is reserved: adding a field later changes the
/// codec schema and breaks compatibility with every older build, flag bits do not.
/// </summary>
public record PointerEvent(string Key, ulong Session, uint Seq, byte Flags, PointerKind Kind, byte Button,
    ushort HoldMs, ushort GapMs, float DownX, float DownY, float UpX, float UpY, PointerEvent.Point[] Path)
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
            bw.Write(packet.GapMs);
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
            var gap = br.ReadUInt16();
            var downX = br.ReadSingle();
            var downY = br.ReadSingle();
            var upX = br.ReadSingle();
            var upY = br.ReadSingle();
            var count = br.ReadUInt16();
            if (count > MaxPoints) throw new InvalidDataException($"Drag path of {count} points rejected");
            var path = count == 0 ? NoPath : new Point[count];
            for (var i = 0; i < count; i++)
                path[i] = new Point(br.ReadUInt16(), br.ReadSingle(), br.ReadSingle());
            return new PointerEvent(key, session, seq, flags, (PointerKind)kind, button, hold, gap,
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
