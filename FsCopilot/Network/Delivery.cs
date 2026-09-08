namespace FsCopilot.Network;

/// <summary>
/// How a packet travels. Each value is its own ordering domain - <see cref="Transport"/> says
/// which LiteNetLib channel and method it becomes - and the rule for sharing a domain is that two
/// streams may share one only if neither is hurt by waiting behind the other. A packet picks a
/// <c>Delivery</c> for the ordering it needs, never a channel for the feature it belongs to.
/// </summary>
public enum Delivery
{
    /// <summary>
    /// Must arrive, in order, and promptly: the session's own traffic - cockpit updates, the
    /// master handover, peer lists. Nothing bursty may share its queue.
    /// </summary>
    Reliable,

    /// <summary>
    /// Must arrive, in order, but comes in bursts - a hundred traffic identities when a peer
    /// joins - and would hold every <see cref="Reliable"/> packet behind it if they shared a
    /// queue: ReliableOrdered is head-of-line blocking by definition.
    /// </summary>
    Bulk,

    /// <summary>
    /// What <c>unreliable: true</c> has always meant: no retransmit, and a packet that arrives
    /// after a newer one is dropped. Right only when every packet supersedes the last, as the
    /// user aircraft's physics does; wrong for a stream whose packets each carry different
    /// objects.
    /// </summary>
    Sequenced,

    /// <summary>
    /// Everything that arrives, in whatever order. A stream that carries its own sequence numbers
    /// and decides for itself what is stale wants this - traffic states, audio frames.
    /// </summary>
    Unreliable
}
