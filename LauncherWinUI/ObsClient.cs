using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamerKit;

/// <summary>
/// Talks to OBS Studio over obs-websocket 5.x (bundled with OBS 28+).
///
/// Protocol in short: OBS sends Hello (op 0), we answer Identify (op 1), OBS confirms with
/// Identified (op 2). After that, requests are op 6 and come back as op 7 matched on
/// requestId, and events arrive unsolicited as op 5.
/// </summary>
public sealed class ObsClient : INotifyPropertyChanged
{
    private const int LogCapacity = 60_000;
    private const int RpcVersion = 1;

    /// <summary>EventSubscription.All — everything except the high-volume opt-ins
    /// (volume meters, transform changes), which would flood the log.</summary>
    private const int AllEvents = 2047;

    private readonly DispatcherQueue _ui;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly StringBuilder _log = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellation;
    private ServerState _state = ServerState.Stopped;

    private string _address;
    private int _port;
    private string _password;
    private bool _autoConnect;
    private string _obsVersion = "";

    public ObsClient(Settings.ObsConfig config)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _address = config.Address;
        _port = config.Port;
        _password = config.Password;
        _autoConnect = config.AutoConnect;
    }

    public event Action? ConfigChanged;

    /// <summary>Raised for every op 5 event, on a background thread: event type and its data.</summary>
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

    public string Password
    {
        get => _password;
        set => Set(ref _password, value ?? "", nameof(Password));
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

    public void CopyTo(Settings.ObsConfig config)
    {
        config.Address = Address;
        config.Port = Port;
        config.Password = Password;
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
        ServerState.Running => string.IsNullOrEmpty(_obsVersion) ? "Connected" : $"Connected · OBS {_obsVersion}",
        ServerState.Stopping => "Disconnecting…",
        ServerState.Failed => "Failed",
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
        _obsVersion = "";
        State = ServerState.Stopped;
        Log("Disconnected.");
        FlushLog();
    }

    private async Task Run(CancellationToken token)
    {
        var url = $"ws://{Address}:{Port}";
        try
        {
            _socket = new ClientWebSocket();
            Log($"Connecting to {url}…");
            await _socket.ConnectAsync(new Uri(url), token);
            Log("Socket open, waiting for Hello.");

            await ReadLoop(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"Connection failed: {ex.Message}");
            Log("Check that OBS is running and Tools -> WebSocket Server Settings is enabled.");
            _ui.TryEnqueue(() => State = ServerState.Failed);
            FlushLog();
            return;
        }

        _ui.TryEnqueue(() => { if (State != ServerState.Stopped) State = ServerState.Stopped; });
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var pending = new StringBuilder();

        while (_socket is { State: WebSocketState.Open } && !token.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) break;

            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;      // a frame can span several reads

            var text = pending.ToString();
            pending.Clear();

            try { await Handle(text, token); }
            catch (Exception ex) { Log($"Error handling message: {ex.Message}"); }
        }
    }

    private async Task Handle(string text, CancellationToken token)
    {
        var root = JsonNode.Parse(text)?.AsObject();
        if (root is null) return;

        var op = root["op"]?.GetValue<int>() ?? -1;
        var d = root["d"]?.AsObject();

        switch (op)
        {
            case 0:   // Hello
                await Identify(d, token);
                break;

            case 2:   // Identified
                _obsVersion = d?["negotiatedRpcVersion"]?.ToString() ?? "";
                Log("Identified. Connection is live.");
                _ui.TryEnqueue(() => State = ServerState.Running);
                _ = FetchVersion();
                break;

            case 5:   // Event
                var eventType = d?["eventType"]?.ToString() ?? "";
                Log($"EVENT  {eventType}  {Compact(d?["eventData"])}");
                Event?.Invoke(eventType, d?["eventData"]);
                break;

            case 7:   // RequestResponse
                var id = d?["requestId"]?.ToString() ?? "";
                if (_pending.TryRemove(id, out var waiter) && d is not null) waiter.TrySetResult(d);
                break;

            default:
                Log($"op {op}: {Compact(root)}");
                break;
        }
    }

    private async Task Identify(JsonObject? hello, CancellationToken token)
    {
        var identify = new JsonObject
        {
            ["rpcVersion"] = RpcVersion,
            ["eventSubscriptions"] = AllEvents
        };

        var auth = hello?["authentication"]?.AsObject();
        if (auth is not null)
        {
            var challenge = auth["challenge"]?.ToString() ?? "";
            var salt = auth["salt"]?.ToString() ?? "";
            identify["authentication"] = BuildAuth(Password, salt, challenge);
            Log("Authentication required; answering the challenge.");
        }
        else
        {
            Log("No authentication required.");
        }

        await Send(new JsonObject { ["op"] = 1, ["d"] = identify }, token);
    }

    /// <summary>secret = base64(sha256(password + salt)); auth = base64(sha256(secret + challenge)).</summary>
    private static string BuildAuth(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>Sends a request and waits for the matching response.</summary>
    /// <summary>
    /// What a request actually did. <see cref="Request"/> can't say: plenty of OBS requests
    /// succeed with no responseData at all, so a null result there means nothing on its own.
    /// Action steps need the difference, so they call <see cref="Call"/>.
    /// </summary>
    public sealed record ObsResult(bool Ok, JsonObject? Data, string Error)
    {
        public JsonNode? this[string key] => Data?[key];
    }

    /// <summary>Fire-and-read, for the debug page. Returns just the response data, if any.</summary>
    public async Task<JsonObject?> Request(string requestType, JsonObject? requestData = null, int timeoutMs = 8000)
        => (await Call(requestType, requestData, timeoutMs)).Data;

    public async Task<ObsResult> Call(string requestType, JsonObject? requestData = null, int timeoutMs = 8000)
    {
        if (_socket is not { State: WebSocketState.Open })
        {
            Log("Not connected.");
            FlushLog();
            return new ObsResult(false, null, "OBS is not connected.");
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        var message = new JsonObject
        {
            ["op"] = 6,
            ["d"] = new JsonObject
            {
                ["requestType"] = requestType,
                ["requestId"] = id,
                ["requestData"] = requestData ?? new JsonObject()
            }
        };

        Log($"---> {requestType}");
        await Send(message, CancellationToken.None);

        using var timeout = new CancellationTokenSource(timeoutMs);
        timeout.Token.Register(() => waiter.TrySetCanceled());

        try
        {
            var response = await waiter.Task;
            var status = response["requestStatus"]?.AsObject();
            var ok = status?["result"]?.GetValue<bool>() ?? false;
            var data = response["responseData"]?.AsObject();

            Log(ok
                ? $"<--- {requestType} ok\n{Pretty(data)}"
                : $"<--- {requestType} FAILED  code {status?["code"]}  {status?["comment"]}");

            FlushLog();

            var error = ok ? "" : $"{requestType} failed — {status?["comment"] ?? status?["code"]}";
            return new ObsResult(ok, data, error);
        }
        catch (TaskCanceledException)
        {
            _pending.TryRemove(id, out _);
            Log($"<--- {requestType} timed out");
            FlushLog();
            return new ObsResult(false, null, $"{requestType} timed out after {timeoutMs} ms.");
        }
    }

    private async Task Send(JsonObject message, CancellationToken token)
    {
        if (_socket is not { State: WebSocketState.Open }) return;

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

    private async Task FetchVersion()
    {
        var data = await Request("GetVersion");
        if (data?["obsVersion"] is { } version)
        {
            _obsVersion = version.ToString();
            _ui.TryEnqueue(() => Notify(nameof(StatusText)));
        }
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

        // Request and Disconnect flush from a background thread. Draining there would move
        // the lines into _log and raise PropertyChanged off the UI thread, where the binding
        // ignores it — and the next pump tick, finding the queue already empty, returns
        // before notifying. The log would fill up and never appear.
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
