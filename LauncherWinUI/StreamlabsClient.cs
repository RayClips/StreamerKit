using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamerKit;

/// <summary>
/// Talks to Streamlabs Desktop over its remote-control API.
///
/// Protocol in short: a plain WebSocket on port 59650 at /api/websocket carrying JSON-RPC
/// 2.0, one object per frame. Nothing is answered until the API token is presented, so the
/// first thing sent after the socket opens is
/// <c>auth(token)</c> on TcpServerService; every later call names a resource (a Streamlabs
/// service) and a method on it, and comes back matched on id.
///
/// This is deliberately *not* socket.io, despite what the port suggests. 59650 speaks enough
/// HTTP to answer a non-WebSocket request with 400, but /socket.io/ is not a route it has -
/// the upgrade only succeeds at /api/websocket, and no engine.io handshake preamble follows.
/// </summary>
public sealed class StreamlabsClient : INotifyPropertyChanged
{
    private const int LogCapacity = 60_000;

    /// <summary>The service that owns auth. Streamlabs names it for TCP, but the same
    /// resource is what the WebSocket transport authenticates against.</summary>
    private const string AuthResource = "TcpServerService";

    private readonly DispatcherQueue _ui;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly StringBuilder _log = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellation;
    private ServerState _state = ServerState.Stopped;
    private int _nextId;

    /// <summary>
    /// Set once a real reason for failing has already been logged. Rejecting the token means
    /// aborting the socket, which surfaces a second time as a read error — without this the
    /// last thing in the log would be "check that Streamlabs is running", which is exactly
    /// the wrong thing to go and check.
    /// </summary>
    private bool _reported;

    private string _address;
    private int _port;
    private string _token;
    private bool _autoConnect;
    private string _detail = "";

