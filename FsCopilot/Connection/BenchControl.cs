namespace FsCopilot.Connection;

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Network;
using Simulation;
using ViewModels;
using Avalonia.Threading;

/*
 * Test-only. Started by --bench <port> and by nothing else, and deliberately kept to
 * one file so the upstream PR is a subset that simply does not include it.
 *
 * The bench runs two app instances on one machine and has to drive them without a
 * mouse: join, leave, set the pointer opt-in list that normally arrives from an
 * aircraft profile, and degrade the peer link. Newline-delimited JSON over loopback
 * TCP, one request per line, one reply per line, correlated by id.
 *
 * Session state is not here. A bench panel is a real panel socket and already gets
 * {t:"state"} every two seconds from PanelServer, so the bench watches the state the
 * pilot's panel would see rather than a second one maintained for tests.
 *
 *   {"id":1,"c":"configure","keys":["A320_CDU|1"]}   the pointer: list, without a sim
 *   {"id":2,"c":"join","code":"ALPHA001"}            what pressing Join does
 *   {"id":3,"c":"leave"}                             what pressing Leave does
 *   {"id":4,"c":"peers"}                             connected peer ids, and master
 *   {"id":5,"c":"netsim","loss":20,"minLat":80,"maxLat":300}
 *   {"id":6,"c":"quit"}                              the clean exit path, with its goodbye
 */
