namespace P2PDiscovery;

using System.Buffers;
using LiteNetLib;
using LiteNetLib.Utils;

public sealed class Relay : BackgroundService
{
    private const int Port = 3600;
    private const byte ControlChannel = 0;
    // Must match Transport.ChannelsCount in the client: LiteNetLib drops a packet whose channel
    // number is at or past the count, and the relay forwards channel numbers verbatim.
    private const byte ChannelsCount = 4;
    private const int TickMs = 10;

    /// <summary>
    /// The newest relay protocol. A client says which it speaks with <c>v=</c> in its connect
    /// token; none means 1. Version 1 tells control from data by channel number, which fails for
    /// Unreliable packets: LiteNetLib carries no channel on those, so they arrive as channel 0
    /// and were read as control. Version 2 starts every frame with a <see cref="FrameType"/>
    /// byte and leaves channel numbers to LiteNetLib, where they are ordering domains. A newer
    /// version than this is refused at connect; the two known ones are served side by side, and
    /// never linked to each other.
    /// </summary>
    private const int ProtocolVersion = 2;

    private readonly ILogger<Relay> _logger;
    private readonly ServerStats _stats;

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;

    // Mutated ONLY on PollEvents loop thread
    private readonly Dictionary<NetPeer, PeerState> _byPeer = new();
    private readonly Dictionary<string, PeerState> _byId = new(StringComparer.Ordinal);

    public Relay(ILogger<Relay> logger, ServerStats stats)
    {
        _logger = logger;
        _stats = stats;

        _net = new(_listener)
        {
            IPv6Enabled = true,
            DisconnectTimeout = 15_000,
            NatPunchEnabled = false,
            UnconnectedMessagesEnabled = false,
            ChannelsCount = ChannelsCount
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_net.Start(Port))
            throw new InvalidOperationException($"Failed to start the server on UDP port '{Port}'.");
        _logger.LogInformation("Server started on UDP port: {Port}", Port);
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping server...");
        _net.Stop();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _net.PollEvents();
                await Task.Delay(TickMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex) { _logger.LogError(ex, "An error occurred during server event processing"); }
        }

        _logger.LogInformation("Server stopped.");
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        var token = request.Data.GetString();

        // Token handshake: v=...;pid=...;schema=...
        if (!TryParseToken(token, out var peerId, out var schemaId, out var version))
        {
            _logger.LogError("Invalid connection request: {Request}", token);
            request.Reject(NetDataWriter.FromString("PROTOCOL_ERROR"));
            return;
        }

        if (version > ProtocolVersion)
        {
            _logger.LogWarning("REJECT pid={PeerId} v={Version}: newer than this relay", peerId, version);
            request.Reject(NetDataWriter.FromString("VERSION_UNSUPPORTED"));
            return;
        }

        if (_byId.TryGetValue(peerId, out var existing) &&
            existing.NetPeer.ConnectionState == ConnectionState.Connected)
        {
            request.Reject(NetDataWriter.FromString("PEER_ID_TAKEN"));
            return;
        }

        var peer = request.Accept();
        if (peer == null) return;

        var ps = new PeerState(peerId, schemaId, version, peer);
        _byPeer[peer] = ps;
        _byId[peerId] = ps;

