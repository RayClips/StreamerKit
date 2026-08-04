using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamerKit;

/// <summary>What a server card needs, so the same template drives both kinds.</summary>
public interface IServerCard
{
    string? Url { get; }
    void Toggle();
}

/// <summary>One row in the "Connected clients" list.</summary>
public sealed record ConnectedClient(string Remote, string Since);

/// <summary>
/// A server StreamerKit runs itself, in-process — as opposed to <see cref="ServerProcess"/>,
/// which supervises somebody else's executable. Its settings are editable while stopped
/// and are saved to settings.json.
/// </summary>
public abstract class HostedServer : INotifyPropertyChanged, IServerCard
{
    private const int LogCapacity = 20_000;

    private readonly DispatcherQueue _ui;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly StringBuilder _log = new();

    private ServerState _state = ServerState.Stopped;
    private string _address = "127.0.0.1";
    private int _port;
    private string _endpoint = "/";
    private bool _autoStart;
    private bool _authentication;
    private string _token = "";

    protected CancellationTokenSource? Cancellation;

    private string _name;

    protected HostedServer(string name, string subtitle, Settings.HostedConfig config)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _name = string.IsNullOrWhiteSpace(config.Name) ? name : config.Name;
        Subtitle = subtitle;
        _address = config.Address;
        _port = config.Port;
        _endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? "/" : config.Endpoint;
        _autoStart = config.AutoStart;
        _authentication = config.Authentication;
        _token = config.Token;
    }

    /// <summary>Editable for user-added servers; the three built-ins keep their default.</summary>
    public string Name
    {
        get => _name;
        set => SetConfig(ref _name, string.IsNullOrWhiteSpace(value) ? "WebSocket Server" : value.Trim(),
                         nameof(Name));
    }

    public string Subtitle { get; }

    /// <summary>Raised whenever a saved setting changes, so the host can persist it.</summary>
    public event Action? ConfigChanged;

    // ---- editable settings ----

    public string Address
    {
        get => _address;
        set => SetConfig(ref _address, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim(),
                         nameof(Address), nameof(Endpoint), nameof(Url));
    }

    public int Port
    {
        get => _port;
        set => SetConfig(ref _port, Math.Clamp(value, 1, 65535), nameof(Port), nameof(PortValue),
                         nameof(StatusText), nameof(Endpoint), nameof(Url));
    }

    /// <summary>NumberBox binds to a double, so it gets its own view of the port.</summary>
    public double PortValue
    {
        get => _port;
        set => Port = (int)value;
    }

    public string EndpointPath
    {
        get => _endpoint;
        set
        {
            var cleaned = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
            if (!cleaned.StartsWith('/')) cleaned = "/" + cleaned;
            if (!cleaned.EndsWith('/')) cleaned += "/";
            SetConfig(ref _endpoint, cleaned, nameof(EndpointPath), nameof(Endpoint), nameof(Url));
        }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetConfig(ref _autoStart, value, nameof(AutoStart));
    }

    public bool Authentication
    {
        get => _authentication;
        set
        {
            // Turning auth on with no token would be a lock with no key: the only clients
            // that could connect are ones passing an empty token. Mint one instead.
            if (value && string.IsNullOrEmpty(_token))
                Token = Guid.NewGuid().ToString("N")[..16];

            SetConfig(ref _authentication, value, nameof(Authentication), nameof(TokenVisibility));
        }
    }

    public string Token
    {
        get => _token;
        set => SetConfig(ref _token, value ?? "", nameof(Token));
    }

    private void SetConfig<T>(ref T field, T value, params string[] names)
    {
        if (Equals(field, value)) return;
        field = value;
        Notify(names);
        ConfigChanged?.Invoke();
    }

    public void CopyTo(Settings.HostedConfig config)
    {
        config.Name = Name;
        config.Address = Address;
        config.Port = Port;
        config.Endpoint = EndpointPath;
        config.AutoStart = AutoStart;
        config.Authentication = Authentication;
        config.Token = Token;
    }

    // ---- what each server type supports ----

    public virtual bool SupportsEndpoint => true;
    public virtual bool SupportsAuthentication => false;
    public virtual bool SupportsClients => false;
    public virtual bool SupportsTestMessage => false;

    public Visibility TestMessageVisibility =>
        SupportsTestMessage && State == ServerState.Running ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EndpointVisibility => SupportsEndpoint ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AuthVisibility => SupportsAuthentication ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClientsVisibility => SupportsClients ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TokenVisibility =>
        SupportsAuthentication && Authentication ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Settings can only be edited while the server is down.</summary>
    public bool IsEditable => State is ServerState.Stopped or ServerState.Failed;

    public ObservableCollection<ConnectedClient> Clients { get; } = new();
    public Visibility NoClientsVisibility =>
        Clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- runtime state ----

    public abstract string Scheme { get; }
    public virtual string Endpoint => $"{Scheme}://{Address}:{Port}{EndpointPath}";

    public string? Url => State == ServerState.Running ? Endpoint : null;
    public bool HasUrl => Url is not null;
    public Visibility UrlVisibility => HasUrl ? Visibility.Visible : Visibility.Collapsed;
    public string LogText => _log.ToString();

    public ServerState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State), nameof(StatusText), nameof(StatusBrush), nameof(ActionText),
                   nameof(Url), nameof(HasUrl), nameof(UrlVisibility), nameof(IsEditable),
                   nameof(TestMessageVisibility));
        }
    }

    public string StatusText => State switch
    {
        ServerState.Starting => "Starting",
        ServerState.Running => $"Listening on {Port}",
        ServerState.Stopping => "Stopping",
        ServerState.Failed => "Failed",
        _ => "Stopped"
    };

    public Brush StatusBrush => State switch
    {
        ServerState.Running => Theme("SystemFillColorSuccessBrush", 0x3D, 0xD6, 0x8C),
        ServerState.Starting or ServerState.Stopping => Theme("SystemFillColorCautionBrush", 0xE3, 0xB3, 0x41),
        ServerState.Failed => Theme("SystemFillColorCriticalBrush", 0xF0, 0x59, 0x6B),
        _ => Theme("TextFillColorDisabledBrush", 0x6E, 0x6E, 0x6E)
    };

    public string ActionText => State is ServerState.Stopped or ServerState.Failed ? "Start" : "Stop";

    private static Brush Theme(string key, byte r, byte g, byte b)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush)
            return brush;
        return new SolidColorBrush(Color.FromArgb(255, r, g, b));
    }

    public void Toggle()
    {
        if (State is ServerState.Stopped or ServerState.Failed) Start();
        else Stop();
    }

    public void Start()
    {
        if (State is ServerState.Running or ServerState.Starting) return;

        _log.Clear();
        Notify(nameof(LogText));
        State = ServerState.Starting;

        Cancellation = new CancellationTokenSource();
        try
        {
            StartCore(Cancellation.Token);
            State = ServerState.Running;
            Log($"Listening at {Endpoint}");
            if (SupportsAuthentication && Authentication)
                Log("Authentication is on: clients must pass ?token=<your token>");
        }
        catch (Exception ex)
        {
            Log(ex is HttpListenerException or SocketException
                ? $"Could not bind {Address}:{Port} — {ex.Message}"
                : ex.Message);
            if (ex is HttpListenerException && Address is not ("127.0.0.1" or "localhost"))
                Log("Binding an address other than 127.0.0.1 usually needs an admin URL reservation.");
            State = ServerState.Failed;
            SafeStopCore();
        }
        FlushLog();
    }

    public void Stop()
    {
        if (State is ServerState.Stopped) return;

        State = ServerState.Stopping;
        Cancellation?.Cancel();
        SafeStopCore();
        ClearClients();
        Log("Stopped.");
        State = ServerState.Stopped;
        FlushLog();
    }

    private void SafeStopCore()
    {
        try { StopCore(); } catch { /* already torn down */ }
    }

    protected abstract void StartCore(CancellationToken token);
    protected abstract void StopCore();

    // ---- client list, always touched on the UI thread ----

    protected void AddClient(string remote) => _ui.TryEnqueue(() =>
    {
        Clients.Add(new ConnectedClient(remote, DateTime.Now.ToString("HH:mm:ss")));
        Notify(nameof(NoClientsVisibility));
    });

    protected void RemoveClient(string remote) => _ui.TryEnqueue(() =>
    {
        var found = Clients.FirstOrDefault(c => c.Remote == remote);
        if (found is not null) Clients.Remove(found);
        Notify(nameof(NoClientsVisibility));
    });

    private void ClearClients() => _ui.TryEnqueue(() =>
    {
        Clients.Clear();
        Notify(nameof(NoClientsVisibility));
    });

    /// <summary>Safe to call from any thread; the UI drains this on its own timer.</summary>
    protected void Log(string line) => _incoming.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");

    /// <summary>Lets a feed like Twitch chat write into this server's log.</summary>
    public void LogExternal(string line) => Log(line);

    public void FlushLog()
    {
        if (_incoming.IsEmpty) return;

        while (_incoming.TryDequeue(out var line)) _log.Append(line).Append('\n');
        if (_log.Length > LogCapacity) _log.Remove(0, _log.Length - LogCapacity);
        Notify(nameof(LogText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Serves the "www" folder next to the exe over HTTP.</summary>
public sealed class HttpHostedServer : HostedServer
{
    private HttpListener? _listener;

    public HttpHostedServer(Settings.HostedConfig config)
        : base("HTTP Server", "Serves the www folder next to the app", config) { }

    public override string Scheme => "http";

    private static string Root => Path.Combine(AppContext.BaseDirectory, "www");

    protected override void StartCore(CancellationToken token)
    {
        EnsureRoot();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{Address}:{Port}{EndpointPath}");
        _listener.Start();
        _ = Task.Run(() => AcceptLoop(token), token);
    }

    protected override void StopCore()
    {
        _listener?.Close();
        _listener = null;
    }

    private static void EnsureRoot()
    {
        if (Directory.Exists(Root)) return;
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "index.html"),
            "<!doctype html><meta charset=\"utf-8\"><title>StreamerKit</title>"
          + "<body style=\"font:16px system-ui;background:#202020;color:#fff;padding:40px\">"
          + "<h1>StreamerKit HTTP server</h1><p>Drop files in the <code>www</code> folder "
          + "next to StreamerKit.exe and they will be served from here.</p>");
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { break; }   // listener closed
            _ = Task.Run(() => Serve(context), token);
        }
    }

    private void Serve(HttpListenerContext context)
    {
        // Read what we need up front: the context is not safe to touch once the response
        // is closed, and reading it in the finally block loses the log line entirely.
        var method = context.Request.HttpMethod;
        var path = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
        if (path.Length == 0) path = "index.html";

        var full = Path.GetFullPath(Path.Combine(Root, path));
        var status = 200;
        try
        {
            // Never serve outside the www folder, whatever the request asks for.
            if (!full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                status = 404;
                var missing = Encoding.UTF8.GetBytes("404 - not found");
                context.Response.StatusCode = status;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.OutputStream.Write(missing);
            }
            else
            {
                var bytes = File.ReadAllBytes(full);
                context.Response.ContentType = ContentType(full);
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes);
            }
        }
        catch (Exception ex)
        {
            status = 500;
            Log($"error serving /{path}: {ex.Message}");
        }
        finally
        {
            try { context.Response.Close(); } catch { }
            Log($"{method} /{path} -> {status}");
        }
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "application/octet-stream"
    };
}

