namespace FsCopilot.Network;

using LiteNetLib;

/// <summary>
/// Where each <see cref="Delivery"/> goes on the wire, for the direct and the relay transport
/// alike. A LiteNetLib channel number is an ordering domain: packets sent ReliableOrdered on one
/// channel wait for each other and never for anything on another, and a Sequenced channel keeps
/// one "newest so far" of its own. Unreliable is the exception - its one-byte header has no room
/// for a channel, so it is delivered as channel 0 whatever was asked - which is why the relay
/// cannot tell frames apart by channel and carries a frame type of its own instead.
/// </summary>
internal static class Transport
{
    /// <summary>The relay's own control traffic. The direct transport never uses it.</summary>
    public const byte ControlChannel = 0;
    public const byte SessionChannel = 1;
    public const byte PhysicsChannel = 2;
    public const byte BulkChannel = 3;
    public const byte ChannelsCount = 4;

    /// <summary>
    /// LiteNetLib 1.3.1 starts every link at <c>NetConstants.InitialMtu</c> = 1024 and only grows
    /// it by discovery, and an unreliable packet over the link's MTU is not fragmented but
    /// thrown away with an exception. A relayed packet crosses two links that discover
    /// separately, so the budget is the floor, never the discovered value.
    /// </summary>
    public const int MtuFloor = 1024;

    /// <summary>The floor less LiteNetLib's one-byte unreliable header.</summary>
    public const int MaxUnreliablePayload = MtuFloor - 1;

    public static (byte Channel, DeliveryMethod Method) Map(Delivery delivery) => delivery switch
    {
        Delivery.Bulk => (BulkChannel, DeliveryMethod.ReliableOrdered),
        Delivery.Sequenced => (PhysicsChannel, DeliveryMethod.Sequenced),
        Delivery.Unreliable => (ControlChannel, DeliveryMethod.Unreliable),   // not carried; see above
        _ => (SessionChannel, DeliveryMethod.ReliableOrdered)
    };
}
