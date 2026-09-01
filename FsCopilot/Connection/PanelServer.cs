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
/// "the app is gone" and fail open.
/// </summary>
public sealed class PanelServer : IDisposable
{
    // channel.js rotates through the same range in its reconnect backoff, so the app
    // binds the first free port and panels find it without any side channel.
    private static readonly int[] Ports = [9020, 9021, 9022, 9023, 9024];

    private static readonly TimeSpan StateRenewal = TimeSpan.FromSeconds(2);

    private readonly HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<PanelSocket, byte> _sockets = new();
    private readonly BehaviorSubject<bool> _bindFailed = new(false);
    private readonly CompositeDisposable _d = new();

    private volatile string[] _pointerKeys = [];
    private volatile string _session = SessionState.None;
    private volatile bool _isMaster = true;

    public int Port { get; } = -1;

    /// <summary>True when every port in the range was taken and the feature is off.</summary>
    public IObservable<bool> BindFailed => _bindFailed.ObserveOn(TaskPoolScheduler.Default);

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

    /// <summary>Updates the session state broadcast to panels; drives the overlay lock.</summary>
    public void SetSession(string session, bool isMaster)
    {
        if (_session == session && _isMaster == isMaster) return;
        _session = session;
        _isMaster = isMaster;
        Broadcast(StateJson());
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
                break;
            case "stats":
                Log.Debug("[PanelServer] Panel stats: {Stats}", text);
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
