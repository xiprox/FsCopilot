namespace FsCopilot.Simulation;

using System.IO;
using Audio;
using Network;

/// <summary>
/// Who hosts what. Traffic and ATC audio are shared independently, each by at most one peer,
/// and hosting has nothing to do with the physics master. A peer claims a feature with a
/// <see cref="ShareHost"/> broadcast; every peer keeps the set of claimants and the host is the
/// ordinal-smallest id, so the outcome is the same everywhere whatever order the packets
/// arrived in. A peer whose own claim is not the smallest stands down and says so through
/// <see cref="Lost"/>. Claims are withdrawn explicitly, or pruned when the peer leaves; our own
/// are re-broadcast to every peer that joins.
///
/// Also the one place the feature's packets are registered, after Coordinator's, so that every
/// class depending on this one sees a fixed packet table.
/// </summary>
public sealed class ShareSwitch : IDisposable
{
    public enum Feature : byte { Traffic = 0, Atc = 1 }

    private readonly INetwork _net;
    private readonly Lock _lock = new();
    private readonly SortedSet<string>[] _claims = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
    private readonly bool[] _want = new bool[2];
    private readonly BehaviorSubject<string?>[] _host = [new(null), new(null)];
    private readonly Subject<Feature> _lost = new();
    private readonly Subject<string> _peerJoined = new();
    private HashSet<string> _known = [];
    private readonly CompositeDisposable _d = new();

    public string SelfId { get; }

    /// <summary>The peer hosting the feature, or null. Distinct, off the network thread.</summary>
    public IObservable<string?> Host(Feature f) => _host[(int)f].DistinctUntilChanged().ObserveOn(TaskPoolScheduler.Default);
    public string? CurrentHost(Feature f) => _host[(int)f].Value;
    public bool IsHosting(Feature f) => CurrentHost(f) == SelfId;
    /// <summary>We claimed a feature and lost the tie-break; the toggle should revert.</summary>
    public IObservable<Feature> Lost => _lost.ObserveOn(TaskPoolScheduler.Default);
    /// <summary>A peer id seen for the first time; hosts re-send what a late joiner missed.</summary>
    public IObservable<string> PeerJoined => _peerJoined.ObserveOn(TaskPoolScheduler.Default);

    public ShareSwitch(string peerId, INetwork net)
    {
        SelfId = peerId;
        _net = net;

        net.RegisterPacket<ShareHost, ShareHost.Codec>();
        net.RegisterPacket<TrafficIdentity, TrafficIdentity.Codec>();
        net.RegisterPacket<TrafficStates, TrafficStates.Codec>();
        net.RegisterPacket<TrafficRemove, TrafficRemove.Codec>();
        net.RegisterPacket<AtcFrame, AtcFrame.Codec>();

        _d.Add(net.Stream<ShareHost>().Subscribe(Apply));
        _d.Add(net.Peers.Subscribe(OnPeers));
    }

    public void Dispose()
    {
        _d.Dispose();
        foreach (var h in _host) h.OnCompleted();
        _lost.OnCompleted();
        _peerJoined.OnCompleted();
    }

    /// <summary>
    /// Start or stop hosting a feature. Starting while another peer hosts it does nothing:
    /// there is no take-over, the toggle is disabled in that state and this is the backstop.
    /// </summary>
    public void Request(Feature f, bool on)
    {
        var i = (int)f;
        lock (_lock)
        {
            if (on)
            {
                var host = Min(f);
                if (host is not null && host != SelfId) return;
                _want[i] = true;
                _claims[i].Add(SelfId);
            }
            else
            {
                _want[i] = false;
                _claims[i].Remove(SelfId);
            }
            Recompute(f);
        }
        Send(new ShareHost(SelfId, f, on));
    }

    /// <summary>Withdraw every claim; for exit.</summary>
    public void StopAll()
    {
        foreach (var f in new[] { Feature.Traffic, Feature.Atc })
        {
            bool want;
            lock (_lock) want = _want[(int)f];
            if (want) Request(f, false);
        }
    }

    private void Apply(ShareHost p)
    {
        if (p.Peer == SelfId) return;
        lock (_lock)
        {
            var claims = _claims[(int)p.Feature];
            if (p.Hosting) claims.Add(p.Peer);
            else claims.Remove(p.Peer);
            Recompute(p.Feature);
        }
    }

    private void OnPeers(ICollection<Peer> peers)
    {
        var ids = peers.Select(p => p.PeerId).ToHashSet(StringComparer.Ordinal);
        List<string> joined;
        var reclaim = new List<ShareHost>();
        lock (_lock)
        {
            joined = ids.Where(id => !_known.Contains(id)).ToList();
            _known = ids;
            foreach (var f in new[] { Feature.Traffic, Feature.Atc })
            {
                var claims = _claims[(int)f];
                claims.RemoveWhere(c => c != SelfId && !ids.Contains(c));
                Recompute(f);
                if (joined.Count > 0 && _want[(int)f]) reclaim.Add(new ShareHost(SelfId, f, true));
            }
        }
        foreach (var claim in reclaim) Send(claim);
        foreach (var id in joined) _peerJoined.OnNext(id);
    }

    private string? Min(Feature f) => _claims[(int)f].Count > 0 ? _claims[(int)f].Min : null;

    /// <summary>Under the lock. Settles the host and stands down if our claim lost.</summary>
    private void Recompute(Feature f)
    {
        var i = (int)f;
        var host = Min(f);
        if (_want[i] && host != SelfId)
        {
            _want[i] = false;
            _claims[i].Remove(SelfId);
            Log.Information("[Share] {Peer} already shares {Feature}; standing down", host, f);
            Send(new ShareHost(SelfId, f, false));
            _lost.OnNext(f);
            host = Min(f);
        }
        if (_host[i].Value != host)
        {
            Log.Information("[Share] {Feature} host: {Host}", f, host ?? "none");
            _host[i].OnNext(host);
        }
    }

    private void Send(ShareHost p)
    {
        try { _net.SendAll(p); }
        catch (Exception e) { Log.Error(e, "[Share] Could not send {Feature} {Hosting}", p.Feature, p.Hosting); }
    }

    public record ShareHost(string Peer, Feature Feature, bool Hosting)
    {
        public class Codec : IPacketCodec<ShareHost>
        {
            public void Encode(ShareHost p, BinaryWriter bw)
            {
                bw.Write(p.Peer);
                bw.Write((byte)p.Feature);
                bw.Write(p.Hosting);
            }

            public ShareHost Decode(BinaryReader br) => new(br.ReadString(), (Feature)br.ReadByte(), br.ReadBoolean());
        }
    }
}
