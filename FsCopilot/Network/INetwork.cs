namespace FsCopilot.Network;

public interface INetwork
{
    IObservable<ICollection<Peer>> Peers { get; }

    /// <summary>True while a <see cref="Connect"/> is in flight. Set and cleared inside
    /// Connect itself, so a joiner is "connecting" from the moment Join is pressed until
    /// the attempt resolves, without the Coordinator having to see the view model.</summary>
    IObservable<bool> Connecting { get; }

    /// <summary>A peer that went on purpose - Leave, or the app quitting - rather than
    /// being lost. Carried in the disconnect itself (a payload on the direct-path
    /// disconnect, the PEER_LEFT / LEFT_ALL close code over the relay), not in a packet
    /// sent before it: the transport does not promise queued packets go out ahead of
    /// the disconnect. Emits the peer id, before the peer list reflects the departure.
    /// A timeout, a crash or a kill never produce it; those are outages.</summary>
    IObservable<string> PeerLeft { get; }
    
    Task<ConnectionResult> Connect(string target, CancellationToken ct);

    void Disconnect();

    void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull;

    void RegisterPacket<TPacket, TCodec>() 
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new();

    IObservable<TPacket> Stream<TPacket>();
}
