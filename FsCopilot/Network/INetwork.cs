namespace FsCopilot.Network;

public interface INetwork
{
    IObservable<ICollection<Peer>> Peers { get; }

    IObservable<bool> Connecting { get; }

    IObservable<string> PeerLeft { get; }
    
    Task<ConnectionResult> Connect(string target, CancellationToken ct);

    void Disconnect();

    /// <summary>
    /// Waits, up to <paramref name="grace"/>, for a departure queued by <see cref="Disconnect"/>
    /// to actually leave the socket.
    /// </summary>
    void DrainDisconnect(TimeSpan grace);

    void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull;

    void RegisterPacket<TPacket, TCodec>() 
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new();

    IObservable<TPacket> Stream<TPacket>();
}
