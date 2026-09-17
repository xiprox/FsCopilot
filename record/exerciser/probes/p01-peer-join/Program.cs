// Probe: can a separate process join a running FS Copilot as a peer, on the same
// machine, through the public rendezvous, and exchange PointerEvents both ways?
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using FsCopilot.Connection;
using FsCopilot.Network;
using FsCopilot.Simulation;
using Serilog;

var fscPeer = args[0];
var panelPort = int.Parse(args[1]);
var key = args[2];
var host = args.Length > 3 ? args[3] : "p2p.fscopilot.com";
var t0 = DateTime.UtcNow;
string T() => $"{(DateTime.UtcNow - t0).TotalSeconds,6:0.00}";

Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// 1. Observer connection to FSC's PanelServer: a native client, no Origin header.
var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{panelPort}/"), CancellationToken.None);
Console.WriteLine($"{T()} PROBE observer connected to PanelServer :{panelPort}");
await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"hello\",\"name\":\"exerciser|observer\",\"url\":\"exerciser://probe\"}"),
    WebSocketMessageType.Text, true, CancellationToken.None);
_ = Task.Run(async () =>
{
    var buf = new byte[65536];
    string? lastState = null;
    while (ws.State == WebSocketState.Open)
    {
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        if (r.MessageType == WebSocketMessageType.Close) break;
        var text = Encoding.UTF8.GetString(buf, 0, r.Count);
        if (text.Contains("\"state\"")) { if (text == lastState) continue; lastState = text; }
        Console.WriteLine($"{T()} PROBE observer <- {text}");
    }
});

// 2. The peer. Same packets, same order as FSC: MasterSwitch, then Coordinator.
var myId = "EXRC" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
var net = new HybridNetwork(host, myId, "exerciser");
var register = typeof(HybridNetwork).GetMethod("RegisterPacket")!;
void Reg(Type packet, Type codec) => register.MakeGenericMethod(packet, codec).Invoke(net, null);
const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;
var setMaster = typeof(MasterSwitch).GetNestedType("SetMaster", Any)!;
var update = typeof(Coordinator).GetNestedType("Update", Any)!;
Reg(setMaster, setMaster.GetNestedType("Codec", Any)!);
Reg(update, update.GetNestedType("Codec", Any)!);
Reg(typeof(Interact), typeof(Coordinator).GetNestedType("InteractCodec", Any)!);
Reg(typeof(Physics), typeof(Physics.Codec));
Reg(typeof(Surfaces), typeof(Surfaces.Codec));
Reg(typeof(PointerEvent), typeof(PointerEvent.Codec));
Reg(typeof(PointerAck), typeof(PointerAck.Codec));

var live = new TaskCompletionSource();
string? lastPeers = null;
net.Peers.Subscribe(ps =>
{
    var s = string.Join(", ", ps.Select(p => $"{p.PeerId} {p.Transport} connected={p.Connected}"));
    if (s != lastPeers) { lastPeers = s; Console.WriteLine($"{T()} PROBE peers: [{string.Join(", ", ps.Select(p => $"{p.PeerId} {p.Transport} connected={p.Connected}"))}]"); }
    if (ps.Any(p => p.PeerId == fscPeer && p.Connected)) live.TrySetResult();
});
var gotEvent = new TaskCompletionSource<PointerEvent>();
net.Stream<PointerEvent>().Subscribe(e =>
{
    Console.WriteLine($"{T()} PROBE <- PointerEvent {e.Kind} key={e.Key} seq={e.Seq} down=({e.DownX},{e.DownY}) up=({e.UpX},{e.UpY}) hold={e.HoldMs} path={e.Path.Length}");
    gotEvent.TrySetResult(e);
});
net.Stream<PointerAck>().Subscribe(a => Console.WriteLine($"{T()} PROBE <- PointerAck session={a.Session} seq={a.Seq}"));

Console.WriteLine($"{T()} PROBE {myId} joining {fscPeer} via {host}");
var result = await net.Connect(fscPeer, new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);
Console.WriteLine($"{T()} PROBE connect result: {result}");
if (await Task.WhenAny(live.Task, Task.Delay(30000)) != live.Task) { Console.WriteLine($"{T()} PROBE FAIL: never live"); return 1; }
Console.WriteLine($"{T()} PROBE LIVE");

await Task.Delay(3000); // FSC's sync state goes live on its 2 s tick
var session = (ulong)Random.Shared.NextInt64();
net.SendAll(new PointerEvent(key, session, 1, 0, PointerKind.Press, 0, 120, 0, 0.25f, 0.5f, 0.25f, 0.5f, PointerEvent.NoPath));
Console.WriteLine($"{T()} PROBE -> PointerEvent press key={key} seq=1");

var ok = await Task.WhenAny(gotEvent.Task, Task.Delay(30000)) == gotEvent.Task;
Console.WriteLine($"{T()} PROBE {(ok ? "PASS: received a capture from FSC" : "FAIL: nothing came back")}");
await Task.Delay(45000); // watch for a relay -> direct upgrade
net.Disconnect();
net.DrainDisconnect(TimeSpan.FromSeconds(1));
return ok ? 0 : 2;
