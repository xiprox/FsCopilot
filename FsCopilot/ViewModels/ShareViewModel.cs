namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Audio;
using Connection;
using Network;
using ReactiveUI;
using Simulation;

/// <summary>
/// The "ATC &amp; Traffic" card. Each feature is one row: a toggle when this peer can host it,
/// or who is sharing it when someone else does. Sharing needs a session - the controls are
/// disabled until a peer is connected, whoever shares first hosts, and hosting ends with the
/// session - so nothing is armed in advance and nothing about the toggles is persisted. Under
/// the ATC toggle, which app is captured; while receiving ATC, a volume slider.
/// </summary>
public sealed class ShareViewModel : ReactiveObject, IDisposable
{
    private static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(5);

    private readonly CompositeDisposable _d = new();
    private readonly ShareSwitch _share;
    private readonly AtcHost _atcHost;
    private readonly AtcReceiver _atcReceiver;
    private readonly Settings _settings;
    private readonly Dictionary<string, string> _peerNames = new();

    private bool _trafficOn, _atcOn, _sessionActive, _simConnected, _foreignTraffic;
    private string? _trafficHost, _atcHostPeer, _notice;
    private AtcHost.Status _atcStatus = new(AtcHost.Phase.Waiting, null, false);
    private AtcAppItem? _selectedAtcApp;
    private bool _syncingSelection;
    private IDisposable? _noticeTimer;

    public sealed record AtcAppItem(string Label, string? Exe, bool IsOther)
    {
        public override string ToString() => Label;
    }

    public ShareViewModel(ShareSwitch share, SimTraffic simTraffic, INetwork net, TrafficReceiver traffic,
        AtcHost atcHost, AtcReceiver atcReceiver, Settings settings)
    {
        _share = share;
        _atcHost = atcHost;
        _atcReceiver = atcReceiver;
        _settings = settings;

        net.Peers
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(peers =>
            {
                _peerNames.Clear();
                foreach (var p in peers) _peerNames[p.PeerId] = string.IsNullOrWhiteSpace(p.Name) ? p.PeerId : p.Name;
                var active = peers.Count > 0;
                if (active != _sessionActive)
                {
                    _sessionActive = active;
                    if (!active) EndSession();
                    this.RaisePropertyChanged(nameof(TrafficCanHost));
                    this.RaisePropertyChanged(nameof(AtcCanHost));
                    this.RaisePropertyChanged(nameof(NeedsSession));
                    this.RaisePropertyChanged(nameof(TrafficHint));
                }
                this.RaisePropertyChanged(nameof(TrafficSharedBy));
                this.RaisePropertyChanged(nameof(AtcSharedBy));
            })
            .DisposeWith(_d);

        // Traffic hosting is the toggle and the sim together.
        simTraffic.Connected
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(connected =>
            {
                _simConnected = connected;
                ApplyTrafficIntent();
                this.RaisePropertyChanged(nameof(TrafficCanHost));
                this.RaisePropertyChanged(nameof(TrafficHint));
            })
            .DisposeWith(_d);

        share.Host(ShareSwitch.Feature.Traffic)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(host =>
            {
                _trafficHost = host;
                this.RaisePropertyChanged(nameof(TrafficCanHost));
                this.RaisePropertyChanged(nameof(TrafficSharedBy));
                this.RaisePropertyChanged(nameof(TrafficReceiving));
                this.RaisePropertyChanged(nameof(ShowNote));
            })
            .DisposeWith(_d);

        share.Host(ShareSwitch.Feature.Atc)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(host =>
            {
                _atcHostPeer = host;
                this.RaisePropertyChanged(nameof(AtcCanHost));
                this.RaisePropertyChanged(nameof(AtcSharedBy));
                this.RaisePropertyChanged(nameof(AtcReceiving));
                this.RaisePropertyChanged(nameof(ShowNote));
            })
            .DisposeWith(_d);

        share.Lost
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(feature =>
            {
                var who = feature == ShareSwitch.Feature.Traffic ? _trafficHost : _atcHostPeer;
                if (feature == ShareSwitch.Feature.Traffic) { _trafficOn = false; this.RaisePropertyChanged(nameof(TrafficOn)); }
                else { _atcOn = false; this.RaisePropertyChanged(nameof(AtcOn)); }
                Notice = $"{Name(who)} is already sharing {(feature == ShareSwitch.Feature.Traffic ? "traffic" : "ATC audio")}.";
            })
            .DisposeWith(_d);

        traffic.ForeignAiCount
            .Select(n => n > 0)
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(foreign => { _foreignTraffic = foreign; this.RaisePropertyChanged(nameof(ForeignTrafficWarning)); })
            .DisposeWith(_d);

        atcHost.CurrentStatus
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status => { _atcStatus = status; this.RaisePropertyChanged(nameof(AtcStatus)); })
            .DisposeWith(_d);