    public StreamlabsClient(Settings.StreamlabsConfig config)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _address = config.Address;
        _port = config.Port;
        _token = config.Token;
        _autoConnect = config.AutoConnect;
    }

    public event Action? ConfigChanged;

    /// <summary>Raised for every pushed event, on a background thread.</summary>
    public event Action<string, JsonNode?>? Event;

    public string Address
    {
        get => _address;
        set => Set(ref _address, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim(), nameof(Address));
    }

    public int Port
    {
        get => _port;
        set => Set(ref _port, Math.Clamp(value, 1, 65535), nameof(Port), nameof(PortValue));
    }

    public double PortValue
    {
        get => _port;
        set => Port = (int)value;
    }

    /// <summary>The API token from Settings -> Remote Control. Used as the password.</summary>
    public string Token
    {
        get => _token;
        set => Set(ref _token, value ?? "", nameof(Token));
    }

    /// <summary>Connect on launch instead of waiting to be asked.</summary>
    public bool AutoConnect
    {
        get => _autoConnect;
        set => Set(ref _autoConnect, value, nameof(AutoConnect));
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (Equals(field, value)) return;
        field = value;
        Notify(names);
        ConfigChanged?.Invoke();
    }

    public void CopyTo(Settings.StreamlabsConfig config)
    {
        config.Address = Address;
        config.Port = Port;
        config.Token = Token;
        config.AutoConnect = AutoConnect;
    }

    public ServerState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State), nameof(StatusText), nameof(StatusBrush), nameof(ActionText),
                   nameof(IsEditable), nameof(IsConnected));
        }
    }

    public bool IsConnected => State == ServerState.Running;
    public bool IsEditable => State is ServerState.Stopped or ServerState.Failed;
    public string ActionText => State is ServerState.Stopped or ServerState.Failed ? "Connect" : "Disconnect";
    public string LogText => _log.ToString();

    public string StatusText => State switch
    {
        ServerState.Starting => "Connecting…",
        ServerState.Running => string.IsNullOrEmpty(_detail) ? "Connected" : $"Connected · {_detail}",
        ServerState.Stopping => "Disconnecting…",
        ServerState.Failed => string.IsNullOrEmpty(_detail) ? "Failed" : $"Failed · {_detail}",
        _ => "Not connected"
    };

    public Brush StatusBrush => State switch
    {
        ServerState.Running => Theme("SystemFillColorSuccessBrush", 0x3D, 0xD6, 0x8C),
        ServerState.Starting or ServerState.Stopping => Theme("SystemFillColorCautionBrush", 0xE3, 0xB3, 0x41),
        ServerState.Failed => Theme("SystemFillColorCriticalBrush", 0xF0, 0x59, 0x6B),
        _ => Theme("TextFillColorDisabledBrush", 0x6E, 0x6E, 0x6E)
    };

    private static Brush Theme(string key, byte r, byte g, byte b)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush)
            return brush;
        return new SolidColorBrush(Color.FromArgb(255, r, g, b));
    }

    public void Toggle()
    {
        if (State is ServerState.Stopped or ServerState.Failed) Connect();
        else Disconnect();
    }

    public void Connect()
    {
        if (State is ServerState.Running or ServerState.Starting) return;

        _detail = "";
        _reported = false;
        ForgetLookups();
        State = ServerState.Starting;
        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => Run(_cancellation.Token));
    }

    public void Disconnect()
    {
        if (State is ServerState.Stopped) return;

        State = ServerState.Stopping;
        _cancellation?.Cancel();
        try { _socket?.Abort(); } catch { }
        _socket = null;
        _detail = "";
        ForgetLookups();
        FailPending("Disconnected.");
        State = ServerState.Stopped;
        Log("Disconnected.");
        FlushLog();
    }

    private async Task Run(CancellationToken token)
    {
        var url = $"ws://{Address}:{Port}/api/websocket";
        try
        {
            _socket = new ClientWebSocket();
            Log($"Connecting to {url}…");
            await _socket.ConnectAsync(new Uri(url), token);
            Log("Socket open, authenticating.");

            // The read loop has to be running before auth is sent — the reply to it is an
            // ordinary response frame, matched on id like everything else.
            var reading = ReadLoop(token);
            await Authenticate(token);
            await reading;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Authentication aborts the socket on purpose, and that lands here as a read
            // error. Saying anything now would only bury the reason already reported.
            if (_reported)
            {
                FlushLog();
                return;
            }

            Log($"Connection failed: {ex.Message}");
            Log("Check that Streamlabs Desktop is running, and that Settings -> Remote Control "
                + "has the API server enabled.");
            _ui.TryEnqueue(() => State = ServerState.Failed);
            FlushLog();
            return;
        }
        finally
        {
            FailPending("Connection closed.");
        }

        _ui.TryEnqueue(() => { if (State != ServerState.Stopped) State = ServerState.Stopped; });
    }

    /// <summary>
    /// Streamlabs answers nothing at all until this succeeds — an unauthenticated request
    /// comes back as "Authorization required", so a wrong token looks exactly like a working
    /// connection until the first real call. It's checked here instead, while connecting.
    /// </summary>
    private async Task Authenticate(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            Fail("No API token. Copy it from Streamlabs: Settings -> Remote Control -> show details.");
            return;
        }

        var result = await Call(AuthResource, "auth", new JsonArray { Token }, 10_000);
        if (!result.Ok)
        {
            Fail(result.Error.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                    || result.Error.Contains("token", StringComparison.OrdinalIgnoreCase)
                 ? "Streamlabs rejected the API token. Copy the current one from Settings -> "
                   + "Remote Control; it changes whenever it is regenerated."
                 : result.Error);
            return;
        }

        Log("Authenticated.");
        _ui.TryEnqueue(() => State = ServerState.Running);
        _ = Describe();
        _ = Subscribe();
        return;

        void Fail(string reason)
        {
            _reported = true;
            Log(reason);
            _ui.TryEnqueue(() =>
            {
                _detail = reason;
                State = ServerState.Failed;
                Notify(nameof(StatusText));
            });
            FlushLog();
            try { _socket?.Abort(); } catch { }
        }
    }

    /// <summary>
    /// Asks to be told when streaming or recording changes.
    ///
    /// Streamlabs has no "subscribe" verb: you *call* the event as though it were a method,
    /// and the reply is a SUBSCRIPTION resource rather than a value. Pushes then arrive with
    /// no id, which is how <see cref="Handle"/> tells them apart from replies.
    ///
    /// A failure here is logged and otherwise ignored. Whether these particular events exist
    /// is a property of the Streamlabs build, and a connection that can still switch scenes
    /// is worth more than one refused over an event nobody asked for.
    /// </summary>
    private async Task Subscribe()
    {
        foreach (var name in new[] { "streamingStatusChange", "recordingStatusChange",
                                     "replayBufferStatusChange" })
        {
            var result = await Call("StreamingService", name);
            if (!result.Ok) Log($"Could not subscribe to {name}: {result.Error}");
        }
    }

    /// <summary>Confirms the connection does something, and puts a fact in the status line.</summary>
    private async Task Describe()
    {
        var scenes = await Call("ScenesService", "getScenes");
        if (!scenes.Ok || scenes.Result is not JsonArray list) return;

        _ui.TryEnqueue(() =>
        {
            _detail = list.Count == 1 ? "1 scene" : $"{list.Count} scenes";
            Notify(nameof(StatusText));
        });
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        var pending = new StringBuilder();

        while (_socket is { State: WebSocketState.Open } && !token.IsCancellationRequested)
        {
            // Deliberately the connection's own token and nothing shorter: cancelling a
            // ReceiveAsync aborts the socket outright rather than just giving up on one read.
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) break;

            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;      // a frame can span several reads

            var text = pending.ToString();
            pending.Clear();

            // Replies arrive newline-terminated, and a busy moment can put more than one
            // object in a single frame.
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try { Handle(line.Trim()); }
                catch (Exception ex) { Log($"Error handling message: {ex.Message}"); }
            }
        }
    }

    private void Handle(string text)
    {
        if (text.Length == 0) return;

        var root = JsonNode.Parse(text)?.AsObject();
        if (root is null) return;

        // A reply carries the id we sent. Anything else is a pushed event.
        var id = root["id"]?.ToString();
        if (!string.IsNullOrEmpty(id) && _pending.TryRemove(id, out var waiter))
        {
            waiter.TrySetResult(root);
            return;
        }

        var payload = root["result"];
        var name = payload?["resourceId"]?.ToString() ?? payload?["_type"]?.ToString() ?? "message";
        Log($"EVENT  {name}  {Compact(payload?["data"] ?? payload)}");
        Event?.Invoke(name, payload?["data"]);
    }

    /// <summary>What a call did. Streamlabs reports failure as a JSON-RPC error object.</summary>
    public sealed record SlabsResult(bool Ok, JsonNode? Result, string Error);

    /// <summary>
    /// Calls <paramref name="method"/> on a Streamlabs service and waits for the reply.
    /// </summary>
    public async Task<SlabsResult> Call(string resource, string method, JsonArray? args = null, int timeoutMs = 8000)
    {
        if (_socket is not { State: WebSocketState.Open })
        {
            Log("Not connected.");
            FlushLog();
            return new SlabsResult(false, null, "Streamlabs is not connected.");
        }

        var id = Interlocked.Increment(ref _nextId).ToString();
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = new JsonObject
            {
                ["resource"] = resource,
                ["args"] = args ?? new JsonArray()
            }
        };

        // The token is a credential — never write it into the log.
        Log($"---> {resource}.{method}{(resource == AuthResource && method == "auth" ? "(token)" : Arguments(args))}");
        await Send(message, CancellationToken.None);

        using var timeout = new CancellationTokenSource(timeoutMs);
        await using var registration = timeout.Token.Register(() => waiter.TrySetCanceled());

        try
        {
            var response = await waiter.Task;

            if (response["error"] is { } error)
            {
                var reason = error["message"]?.ToString() ?? error.ToJsonString();
                Log($"<--- {resource}.{method} FAILED  {reason}");
                FlushLog();
                return new SlabsResult(false, null, reason);
            }

            var payload = response["result"];
            Log($"<--- {resource}.{method} ok\n{Pretty(payload)}");
            FlushLog();
            return new SlabsResult(true, payload, "");
        }
        catch (TaskCanceledException)
        {
            _pending.TryRemove(id, out _);
            Log($"<--- {resource}.{method} timed out");
            FlushLog();
            return new SlabsResult(false, null, $"{resource}.{method} timed out after {timeoutMs} ms.");
        }
    }

    // ---- Name lookups -------------------------------------------------------
    //
    // Everything in Streamlabs is addressed by an opaque resource id, but the user types
    // names. getScenes is the only call that maps a name to an id, and Streamlabs throttles
    // it hard - two calls in about ten seconds and it starts answering
    // "Reached the limit of calls for ScenesService.getScenes". A block that resolved names
    // on every run would therefore work when tested by hand and fail the moment an action
    // fired repeatedly, which is exactly what a mouse-driven one does.
    //
    // So names are resolved once and remembered. Everything after that uses the per-item
    // calls, which are not throttled.

    private readonly ConcurrentDictionary<string, string> _sceneIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _itemResources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A scene item: what to call methods on, and what it looks like right now.</summary>
    public sealed record SceneItemRef(string Resource, JsonObject Model)
    {
        public bool Visible => Model["visible"]?.GetValue<bool>() ?? false;

        public (double X, double Y) Position
        {
            get
            {
                var position = Model["transform"]?["position"];
                return (position?["x"]?.GetValue<double>() ?? 0,
                        position?["y"]?.GetValue<double>() ?? 0);
            }
        }
    }

    /// <summary>The id of the scene with this name, from cache where possible.</summary>
    public async Task<string> SceneId(string name)
    {
        if (_sceneIds.TryGetValue(name, out var known)) return known;

        await RefreshScenes();
        if (_sceneIds.TryGetValue(name, out var found)) return found;

        var all = string.Join(", ", _sceneIds.Keys.Select(k => $"\"{k}\""));
        throw new InvalidOperationException(
            $"Streamlabs has no scene called \"{name}\"" + (all.Length > 0 ? $" — it has {all}" : ""));
    }

    private async Task RefreshScenes()
    {
        var result = await Call("ScenesService", "getScenes");
        if (!result.Ok) throw new InvalidOperationException(result.Error);

        _sceneIds.Clear();
        if (result.Result is not JsonArray scenes) return;

        foreach (var scene in scenes)
            if (scene?["name"]?.ToString() is { Length: > 0 } name &&
                scene["id"]?.ToString() is { Length: > 0 } id)
                _sceneIds[name] = id;
    }

    /// <summary>
    /// Finds a source inside a scene and reads its current state.
    ///
    /// A remembered resource id goes stale if the source is deleted and re-added, so a failed
    /// read is treated as "look it up again" rather than as an error — otherwise an action
    /// would stay broken until the app was restarted.
    /// </summary>
    public async Task<SceneItemRef> Item(string sceneName, string sourceName)
    {
        var key = $"{sceneName} {sourceName}";

        if (_itemResources.TryGetValue(key, out var cached))
        {
            var model = await Call(cached, "getModel");
            if (model.Ok && model.Result is JsonObject fromCache) return new SceneItemRef(cached, fromCache);

            _itemResources.TryRemove(key, out _);
        }

        var resource = await FindItem(sceneName, sourceName);
        _itemResources[key] = resource;

        var fresh = await Call(resource, "getModel");
        if (!fresh.Ok || fresh.Result is not JsonObject shape)
            throw new InvalidOperationException(fresh.Error.Length > 0
                ? fresh.Error
                : $"could not read \"{sourceName}\" in \"{sceneName}\"");

        return new SceneItemRef(resource, shape);
    }

    private async Task<string> FindItem(string sceneName, string sourceName)
    {
        var sceneId = await SceneId(sceneName);

        // The scene's own resource id is its scene id wrapped this way — the same shape
        // Streamlabs hands back from getScene.
        var node = await Call($"Scene[\"{sceneId}\"]", "getNodeByName", new JsonArray { sourceName });
        if (!node.Ok) throw new InvalidOperationException(node.Error);

        var resource = node.Result?["resourceId"]?.ToString();
        if (string.IsNullOrEmpty(resource))
            throw new InvalidOperationException($"scene \"{sceneName}\" has no source called \"{sourceName}\"");

        return resource;
    }

    /// <summary>Nothing remembered survives a reconnect — Streamlabs may be a different session.</summary>
    private void ForgetLookups()
    {
        _sceneIds.Clear();
        _itemResources.Clear();
    }

    private static string Arguments(JsonArray? args)
        => args is null || args.Count == 0 ? "()" : $"({Compact(args).Trim('[', ']')})";

    private async Task Send(JsonObject message, CancellationToken token)
    {
        if (_socket is not { State: WebSocketState.Open }) return;

        // A WebSocket allows one outstanding SendAsync; overlapping writes kill the socket.
        await _sendGate.WaitAsync(token);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Nothing is going to answer these now, and a caller left awaiting never returns.</summary>
    private void FailPending(string reason)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var waiter))
                waiter.TrySetException(new InvalidOperationException(reason));
    }

    private static string Pretty(JsonNode? node) =>
        node is null ? "(no data)" : node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static string Compact(JsonNode? node)
    {
        if (node is null) return "";
        var text = node.ToJsonString();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    public void ClearLog()
    {
        _log.Clear();
        Notify(nameof(LogText));
    }

    private void Log(string line) => _incoming.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");

    public void FlushLog()
    {
        if (_incoming.IsEmpty) return;

        // Connecting and every call flush from a background thread. Draining there would
        // move the lines into _log and raise PropertyChanged off the UI thread, where the
        // binding ignores it — and the next pump tick, finding the queue already empty,
        // returns before notifying. The log would fill up and never appear.
        if (!_ui.HasThreadAccess)
        {
            _ui.TryEnqueue(FlushLog);
            return;
        }

        while (_incoming.TryDequeue(out var line)) _log.Append(line).Append('\n');
        if (_log.Length > LogCapacity) _log.Remove(0, _log.Length - LogCapacity);
        Notify(nameof(LogText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
