namespace FsCopilot.Network;

public interface INetwork
{
    IObservable<ICollection<Peer>> Peers { get; }

    /// <summary>True while a <see cref="Connect"/> is in flight. Set and cleared inside
    /// Connect itself, so a joiner is "connecting" from the moment Join is pressed until
    /// the attempt resolves, without the Coordinator having to see the view model.</summary>
    IObservable<bool> Connecting { get; }
    
    Task<ConnectionResult> Connect(string target, CancellationToken ct);

    void Disconnect();

    void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull;

    void RegisterPacket<TPacket, TCodec>() 
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new();

    IObservable<TPacket> Stream<TPacket>();
}