/// <summary>WebSocket server that relays whatever one client sends to all the others.</summary>
public sealed class WebSocketHostedServer : HostedServer
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private HttpListener? _listener;

    /// <summary>
    /// A WebSocket allows only one outstanding SendAsync at a time. Chat arrives faster
    /// than a send completes, so every write goes through here — without it the second
    /// overlapping send throws and takes the client's connection down with it.
    /// </summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public WebSocketHostedServer(Settings.HostedConfig config)
        : base("WebSocket Server", "Broadcasts messages to every connected overlay", config) { }

    /// <summary>
    /// Supplies the action list for GetActions, and runs one for DoAction. Set by the window;
    /// left null the server answers as though there are no actions, which is what a custom
    /// WebSocket server should do.
    /// </summary>
    /// <remarks>Must hand back a fresh array each call — a JsonNode can only have one parent.</remarks>
    public Func<JsonArray>? Actions { get; set; }

    /// <summary>
    /// Runs an action for a client. Returns null on success, or why it was refused.
    /// Async because this is called from the receive loop, and the engine lives on the UI thread.
    /// </summary>
    public Func<string, Dictionary<string, string>, Task<string?>>? RunAction { get; set; }

    public override string Scheme => "ws";
    public override bool SupportsAuthentication => true;
    public override bool SupportsClients => true;
    public override bool SupportsTestMessage => true;

    protected override void StartCore(CancellationToken token)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{Address}:{Port}{EndpointPath}");
        _listener.Start();
        _ = Task.Run(() => AcceptLoop(token), token);
    }

    protected override void StopCore()
    {
        foreach (var client in _clients.Values)
        {
            try { client.Abort(); client.Dispose(); } catch { }
        }
        _clients.Clear();
        _listener?.Close();
        _listener = null;
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { break; }

            if (!context.Request.IsWebSocketRequest)
            {
                Reject(context, 426, "This is a WebSocket endpoint.");
                continue;
            }

            if (Authentication && context.Request.QueryString["token"] != Token)
            {
                Log($"rejected {context.Request.RemoteEndPoint}: bad or missing token");
                Reject(context, 401, "Unauthorized.");
                continue;
            }

            _ = Task.Run(() => Handle(context, token), token);
        }
    }

    private static void Reject(HttpListenerContext context, int status, string message)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(message));
            context.Response.Close();
        }
        catch { }
    }

    private async Task Handle(HttpListenerContext context, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var remote = context.Request.RemoteEndPoint?.ToString() ?? "unknown";

        WebSocket socket;
        try { socket = (await context.AcceptWebSocketAsync(null)).WebSocket; }
        catch (Exception ex) { Log($"handshake failed: {ex.Message}"); return; }

        _clients[id] = socket;
        AddClient(remote);
        Log($"client connected: {remote} ({_clients.Count} online)");

        var buffer = new byte[8 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Log($"recv from {remote}: {Trim(text)}");

                // Streamer.bot clients open with a GetInfo handshake and will not report
                // themselves connected until it is answered. Anything that isn't one of
                // those requests is treated as a plain message and relayed as before.
                if (!await TryAnswerRequest(socket, text, token))
                    await Broadcast(text, id, token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        catch (Exception ex) { Log($"client error: {ex.Message}"); }
        finally
        {
            _clients.TryRemove(id, out _);
            RemoveClient(remote);
            try { socket.Dispose(); } catch { }
            Log($"client disconnected: {remote} ({_clients.Count} online)");
        }
    }

    private async Task Broadcast(string text, Guid from, CancellationToken token)
    {
        foreach (var (id, socket) in _clients)
        {
            if (id == from || socket.State != WebSocketState.Open) continue;
            try { await Send(socket, text, token); }
            catch { /* dropped client; its own loop will clean up */ }
        }
    }

    /// <summary>
    /// Answers the handful of Streamer.bot API requests that overlay widgets send while
    /// connecting, so clients built on @streamerbot/client will talk to StreamerKit.
    /// Returns false for anything that isn't a recognised request.
    /// </summary>
    private async Task<bool> TryAnswerRequest(WebSocket socket, string text, CancellationToken token)
    {
        string request, id;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("request", out var requestNode)) return false;

            request = requestNode.GetString() ?? "";
            id = root.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return false;   // not JSON: a plain relay message
        }

        var reply = new JsonObject { ["id"] = id, ["status"] = "ok" };

        switch (request)
        {
            case "GetInfo":
                reply["info"] = new JsonObject
                {
                    ["instanceId"] = InstanceId,
                    ["name"] = "StreamerKit",
                    ["version"] = "0.2.5",
                    ["os"] = "windows"
                };
                break;

            case "Subscribe":
            case "UnSubscribe":
                // Echo back whatever was asked for; nothing is filtered yet.
                reply["events"] = root.TryGetProperty("events", out var events)
                    ? JsonNode.Parse(events.GetRawText())
                    : new JsonObject();
                break;

            case "GetEvents":
                reply["events"] = new JsonObject
                {
                    ["Twitch"] = new JsonArray("ChatMessage"),
                    ["Kick"] = new JsonArray("ChatMessage"),
                    ["Discord"] = new JsonArray("ChatMessage")
                };
                break;

            case "GetActions":
                var actions = Actions?.Invoke() ?? new JsonArray();
                reply["count"] = actions.Count;
                reply["actions"] = actions;
                break;

            case "DoAction":
                // Streamer.bot takes either {"action":{"id":…}} or {"action":{"name":…}},
                // with optional args. Anything the host refuses comes back as an error so
                // the caller sees why instead of watching nothing happen.
                var target = root.TryGetProperty("action", out var actionNode) && actionNode.ValueKind == JsonValueKind.Object
                    ? actionNode.TryGetProperty("id", out var actionId) && actionId.ValueKind == JsonValueKind.String
                        ? actionId.GetString() ?? ""
                        : actionNode.TryGetProperty("name", out var actionName) ? actionName.GetString() ?? "" : ""
                    : "";

                if (target.Length == 0)
                {
                    reply["status"] = "error";
                    reply["error"] = "DoAction needs action.id or action.name";
                    break;
                }

                var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object)
                {
                    foreach (var argument in args.EnumerateObject())
                    {
                        arguments[argument.Name] = argument.Value.ValueKind == JsonValueKind.String
                            ? argument.Value.GetString() ?? ""
                            : argument.Value.ToString();
                    }
                }

                if (RunAction is null)
                {
                    reply["status"] = "error";
                    reply["error"] = "This server does not run actions.";
                }
                else if (await RunAction(target, arguments) is { } refusal)
                {
                    reply["status"] = "error";
                    reply["error"] = refusal;
                }
                break;

            case "GetActiveViewers":
                reply["viewers"] = new JsonArray();
                reply["count"] = 0;
                break;

            case "GetUserPronouns":
                // Asked once per chatter before their message is rendered. The reply must be
                // an object under "pronoun" (singular) — widgets read response.pronoun.userFound
                // straight off it, so null or a missing field throws inside their handler and
                // the message never gets appended.
                reply["pronoun"] = new JsonObject
                {
                    ["userFound"] = false,
                    ["userLogin"] = root.TryGetProperty("userLogin", out var login) ? login.GetString() ?? "" : "",
                    ["pronounId"] = "",
                    ["pronounSubject"] = "",
                    ["pronounObject"] = ""
                };
                break;

            case "GetBroadcaster":
                // Answer truthfully: no platform accounts are connected. Returning an
                // error here makes some widgets abandon the handshake.
                reply["platforms"] = new JsonObject();
                reply["connected"] = new JsonArray();
                break;

            default:
                reply["status"] = "error";
                reply["error"] = $"Unknown request '{request}'";
                break;
        }

        await Send(socket, reply.ToJsonString(), token);
        Log($"{request} -> {reply["status"]}");
        return true;
    }

    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    private async Task Send(WebSocket socket, string text, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open) return;

        await _sendGate.WaitAsync(token);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
                WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Pushes a Streamer.bot-shaped event to every connected client.</summary>
    public async Task BroadcastEvent(string source, string type, JsonObject data)
    {
        var envelope = new JsonObject
        {
            ["timeStamp"] = DateTime.Now.ToString("O"),
            ["event"] = new JsonObject { ["source"] = source, ["type"] = type },
            ["data"] = data
        };

        var payload = envelope.ToJsonString();
        foreach (var socket in _clients.Values)
        {
            try { await Send(socket, payload, CancellationToken.None); }
            catch { }
        }
    }

    /// <summary>Pushes one fake Twitch chat message, shaped the way Streamer.bot emits them.</summary>
    public async Task SendTestMessage()
    {
        var envelope = new JsonObject
        {
            ["timeStamp"] = DateTime.Now.ToString("O"),
            ["event"] = new JsonObject { ["source"] = "Twitch", ["type"] = "ChatMessage" },
            // Streamer.bot 0.3.x ChatMessage shape: text and user sit at the top of data,
            // not nested under a "message" object as they did in 0.2.x.
            ["data"] = new JsonObject
            {
                ["messageId"] = Guid.NewGuid().ToString(),
                ["text"] = "Test message from StreamerKit",
                ["isReply"] = false,
                ["isFromSharedChatGuest"] = false,
                ["meta"] = new JsonObject
                {
                    ["firstMessage"] = false,
                    ["isMe"] = false,
                    ["subscriber"] = false
                },
                ["user"] = new JsonObject
                {
                    ["id"] = "1",
                    ["login"] = "streamerkit",
                    ["name"] = "StreamerKit",
                    ["color"] = "#36D767",
                    ["role"] = 1,
                    ["badges"] = new JsonArray(),
                    ["subscribed"] = false
                },
                ["parts"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "Test message from StreamerKit"
                    })
            }
        };

        var payload = envelope.ToJsonString();
        foreach (var socket in _clients.Values)
        {
            try { await Send(socket, payload, CancellationToken.None); }
            catch { }
        }
        Log($"sent a test Twitch ChatMessage to {_clients.Count} client(s)");
        FlushLog();
    }

    private static string Trim(string text) => text.Length <= 120 ? text : text[..120] + "…";
}

