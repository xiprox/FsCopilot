namespace FsCopilot.Network;

/// <summary>
/// How a packet travels. <see cref="Sequenced"/> is what <c>unreliable: true</c> has always
/// meant: no retransmit, and a packet that arrives after a newer one is dropped - per channel,
/// so every sequenced stream shares one ordering and a late packet of one drops on the arrival
/// of another. <see cref="Unreliable"/> delivers everything that arrives, in whatever order; a
/// stream that carries its own sequence numbers wants this.
/// </summary>
public enum Delivery
{
    Reliable,
    Sequenced,
    Unreliable
}
