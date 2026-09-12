namespace FsCopilot.Connection;

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

/// <summary>
/// Local WebSocket endpoint for panel documents. Cockpit JS (see the bridge package's
/// FsCopilot/channel.js) connects here directly, bypassing the CommBus/WASM/SimConnect
/// bus and its 512-byte single-slot buffer. Panels identify themselves per instrument
/// with a hello message and receive the current pointer configuration and session state
/// in return; the state is re-broadcast every 2 seconds so a panel can treat silence as
/// "the app is gone" and fail open. A deliberate shutdown says goodbye first, so a panel
/// can tell a quit from a crash - a dropped socket looks identical either way.
/// </summary>
public sealed class PanelServer : IDisposable
{
    // channel.js rotates through the same range in its reconnect backoff, so the app
    // binds the first free port and panels find it without any side channel.
    private static readonly int[] Ports = [9020, 9021, 9022, 9023, 9024];

    private static readonly TimeSpan StateRenewal = TimeSpan.FromSeconds(2);

    // How long a deliberate shutdown waits for the goodbye to reach its panels. Bounded
    // hard: this runs on the way out of the process, and a wedged socket must not hold
    // the app open. Loopback delivery is sub-millisecond when it works at all.
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromMilliseconds(750);

    // Bounds on events held for a panel that has not helloed its key yet (still loading,
    // or reloading on a view change). Flushed in order on hello; capped so a key that
    // never appears cannot leak.
    private static readonly TimeSpan PendingMaxAge = TimeSpan.FromMinutes(5);
    private const int PendingMaxCount = 500;

    private readonly HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<PanelSocket, byte> _sockets = new();
    private readonly BehaviorSubject<bool> _bindFailed = new(false);
    private readonly CompositeDisposable _d = new();
    private readonly Subject<PointerEvent> _events = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(string Json, DateTime At)>> _pending = new();

    private volatile string[] _pointerKeys = [];
    private volatile string _session = SessionState.None;
    private volatile bool _isMaster = true;

    public int Port { get; } = -1;

    /// <summary>True when every port in the range was taken and the feature is off.</summary>
    public IObservable<bool> BindFailed => _bindFailed.ObserveOn(TaskPoolScheduler.Default);

    /// <summary>Gestures captured by panels on this machine, presses and drags on one stream
    /// in capture order. Session/Seq are unstamped here.</summary>
    public IObservable<PointerEvent> Events => _events.ObserveOn(TaskPoolScheduler.Default);

    public PanelServer()
    {
        foreach (var port in Ports)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (Exception e)
            {
                Log.Debug("[PanelServer] Port {Port} unavailable: {Error}", port, e.Message);
                ((IDisposable)listener).Dispose();
            }
        }

        if (_listener == null)
        {
            Log.Warning("[PanelServer] All ports {Ports} in use; panel channel disabled", string.Join(", ", Ports));
            _bindFailed.OnNext(true);
            return;
        }