/// <summary>Listens for UDP datagrams — handy for hardware and scripts that fire-and-forget.</summary>
public sealed class UdpHostedServer : HostedServer
{
    private UdpClient? _client;

    public UdpHostedServer(Settings.HostedConfig config)
        : base("UDP Server", "Logs datagrams sent to this port", config) { }

    public override string Scheme => "udp";
    public override bool SupportsEndpoint => false;
    public override string Endpoint => $"udp://{Address}:{Port}";

    protected override void StartCore(CancellationToken token)
    {
        var ip = IPAddress.TryParse(Address, out var parsed) ? parsed : IPAddress.Loopback;
        _client = new UdpClient(new IPEndPoint(ip, Port));
        _ = Task.Run(() => ReceiveLoop(token), token);
    }

    protected override void StopCore()
    {
        _client?.Close();
        _client = null;
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _client is not null)
        {
            UdpReceiveResult result;
            try { result = await _client.ReceiveAsync(token); }
            catch { break; }   // closed or cancelled

            var text = Encoding.UTF8.GetString(result.Buffer);
            Log($"{result.RemoteEndPoint} sent {result.Buffer.Length} bytes: {Trim(text)}");
        }
    }

    private static string Trim(string text) => text.Length <= 120 ? text : text[..120] + "…";
}