        _logger.LogInformation("CONNECT pid={PeerId} v={Version} schema={Schema} ep={Ep}", peerId, version, schemaId, peer.Address);
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_byPeer.TryGetValue(peer, out var ps))
            return;

        _byPeer.Remove(peer);

        if (_byId.TryGetValue(ps.PeerId, out var cur) && ReferenceEquals(cur.NetPeer, peer))
            _byId.Remove(ps.PeerId);

        // Remove links and notify other side
        foreach (var other in ps.Sessions)
        {
            if (!_byPeer.TryGetValue(other, out var otherState))
                continue;

            otherState.Sessions.Remove(peer);

            if (other.ConnectionState == ConnectionState.Connected)
                SendLinkClosed(otherState, ps.PeerId, "PEER_DISCONNECTED", $"Peer '{ps.PeerId}' disconnected");
        }

        ps.Sessions.Clear();

        _logger.LogInformation("DISCONNECT pid={PeerId} reason={Reason}", ps.PeerId, info.Reason);
        UpdateRelayLinksStats();
    }

    // ---------------------------------
    // Receive
    // ---------------------------------

    private void OnNetworkReceive(NetPeer from, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            if (!_byPeer.TryGetValue(from, out var self))
                return;

            if (self.Version < 2)
            {
                // v1: the channel number is the frame type.
                if (channel == ControlChannel) HandleControl(from, self, reader);
                else ForwardToLinkedPeers(from, self, reader, channel, method);
                return;
            }

            if (reader.AvailableBytes < 1) return;
            var frame = (FrameType)reader.PeekByte();
            switch (frame)
            {
                case FrameType.Control:
                    // Control is reliable by construction; anything else claiming to be it is not ours.
                    if (method is DeliveryMethod.Unreliable or DeliveryMethod.Sequenced) return;
                    reader.GetByte();
                    HandleControl(from, self, reader);
                    return;

                case FrameType.Data:
                    // Forwarded whole, frame type included: the receiver strips it.
                    ForwardToLinkedPeers(from, self, reader, channel, method);
                    return;

                default:
                    _logger.LogDebug("FRAME ? pid={PeerId} type={Type}", self.PeerId, (byte)frame);
                    return;
            }
        }
        finally
        {
            reader.Recycle();
        }
    }

    // ---------------------------------
    // Control channel (0)
    // ---------------------------------

    private void HandleControl(NetPeer from, PeerState self, NetPacketReader reader)
    {
        if (reader.AvailableBytes < 1)
            return;

        var type = (ControlType)reader.GetByte();

        switch (type)
        {
            case ControlType.ConnectIntent:
                HandleConnectIntent(from, self, reader);
                break;

            case ControlType.DisconnectIntent:
                HandleDisconnectIntent(from, self, reader);
                break;

            default:
                // Never answered. A reply per received packet turns a confused client into an
                // amplifier - and this is exactly where a v1 client's Unreliable data used to land.
                _logger.LogDebug("CONTROL ? pid={PeerId} type={Type}", self.PeerId, (byte)type);
                break;
        }
    }

    private void HandleConnectIntent(NetPeer from, PeerState self, NetPacketReader reader)
    {
        var targetId = reader.GetString();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            _logger.LogError("Invalid target peer {Peer}", targetId);
            SendError(self, targetId ?? "Unknown", "PROTOCOL_ERROR", "Invalid ConnectIntent payload");
            return;
        }

        if (!_byId.TryGetValue(targetId, out var target) ||
            target.NetPeer.ConnectionState != ConnectionState.Connected)
        {
            SendError(self, targetId, "TARGET_NOT_FOUND", $"Target '{targetId}' not connected");
            return;
        }

        if (!string.Equals(self.SchemaId, target.SchemaId, StringComparison.Ordinal))
        {
            SendError(self, targetId, "SCHEMA_MISMATCH", $"Schema mismatch: self={self.SchemaId}, target={target.SchemaId}");
            return;
        }

        // Frames are forwarded as they arrive, so both ends of a link must frame them the same
        // way. In practice the schema check already refuses this pair - a v1 client is an
        // upstream build with another packet table - and this is the reason on record.
        if (self.Version != target.Version)
        {
            SendError(self, targetId, "VERSION_MISMATCH", $"Protocol mismatch: self=v{self.Version}, target=v{target.Version}");
            return;
        }

        if (self.Sessions.Contains(target.NetPeer) && target.Sessions.Contains(from)) return;

        // Create symmetric link
        self.Sessions.Add(target.NetPeer);
        target.Sessions.Add(from);

        SendLinkReady(self, targetId);
        SendLinkReady(target, self.PeerId);

        _logger.LogInformation("LINK UP {A} <-> {B}", self.PeerId, target.PeerId);

        UpdateRelayLinksStats();
    }

    private void HandleDisconnectIntent(NetPeer from, PeerState self, NetPacketReader reader)
    {
        // Snapshot to avoid modifying while iterating
        var others = self.Sessions.ToArray();

        foreach (var otherPeer in others)
        {
            // Remove reverse link if possible
            if (_byPeer.TryGetValue(otherPeer, out var otherState))
            {
                otherState.Sessions.Remove(from);

                if (otherPeer.ConnectionState == ConnectionState.Connected)
                {
                    SendLinkClosed(
                        otherState,
                        self.PeerId,
                        code: "PEER_LEFT",
                        message: $"Peer '{self.PeerId}' left all relay links");
                }
            }
        }

        self.Sessions.Clear();

        // Optional: ack to self (useful for UI state)
        SendLinkClosed(
            self,
            otherPeerId: "*",
            code: "LEFT_ALL",
            message: "Left all relay links");

        UpdateRelayLinksStats();
    }

    /// <summary>A control frame for that peer: framed for v2, bare for v1.</summary>
    private static NetDataWriter Control(PeerState to, ControlType type)
    {
        var w = new NetDataWriter();
        if (to.Version >= 2) w.Put((byte)FrameType.Control);
        w.Put((byte)type);
        return w;
    }

    private static void SendLinkReady(PeerState to, string otherPeerId)
    {
        var w = Control(to, ControlType.LinkReady);
        w.Put(otherPeerId);
        to.NetPeer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
    }

    private static void SendLinkClosed(PeerState to, string otherPeerId, string code, string message)
    {
        var w = Control(to, ControlType.LinkClosed);
        w.Put(otherPeerId);
        w.Put(code);
        w.Put(message);
        to.NetPeer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
    }

    private static void SendError(PeerState to, string target, string code, string message)
    {
        var w = Control(to, ControlType.Error);
        w.Put(target);
        w.Put(code);
        w.Put(message);
        to.NetPeer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
    }

    // ---------------------------------
    // Data forwarding: broadcast to linked peers
    // ---------------------------------

    private void ForwardToLinkedPeers(NetPeer from, PeerState self, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var len = reader.AvailableBytes;
        if (len <= 0) return;

        var buf = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            reader.GetBytes(buf, len);

            var sessions = self.Sessions.ToArray();
            foreach (var peer in sessions)
            {
                if (Equals(peer, from)) continue;
                if (peer.ConnectionState != ConnectionState.Connected) continue;

                // Preserve delivery semantics (Unreliable stays Unreliable, ReliableOrdered stays ReliableOrdered)
                try { peer.Send(buf, 0, len, channel, method); }
                catch (Exception e)
                {
                    // One link's trouble - an unreliable packet over its MTU, a socket gone - must
                    // not abort the fan-out, nor the rest of this poll's events for every session.
                    _logger.LogDebug(e, "FORWARD {From} -> {To} failed", self.PeerId, _byPeer.TryGetValue(peer, out var to) ? to.PeerId : "?");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ---------------------------------
    // Token parsing
    // v=1;pid=...;schema=...
    // ---------------------------------

    private static bool TryParseToken(string token, out string peerId, out string schemaId, out int version)
    {
        peerId = schemaId = string.Empty;
        version = 1;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) continue;

            var key = part.Substring(0, eq).Trim();
            var val = part.Substring(eq + 1).Trim();

            switch (key)
            {
                case "v": if (int.TryParse(val, out var v)) version = v; break;
                case "pid": peerId = val; break;
                case "schema": schemaId = val; break;
            }
        }

        if (string.IsNullOrWhiteSpace(peerId)) return false;

        return true;
    }

    private void UpdateRelayLinksStats()
    {
        // Each link is stored in both peers' Sessions collections
        var links = _byPeer.Values.Sum(x => x.Sessions.Count) / 2;
        _stats.SetRelayLinks(links);
    }

    // ---------------------------------
    // Types
    // ---------------------------------

    /// <summary>The first byte of every v2 frame, in either direction.</summary>
    private enum FrameType : byte
    {
        Control = 0,
        Data = 1
    }

    private enum ControlType : byte
    {
        // client -> server
        ConnectIntent = 1,
        DisconnectIntent = 2,

        // server -> client
        LinkReady = 10,
        LinkClosed = 11,

        Error = 255
    }

    private sealed class PeerState
    {
        public PeerState(string peerId, string schemaId, int version, NetPeer netPeer)
        {
            PeerId = peerId;
            SchemaId = schemaId;
            Version = version;
            NetPeer = netPeer;
        }

        public string PeerId { get; }
        public string SchemaId { get; }
        /// <summary>The relay protocol this peer speaks; see <see cref="ProtocolVersion"/>.</summary>
        public int Version { get; }
        public NetPeer NetPeer { get; }

        // "Sessions" == currently linked peers (relay links)
        public HashSet<NetPeer> Sessions { get; } = new();
    }
}
