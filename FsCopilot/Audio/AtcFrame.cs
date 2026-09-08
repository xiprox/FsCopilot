namespace FsCopilot.Audio;

using System.IO;
using Network;

/// <summary>
/// One 20 ms Opus frame of ATC audio from the hosting peer. Unreliable; <see cref="Seq"/> is
/// what the receiver's jitter buffer orders by and conceals gaps in.
/// </summary>
public record AtcFrame(string Host, uint Seq, byte[] Opus)
{
    // Packet id, the 8-character peer id with its length byte, Seq, Len.
    private const int HeaderBytes = 1 + 9 + 4 + 2;

    // Opus allows 1275 bytes per frame, but this is an unreliable packet and LiteNetLib does not
    // fragment those: over the MTU it throws instead of sending, so the frame is budgeted against
    // the same floor as the state batches. At 24 kbps VBR a 20 ms frame is around 60 bytes, so
    // the ceiling is headroom, never a constraint on quality.
    public const int MaxOpusBytes = Transport.MaxUnreliablePayload - HeaderBytes;

    public class Codec : IPacketCodec<AtcFrame>
    {
        public void Encode(AtcFrame p, BinaryWriter bw)
        {
            if (p.Opus.Length > MaxOpusBytes) throw new InvalidDataException($"{p.Opus.Length} byte Opus frame");
            bw.Write(p.Host);
            bw.Write(p.Seq);
            bw.Write((ushort)p.Opus.Length);
            bw.Write(p.Opus);
        }

        public AtcFrame Decode(BinaryReader br)
        {
            var host = br.ReadString();
            var seq = br.ReadUInt32();
            var len = br.ReadUInt16();
            if (len > MaxOpusBytes) throw new InvalidDataException($"{len} byte Opus frame");
            return new(host, seq, br.ReadBytes(len));
        }
    }
}