        atcHost.Detected
            .CombineLatest(atcHost.Sessions, (detected, sessions) => (detected, sessions))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => RebuildApps(x.detected, x.sessions))
            .DisposeWith(_d);
    }

    public void Dispose()
    {
        _noticeTimer?.Dispose();
        _d.Dispose();
    }

    /// <summary>The last peer left: whatever we shared stops with the session.</summary>
    private void EndSession()
    {
        if (_trafficOn) { _trafficOn = false; this.RaisePropertyChanged(nameof(TrafficOn)); }
        if (_atcOn) { _atcOn = false; this.RaisePropertyChanged(nameof(AtcOn)); }
        _share.StopAll();
        this.RaisePropertyChanged(nameof(ShowNote));
    }

    // -- traffic ----------------------------------------------------------------------------

    public bool TrafficOn
    {
        get => _trafficOn;
        set
        {
            if (_trafficOn == value) return;
            _trafficOn = value;
            this.RaisePropertyChanged();
            ApplyTrafficIntent();
            this.RaisePropertyChanged(nameof(ShowNote));
        }
    }

    /// <summary>In a session, the sim is up, and nobody else shares traffic.</summary>
    public bool TrafficCanHost => _sessionActive && _simConnected && (_trafficHost is null || _trafficHost == _share.SelfId);
    public string TrafficSharedBy => _trafficHost is { } h && h != _share.SelfId ? $"Traffic · shared by {Name(h)}" : string.Empty;
    public bool TrafficReceiving => TrafficSharedBy.Length > 0;
    public bool ForeignTrafficWarning => TrafficReceiving && _foreignTraffic;
    public string TrafficHint => _sessionActive && !_simConnected && !TrafficReceiving ? "Waiting for the simulator" : string.Empty;

    private void ApplyTrafficIntent() => _share.Request(ShareSwitch.Feature.Traffic, _trafficOn && _simConnected && _sessionActive);

    // -- ATC --------------------------------------------------------------------------------

    public bool AtcOn
    {
        get => _atcOn;
        set
        {
            if (_atcOn == value) return;
            _atcOn = value;
            this.RaisePropertyChanged();
            _share.Request(ShareSwitch.Feature.Atc, value && _sessionActive);
            this.RaisePropertyChanged(nameof(ShowNote));
        }
    }

    /// <summary>In a session and nobody else shares ATC.</summary>
    public bool AtcCanHost => _sessionActive && (_atcHostPeer is null || _atcHostPeer == _share.SelfId);
    public string AtcSharedBy => _atcHostPeer is { } h && h != _share.SelfId ? $"ATC audio · shared by {Name(h)}" : string.Empty;
    public bool AtcReceiving => AtcSharedBy.Length > 0;

    public string AtcStatus => _atcStatus.Phase switch
    {
        AtcHost.Phase.Capturing when _atcStatus.MixerMuted => $"● capturing {_atcStatus.App} — muted in the volume mixer",
        AtcHost.Phase.Capturing => $"● capturing {_atcStatus.App}",
        AtcHost.Phase.Closed => $"○ {_atcStatus.App} closed",
        AtcHost.Phase.Unsupported => "○ audio capture needs Windows 10 2004 or later",
        _ => "○ waiting for an ATC app…"
    };

    public ObservableCollection<AtcAppItem> AtcApps { get; } = [];

    public AtcAppItem? SelectedAtcApp
    {
        get => _selectedAtcApp;
        set
        {
            if (_syncingSelection || value is null || value == _selectedAtcApp) return;
            if (value.IsOther)
            {
                _atcHost.ShowSessions = true;   // the list refills with every process that makes sound
                return;
            }
            _selectedAtcApp = value;
            this.RaisePropertyChanged();
            _atcHost.PreferredApp = value.Exe;
            _settings.AtcApp = value.Exe;
            _settings.Save();
        }
    }

    private void RebuildApps(IReadOnlyList<Audio.AtcApps.App> detected, IReadOnlyList<Audio.AtcApps.App> sessions)
    {
        var items = new List<AtcAppItem>();
        foreach (var app in detected) items.Add(new AtcAppItem(app.Label, app.Exe, false));
        if (_atcHost.ShowSessions)
        {
            foreach (var app in sessions)
                if (items.All(i => i.Exe != app.Exe)) items.Add(new AtcAppItem(app.Label, app.Exe, false));
        }
        else items.Add(new AtcAppItem("Other…", null, true));

        var target = _atcHost.Target(detected);
        var selected = target is null ? null : items.FirstOrDefault(i => i.Exe == target.Exe);
        if (selected is null && target is not null) { selected = new AtcAppItem(target.Label, target.Exe, false); items.Insert(0, selected); }

        if (items.Select(i => i.Label).SequenceEqual(AtcApps.Select(i => i.Label)) && selected?.Label == _selectedAtcApp?.Label) return;
        _syncingSelection = true;
        try
        {
            AtcApps.Clear();
            foreach (var i in items) AtcApps.Add(i);
            _selectedAtcApp = selected;
            this.RaisePropertyChanged(nameof(SelectedAtcApp));
        }
        finally { _syncingSelection = false; }
    }

    public double AtcVolume
    {
        get => _atcReceiver.Volume;
        set { _atcReceiver.Volume = value; this.RaisePropertyChanged(); }
    }

    public bool AtcMuted
    {
        get => _atcReceiver.Muted;
        set { _atcReceiver.Muted = value; this.RaisePropertyChanged(); }
    }

    public void ToggleMute() => AtcMuted = !AtcMuted;

    // -- card -------------------------------------------------------------------------------

    public string Notice
    {
        get => _notice ?? string.Empty;
        private set
        {
            _notice = value;
            this.RaisePropertyChanged();
            _noticeTimer?.Dispose();
            if (string.IsNullOrEmpty(value)) return;
            _noticeTimer = Observable.Timer(NoticeFor).ObserveOn(RxApp.MainThreadScheduler).Subscribe(_ => Notice = string.Empty);
        }
    }

    /// <summary>No session yet: the card says why its controls are off.</summary>
    public bool NeedsSession => !_sessionActive;

    /// <summary>The requirements line, only while something is being shared either way.</summary>
    public bool ShowNote => _trafficOn || _atcOn || TrafficReceiving || AtcReceiving;

    public string? TrafficHostId => _trafficHost;
    public string? AtcHostId => _atcHostPeer;

    private string Name(string? peerId) => peerId is null ? "someone" : _peerNames.GetValueOrDefault(peerId, peerId);
}
