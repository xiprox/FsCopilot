namespace FsCopilot.Audio;

using Network;
using Simulation;

/// <summary>
/// The receiving side of ATC audio: frames from the peer hosting ATC go to an
/// <see cref="AtcPlayer"/> that exists only while someone else hosts. Volume and mute are the
/// receiver's own, persisted.
/// </summary>
public sealed class AtcReceiver : IDisposable
{
    private readonly ShareSwitch _share;
    private readonly Settings _settings;
    private readonly CompositeDisposable _d = new();
    private readonly Lock _lock = new();
    private AtcPlayer? _player;
    private volatile string? _host;

    public AtcReceiver(INetwork net, ShareSwitch share, Settings settings)
    {
        _share = share;
        _settings = settings;

        _d.Add(share.Host(ShareSwitch.Feature.Atc)
            .Subscribe(host =>
            {
                var foreign = host is not null && host != share.SelfId ? host : null;
                if (foreign == _host) return;
                _host = foreign;
                lock (_lock)
                {
                    _player?.Dispose();
                    _player = null;
                    if (foreign is null) { Log.Information("[Atc] Not receiving"); return; }
                    Log.Information("[Atc] Receiving from {Host}", foreign);
                    _player = new AtcPlayer { Volume = (float)settings.AtcVolume, Muted = settings.AtcMuted };
                }
            }));

        _d.Add(net.Stream<AtcFrame>().Subscribe(f =>
        {
            if (f.Host != _host) return;
            lock (_lock) _player?.Enqueue(f.Seq, f.Opus);
        }));
    }

    public bool Receiving => _host is not null;

    public double Volume
    {
        get => _settings.AtcVolume;
        set
        {
            _settings.AtcVolume = Math.Clamp(value, 0, 1);
            _settings.Save();
            lock (_lock) if (_player is { } p) p.Volume = (float)_settings.AtcVolume;
        }
    }

    public bool Muted
    {
        get => _settings.AtcMuted;
        set
        {
            _settings.AtcMuted = value;
            _settings.Save();
            lock (_lock) if (_player is { } p) p.Muted = value;
        }
    }

    public void Dispose()
    {
        _d.Dispose();
        lock (_lock) { _player?.Dispose(); _player = null; }
    }
}
