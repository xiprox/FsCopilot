namespace FsCopilot.Audio;

using System.IO;
using Network;

/// <summary>
/// One 20 ms Opus frame of ATC audio from the hosting peer. Unreliable; <see cref="Seq"/> is
/// what the receiver's jitter buffer orders by and conceals gaps in.
/// </summary>
public record AtcFrame(string Host, uint Seq, byte[] Opus)
{
    // Opus allows 1275 bytes per frame, but this is an unreliable packet and LiteNetLib does not
    // fragment those: over the MTU it throws instead of sending. The wire header is 16 bytes (id,
    // an 8-character peer id, Seq, Len), so this keeps the packet inside the same 480-byte budget
    // the state batches use against LiteNetLib's 508-byte initial MTU. At 24 kbps VBR a 20 ms
    // frame is around 60 bytes, so the ceiling is headroom, never a constraint on quality.
    public const int MaxOpusBytes = 460;

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
