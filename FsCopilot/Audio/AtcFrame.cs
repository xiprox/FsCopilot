namespace FsCopilot.Audio;

using System.IO;
using Network;

/// <summary>
/// One 20 ms Opus frame of ATC audio from the hosting peer. Unreliable; <see cref="Seq"/> is
/// what the receiver's jitter buffer orders by and conceals gaps in.
/// </summary>
public record AtcFrame(string Host, uint Seq, byte[] Opus)
{
    public const int MaxOpusBytes = 1275;   // the codec's own maximum for one frame

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