        Log.Information("[PanelServer] Listening on 127.0.0.1:{Port}", Port);
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _d.Add(Observable.Interval(StateRenewal).Subscribe(_ => Broadcast(StateJson())));
    }

    /// <summary>Replaces the pointer opt-in list (full panel keys) and pushes it to every panel.</summary>
    public void Configure(IReadOnlyCollection<string> pointerKeys)
    {
        _pointerKeys = pointerKeys.ToArray();
        Broadcast(ConfigJson());
    }

    /// <summary>
    /// Dev harness only (--dev): captures from pointer-opted panels are reflected
    /// straight back, exercising the whole panel -> app -> panel path on one machine.
    /// The reflected press visibly actuates twice - that is the signal that the
    /// pipeline works, not a bug. Which panels participate comes from the aircraft
    /// profile's pointer: list via Configure, exactly as in normal mode.
    /// </summary>
    public void EnableDevEcho()
    {
        _d.Add(Events.Subscribe(Send));
        Log.Information("[PanelServer] Dev echo enabled: panel captures reflect back to their panels");
    }

    /// <summary>Updates the session state broadcast to panels; drives the overlay lock.</summary>
    public void SetSession(string session, bool isMaster)
    {
        if (_session == session && _isMaster == isMaster) return;
        _session = session;
        _isMaster = isMaster;
        Broadcast(StateJson());
    }

    /// <summary>Replays a peer's gesture on every panel that helloed with its key, or holds it
    /// for a panel that has not appeared yet (loading, or reloading on a view change).</summary>
    public void Send(PointerEvent e) => Route(e.Key, Json(w =>
    {
        w.WriteString("t", "pointer");
        w.WriteStartObject("msg");
        w.WriteNumber("v", 5);
        w.WriteString("k", e.Kind == PointerKind.Drag ? "drag" : "press");
        w.WriteString("key", e.Key);
        w.WriteNumber("button", e.Button);
        if (e.Kind == PointerKind.Drag)
        {
            w.WriteStartArray("path");
            // The wire carries per-step deltas; the panel replays against absolute
            // times from the gesture start, so rebuild them here.
            var at = 0;
            foreach (var point in e.Path)
            {
                at += point.DtMs;
                w.WriteStartArray();
                w.WriteNumberValue(at);
                w.WriteNumberValue(point.X);
                w.WriteNumberValue(point.Y);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        else
        {
            w.WriteNumber("nx", e.DownX);
            w.WriteNumber("ny", e.DownY);
            w.WriteNumber("ux", e.UpX);
            w.WriteNumber("uy", e.UpY);
            w.WriteNumber("hold", e.HoldMs);
        }
        w.WriteEndObject();
    }));

    private void Route(string key, string text)
    {
        var delivered = false;
        foreach (var socket in _sockets.Keys)
        {
            if (!socket.HasName(key)) continue;
            Send(socket, text);
            delivered = true;
        }
        if (delivered) return;

        var queue = _pending.GetOrAdd(key, _ => new ConcurrentQueue<(string, DateTime)>());
        queue.Enqueue((text, DateTime.UtcNow));
        while (queue.Count > PendingMaxCount && queue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Announces a deliberate shutdown to every connected panel, then disposes. Without
    /// it a panel sees only a dropped socket - indistinguishable from a crashed app - and
    /// warns the pilot that sync broke when nothing broke. Only the app's own exit path
    /// reaches this; a kill or a crash rightly does not, which is what leaves the warning
    /// for the cases that deserve it.
    /// </summary>
    public void Shutdown()
    {
        var sockets = _sockets.Keys.ToArray();
        if (sockets.Length > 0)
        {
            var bye = Json(w => w.WriteString("t", "bye"));
            try
            {
                // Task.Run, then Wait: this is called from the UI thread on the way out,
                // and awaiting a socket write there would post its continuation back to
                // the very thread the Wait is blocking. Off the pool there is no context
                // to deadlock against, and the grace period bounds the wait either way.
                Task.Run(() => Task.WhenAll(sockets.Select(s => Farewell(s, bye))))
                    .Wait(ShutdownGrace);
                Log.Debug("[PanelServer] Announced shutdown to {Count} panel socket(s)", sockets.Length);
            }
            catch (Exception e)
            {
                Log.Debug("[PanelServer] Shutdown announce failed: {Error}", e.Message);
            }
        }

        Dispose();
    }

    /// <summary>Sends the goodbye and closes cleanly, so the frame is flushed rather than
    /// discarded under the abort in <see cref="Dispose"/>.</summary>
    private async Task Farewell(PanelSocket socket, string bye)
    {
        await SendAsync(socket, bye);
        try
        {
            if (socket.Ws.State == WebSocketState.Open)
            {
                await socket.Ws.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
            }
        }
        catch (Exception) { /* the panel is losing us either way */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _d.Dispose();
        foreach (var socket in _sockets.Keys) socket.Close();
        if (_listener != null) ((IDisposable)_listener).Dispose();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Log.Debug("[PanelServer] Accept failed: {Error}", e.Message);
                continue;
            }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            _ = Task.Run(async () =>
            {
                WebSocketContext wsCtx;
                try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
                catch (Exception e)
                {
                    Log.Debug("[PanelServer] Upgrade failed: {Error}", e.Message);
                    return;
                }

                var socket = new PanelSocket(wsCtx.WebSocket);
                _sockets.TryAdd(socket, 0);
                Log.Debug("[PanelServer] Panel connected ({Count} total)", _sockets.Count);
                try { await ReceiveLoop(socket, ct); }
                finally
                {
                    _sockets.TryRemove(socket, out _);
                    socket.Close();
                    Log.Debug("[PanelServer] Panel disconnected {Names} ({Count} left)",
                        socket.NamesSnapshot(), _sockets.Count);
                }
            }, ct);
        }
    }

    private async Task ReceiveLoop(PanelSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.Ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try { result = await socket.Ws.ReceiveAsync(buffer, ct); }
            catch (Exception) { return; }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await socket.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch (Exception) { /* peer is gone either way */ }
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > 256 * 1024) return; // no legitimate panel message is this big
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            Handle(socket, text);
        }
    }

    private void Handle(PanelSocket socket, string text)
    {
        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement; }
        catch (Exception e)
        {
            Log.Debug("[PanelServer] Invalid JSON from panel: {Error}", e.Message);
            return;
        }

        switch (json.String("t"))
        {
            case "hello":
                var name = json.String("name");
                if (name.Length == 0) return;
                socket.AddName(name);
                Log.Debug("[PanelServer] Hello from {Name} ({Url})", name, json.String("url"));
                // Reply with the current config and state so a panel that came up after the
                // profile loaded still learns its mode; Configure() broadcasts later changes.
                Send(socket, ConfigJson());
                Send(socket, StateJson());
                FlushPending(socket, name);
                break;
            case "pointer":
                HandlePointer(json);
                break;
            case "stats":
                Log.Debug("[PanelServer] Panel stats: {Stats}", text);
                break;
        }
    }

    private void FlushPending(PanelSocket socket, string name)
    {
        if (!_pending.TryRemove(name, out var queue)) return;
        var cutoff = DateTime.UtcNow - PendingMaxAge;
        var flushed = 0;
        while (queue.TryDequeue(out var item))
        {
            if (item.At < cutoff) continue;
            Send(socket, item.Json);
            flushed++;
        }
        if (flushed > 0) Log.Debug("[PanelServer] Flushed {Count} held events to {Name}", flushed, name);
    }

    private void HandlePointer(JsonElement json)
    {
        if (!json.TryGetProperty("msg", out var msg)) return;
        var key = msg.String("key");
        if (key.Length == 0) return;
        var button = (byte)msg.Double("button");

        switch (msg.String("k"))
        {
            case "press":
                var nx = (float)msg.Double("nx");
                var ny = (float)msg.Double("ny");
                _events.OnNext(new PointerEvent(key, 0, 0, 0, PointerKind.Press, button,
                    (ushort)Math.Clamp(msg.Double("hold"), 0, 1500),
                    nx, ny, (float)msg.Double("ux", nx), (float)msg.Double("uy", ny), PointerEvent.NoPath));
                break;
            case "drag":
                if (!msg.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array) return;
                var points = new List<PointerEvent.Point>();
                var prev = 0.0;
                foreach (var p in path.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 3) continue;
                    // Capture reports absolute ms from the gesture start; the wire carries deltas.
                    var at = p[0].GetDouble();
                    var dt = Math.Clamp(at - prev, 0, ushort.MaxValue);
                    prev = at;
                    points.Add(new PointerEvent.Point((ushort)dt, (float)p[1].GetDouble(), (float)p[2].GetDouble()));
                }
                if (points.Count < 2) return;
                var first = points[0];
                var last = points[^1];
                _events.OnNext(new PointerEvent(key, 0, 0, 0, PointerKind.Drag, button, 0,
                    first.X, first.Y, last.X, last.Y, points.ToArray()));
                break;
        }
    }

    private string ConfigJson() => Json(w =>
    {
        w.WriteString("t", "config");
        w.WriteStartArray("pointer");
        foreach (var key in _pointerKeys) w.WriteStringValue(key);
        w.WriteEndArray();
    });

    private string StateJson() => Json(w =>
    {
        w.WriteString("t", "state");
        w.WriteString("session", _session);
        w.WriteString("role", _isMaster ? "master" : "slave");
    });

    private void Broadcast(string text)
    {
        foreach (var socket in _sockets.Keys) Send(socket, text);
    }

    private void Send(PanelSocket socket, string text) => _ = SendAsync(socket, text);

    private async Task SendAsync(PanelSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendLock.WaitAsync();
        try
        {
            if (socket.Ws.State != WebSocketState.Open) return;
            await socket.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception e)
        {
            Log.Debug("[PanelServer] Send failed: {Error}", e.Message);
        }
        finally
        {
            socket.SendLock.Release();
        }
    }

    private static string Json(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal sealed class PanelSocket(WebSocket ws)
    {
        public WebSocket Ws { get; } = ws;
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        private readonly HashSet<string> _names = [];

        public void AddName(string name) { lock (_names) _names.Add(name); }
        public bool HasName(string name) { lock (_names) return _names.Contains(name); }
        public string NamesSnapshot() { lock (_names) return string.Join(", ", _names); }

        public void Close()
        {
            try { Ws.Abort(); } catch (Exception) { /* already gone */ }
        }
    }
}

/// <summary>Session states broadcast to panels; string-valued because they go out as JSON.</summary>
public static class SessionState
{
    public const string None = "none";
    public const string Connecting = "connecting";
    public const string Live = "live";
    public const string Degraded = "degraded";
}
