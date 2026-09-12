namespace FsCopilot.Network;

/// <summary>
/// One entry in the peer list. Connected is false while the transport is still
/// handshaking with this peer (a direct connection in LiteNetLib's Outgoing state):
/// the UI hides such peers, and the Coordinator reads them as "connecting" - a link
/// that may still fail must not count as a live session.
/// </summary>
public record struct Peer(
    string PeerId, 
    string Name, 
    int Ping,
    Peer.TransportKind Transport,
    bool Connected)
{
    public enum TransportKind { Direct, Relay }
}