public sealed class BenchControl : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly PanelServer _panels;
    private readonly MainViewModel _vm;
    private readonly INetwork _net;
    private readonly MasterSwitch _master;
    private readonly Coordinator? _coordinator;

    private BenchControl(int port, PanelServer panels, MainViewModel vm, INetwork net, MasterSwitch master,
        Coordinator? coordinator)
    {
        _panels = panels;
        _vm = vm;
        _net = net;
        _master = master;
        _coordinator = coordinator;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Log.Information("[Bench] Control channel on 127.0.0.1:{Port}", port);
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    /// <summary>Starts the control channel if --bench &lt;port&gt; is present. Every service it
    /// needs is resolved here rather than injected, so nothing outside this file knows it
    /// exists.</summary>
    public static BenchControl? Start(string[] args, Func<Type, object?> resolve)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var port)) return null;

        var panels = resolve(typeof(PanelServer)) as PanelServer;
        var vm = resolve(typeof(MainViewModel)) as MainViewModel;
        var net = resolve(typeof(INetwork)) as INetwork;
        var master = resolve(typeof(MasterSwitch)) as MasterSwitch;
        if (panels == null || vm == null || net == null || master == null)
        {
            Log.Warning("[Bench] --bench given but the app is not in a mode that has a session; ignored");
            return null;
        }

        var coordinator = resolve(typeof(Coordinator)) as Coordinator;
        try { return new BenchControl(port, panels, vm, net, master, coordinator); }
        catch (Exception e)
        {
            Log.Error("[Bench] Could not listen on {Port}: {Error}", port, e.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            using var reader = new StreamReader(client.GetStream());
            using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (Exception) { return; }
                if (line == null) return;
                if (line.Trim().Length == 0) continue;

                var reply = await Handle(line);
                try { await writer.WriteLineAsync(reply); }
                catch (Exception) { return; }
            }
        }
    }

    private async Task<string> Handle(string line)
    {
        JsonElement req;
        try { req = JsonDocument.Parse(line).RootElement; }
        catch (Exception e) { return Fail(0, $"bad JSON: {e.Message}"); }

        var id = req.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var n) ? n : 0;
        var command = req.String("c");

        try
        {
            switch (command)
            {
                case "ping":
                    return Ok(id, w => w.WriteString("peerId", _vm.PeerId));

                case "configure":
                    var keys = new List<string>();
                    if (req.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var k in arr.EnumerateArray())
                            if (k.ValueKind == JsonValueKind.String) keys.Add(k.GetString()!);
                    Configure(keys);
                    return Ok(id, w => w.WriteNumber("keys", keys.Count));

                case "join":
                    var code = req.String("code");
                    if (code.Length != 8) return Fail(id, "code must be 8 characters");
                    // JoinCommand is a ReactiveCommand on the view model and touches
                    // IsBusy, so it runs where the bindings live. Awaited to completion:
                    // the bench needs "the attempt resolved", not "the attempt started".
                    await OnUi(() => _vm.ConnectionCode = code);
                    await _vm.JoinCommand.Execute();
                    return Ok(id, w => w.WriteBoolean("connected", _vm.Connected));

                case "leave":
                    await _vm.LeaveCommand.Execute();
                    return Ok(id, _ => { });

                case "take-control":
                    // The way out of a degraded lock: the overlay stands only on the slave,
                    // so taking control is what clears it without the peer coming back.
                    _master.TakeControl();
                    return Ok(id, w => w.WriteBoolean("master", _master.IsMaster));

                case "peers":
                    return Ok(id, w =>
                    {
                        w.WriteBoolean("connected", _vm.Connected);
                        w.WriteBoolean("master", _master.IsMaster);
                        w.WriteStartArray("peers");
                        foreach (var c in _vm.Connections.ToArray()) w.WriteStringValue(c.PeerId);
                        w.WriteEndArray();
                    });

                case "netsim":
                    var loss = req.TryGetProperty("loss", out var l) ? l.GetInt32() : 0;
                    var min = req.TryGetProperty("minLat", out var lo) ? lo.GetInt32() : 0;
                    var max = req.TryGetProperty("maxLat", out var hi) ? hi.GetInt32() : 0;
                    var applied = NetSim(loss, min, max);
                    return applied < 0
                        ? Fail(id, "LiteNetLib simulation fields not present in this build")
                        : Ok(id, w => w.WriteNumber("managers", applied));

                case "quit":
                    // Posted, not awaited: Shutdown runs on the way out and would be
                    // waiting on this reply to be written by the thread it is blocking.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(50);
                        await OnUi(() =>
                        {
                            if (Avalonia.Application.Current?.ApplicationLifetime
                                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
                                d.Shutdown();
                        });
                    });
                    return Ok(id, _ => { });

                default:
                    return Fail(id, $"unknown command '{command}'");
            }
        }
        catch (Exception e)
        {
            return Fail(id, e.Message);
        }
    }

    /// <summary>
    /// The pointer opt-in list, as an aircraft profile would deliver it.
    ///
    /// Through Coordinator.Load, not PanelServer.Configure: Configure only tells panels
    /// which of them capture, and the Coordinator keeps its own copy as the filter on
    /// both directions of the wire. Setting one and not the other gives panels that
    /// capture and an app that drops everything they send, which is what the first
    /// bench run did.
    ///
    /// Definitions has no public constructor and is built by parsing a profile, so the
    /// private one is called directly. That also lets the two instances hold different
    /// lists, which one shared profile on disk could not, and a peer whose profile
    /// differs is a case the inbound filter exists for.
    /// </summary>
    private void Configure(List<string> keys)
    {
        var ctor = typeof(Definitions).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(string), typeof(DateTime), typeof(Definition[]), typeof(string[]), typeof(string[])], null)
            ?? throw new InvalidOperationException("Definitions constructor has changed; BenchControl needs updating");

        var definitions = (Definitions)ctor.Invoke(["bench", DateTime.UtcNow, Array.Empty<Definition>(),
            Array.Empty<string>(), keys.ToArray()]);

        if (_coordinator != null) _coordinator.Load(definitions);
        else _panels.Configure(keys);
        Log.Information("[Bench] Pointer keys: {Keys}", keys.Count == 0 ? "(none)" : string.Join(", ", keys));
    }

    /// <summary>
    /// Sets LiteNetLib's packet loss and latency simulation on every NetManager the
    /// network holds. Reached by reflection: the fields are LiteNetLib's own debug
    /// knobs and neither HybridNetwork nor its children expose them, and widening a
    /// shipping class for the bench is the wrong trade. Returns the number of managers
    /// set, or -1 when the LiteNetLib build has the fields compiled out.
    /// </summary>
    private int NetSim(int loss, int minLatency, int maxLatency)
    {
        var managers = new List<object>();
        Collect(_net);
        if (managers.Count == 0) return 0;

        var type = managers[0].GetType();
        var simLoss = type.GetField("SimulatePacketLoss");
        var simLat = type.GetField("SimulateLatency");
        var chance = type.GetField("SimulationPacketLossChance");
        var minF = type.GetField("SimulationMinLatency");
        var maxF = type.GetField("SimulationMaxLatency");
        if (simLoss == null || simLat == null || chance == null || minF == null || maxF == null) return -1;

        foreach (var m in managers)
        {
            simLoss.SetValue(m, loss > 0);
            chance.SetValue(m, Math.Clamp(loss, 1, 100));
            simLat.SetValue(m, maxLatency > 0);
            minF.SetValue(m, Math.Max(minLatency, 0));
            maxF.SetValue(m, Math.Max(maxLatency, minLatency));
        }

        Log.Information("[Bench] Netsim: {Loss}% loss, {Min}-{Max}ms on {Count} manager(s)",
            loss, minLatency, maxLatency, managers.Count);
        return managers.Count;

        void Collect(object? holder)
        {
            if (holder == null) return;
            foreach (var f in holder.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                var value = f.GetValue(holder);
                if (value == null) continue;
                if (value.GetType().FullName == "LiteNetLib.NetManager") managers.Add(value);
                else if (f.FieldType.Namespace == "FsCopilot.Network") Collect(value);
            }
        }
    }

    private static Task OnUi(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    private static string Ok(int id, Action<Utf8JsonWriter> body) => Write(w =>
    {
        w.WriteNumber("id", id);
        w.WriteBoolean("ok", true);
        body(w);
    });

    private static string Fail(int id, string error) => Write(w =>
    {
        w.WriteNumber("id", id);
        w.WriteBoolean("ok", false);
        w.WriteString("error", error);
    });

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
