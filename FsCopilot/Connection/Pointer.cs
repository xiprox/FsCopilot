namespace FsCopilot.Connection;

using Network;

/*
 * PointerEvent   Key       string  instrument key: identifier, or identifier|query
 *                Session   u64     random per app run of the sender
 *                Seq       u32     increases per sender run; presses and drags share it
 *                Flags     u8      reserved
 *                Kind      u8      0 press, 1 drag
 *                Button    u8
 *                HoldMs    u16     press: down to up; drag: 0
 *                GapMs     u16     idle time before this gesture, <= 1000
 *                DownX/Y   f32 x2  rect fractions; press: down point, drag: first path point
 *                UpX/Y     f32 x2  press: up point, drag: last path point
 *                Path      u16 count, then (u16 DtMs, f32 X, f32 Y) each; empty for a press
 *
 * Codecs.Schema hashes every registered type, so adding it breaks pairing with older
 * builds. Flags exists so later additions can be bits instead of fields.
 */

public enum PointerKind : byte
{
    Press = 0,
    Drag = 1
}

/// <summary>
/// A press or a drag. One type, not two, because each packet type is its own stream,
/// and two streams could deliver a press before the drag that preceded it.
/// Coordinates are fractions of the instrument's bounding rect and are not clamped.
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
