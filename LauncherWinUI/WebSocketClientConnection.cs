using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamerKit;

/// <summary>
/// An outbound WebSocket connection: StreamerKit dials somebody else's server and stays
/// connected, retrying on its own when the far end goes away.
/// </summary>
public sealed class WebSocketClientConnection : INotifyPropertyChanged
{
    private const int LogCapacity = 20_000;

    private readonly DispatcherQueue _ui;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly StringBuilder _log = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellation;
    private ServerState _state = ServerState.Stopped;

    private string _name;
    private string _endpoint;
    private int _retryInterval;
    private bool _tls;
    private bool _autoStart;
    private int _received;

    public WebSocketClientConnection(Settings.ClientConfig config)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _name = config.Name;
        _endpoint = config.Endpoint;
        _retryInterval = Math.Max(1, config.RetryInterval);
        _tls = config.Tls;
        _autoStart = config.AutoStart;
    }

    public event Action? ConfigChanged;

    // ---- editable settings ----

    public string Name
    {
        get => _name;
        set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? "WebSocket Client" : value.Trim(), nameof(Name));
    }

    public string Endpoint
    {
        get => _endpoint;
        set => Set(ref _endpoint, (value ?? "").Trim(), nameof(Endpoint), nameof(EffectiveUrl));
    }

    /// <summary>Seconds to wait before dialling again after a drop.</summary>
    public int RetryInterval
    {
        get => _retryInterval;
        set => Set(ref _retryInterval, Math.Clamp(value, 1, 3600), nameof(RetryInterval), nameof(RetryValue));
    }

    /// <summary>NumberBox binds to a double.</summary>
    public double RetryValue
    {
        get => _retryInterval;
        set => RetryInterval = (int)value;
    }

    /// <summary>Force wss://. Off leaves the endpoint's own scheme alone.</summary>
    public bool Tls
    {
        get => _tls;
        set => Set(ref _tls, value, nameof(Tls), nameof(EffectiveUrl));
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => Set(ref _autoStart, value, nameof(AutoStart));
    }

    /// <summary>What actually gets dialled once the TLS switch is applied.</summary>
    public string EffectiveUrl
    {
        get
        {
            var url = Endpoint;
            if (!Tls) return url;

            if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) return "wss://" + url[5..];
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "wss://" + url[7..];
            if (!url.Contains("://")) return "wss://" + url;
            return url;
        }
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (Equals(field, value)) return;
        field = value;
        Notify(names);
        ConfigChanged?.Invoke();
    }

    public void CopyTo(Settings.ClientConfig config)
    {
        config.Name = Name;
        config.Endpoint = Endpoint;
        config.RetryInterval = RetryInterval;
        config.Tls = Tls;
        config.AutoStart = AutoStart;
    }

    // ---- state ----

    public ServerState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State), nameof(StatusText), nameof(StatusBrush), nameof(ActionText), nameof(IsEditable));
        }
    }

    public bool IsEditable => State is ServerState.Stopped or ServerState.Failed;
    public string ActionText => State is ServerState.Stopped or ServerState.Failed ? "Connect" : "Disconnect";
    public string LogText => _log.ToString();

    public string StatusText => State switch
    {
        ServerState.Starting => "Connecting…",
        ServerState.Running => $"Connected · {_received} messages",
        ServerState.Stopping => "Disconnecting…",
        ServerState.Restarting => $"Reconnecting in {RetryInterval}s",
        ServerState.Failed => "Failed",
        _ => "Not connected"
    };

    public Brush StatusBrush => State switch
    {
        ServerState.Running => Theme("SystemFillColorSuccessBrush", 0x3D, 0xD6, 0x8C),
        ServerState.Starting or ServerState.Stopping or ServerState.Restarting
            => Theme("SystemFillColorCautionBrush", 0xE3, 0xB3, 0x41),
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
        if (State is ServerState.Running or ServerState.Starting or ServerState.Restarting) return;
        if (string.IsNullOrWhiteSpace(EffectiveUrl))
        {
            Log("Set an endpoint first.");
            State = ServerState.Failed;
            FlushLog();
            return;
        }

        _received = 0;
        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => RunWithRetries(_cancellation.Token));
    }

    public void Disconnect()
    {
        if (State is ServerState.Stopped) return;

        State = ServerState.Stopping;
        _cancellation?.Cancel();
        try { _socket?.Abort(); } catch { }
        _socket = null;
        State = ServerState.Stopped;
        Log("Disconnected.");
        FlushLog();
    }

    /// <summary>Dial, read until the connection drops, wait, dial again — until told to stop.</summary>
    private async Task RunWithRetries(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _ui.TryEnqueue(() => State = ServerState.Starting);
            Log($"Connecting to {EffectiveUrl}…");

            try
            {
                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new Uri(EffectiveUrl), token);
                _ui.TryEnqueue(() => State = ServerState.Running);
                Log("Connected.");

                await ReadLoop(token);
                Log("Connection closed by the other end.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Connect failed: {ex.Message}");
            }

            if (token.IsCancellationRequested) break;

            _ui.TryEnqueue(() => State = ServerState.Restarting);
            Log($"Retrying in {RetryInterval}s.");
            try { await Task.Delay(TimeSpan.FromSeconds(RetryInterval), token); }
            catch (OperationCanceledException) { break; }
        }

        _ui.TryEnqueue(() => { if (State != ServerState.Stopped) State = ServerState.Stopped; });
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (_socket is { State: WebSocketState.Open } && !token.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) return;

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            _received++;
            Log($"recv: {(text.Length <= 160 ? text : text[..160] + "…")}");
            _ui.TryEnqueue(() => Notify(nameof(StatusText)));
        }
    }

    public async Task SendAsync(string text)
    {
        if (_socket is not { State: WebSocketState.Open }) return;
        await _socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
            WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private void Log(string line) => _incoming.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");

    public void FlushLog()
    {
        if (_incoming.IsEmpty) return;

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
