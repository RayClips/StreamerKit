using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamerKit;

/// <summary>
/// Reads a Twitch channel's chat over Twitch's IRC-over-WebSocket gateway, anonymously.
///
/// Anonymous means logging in as "justinfan&lt;random&gt;", which Twitch accepts without a
/// token. That buys chat messages and nothing else — follows, subs, bits and raids come
/// from EventSub, which needs OAuth. Read-only: StreamerKit cannot send messages this way.
/// </summary>
public sealed class TwitchChat : INotifyPropertyChanged, IChatSource
{
    private const string Gateway = "wss://irc-ws.chat.twitch.tv:443";

    private readonly DispatcherQueue _ui;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellation;
    private ServerState _state = ServerState.Stopped;
    private string _channel = "";
    private bool _autoConnect;
    private int _messages;

    public TwitchChat() => _ui = DispatcherQueue.GetForCurrentThread();

    /// <summary>Raised on the UI thread for each chat message, already in Streamer.bot shape.</summary>
    public event Action<JsonObject>? ChatMessage;

    /// <summary>Raised for anything worth showing in a log.</summary>
    public event Action<string>? Logged;

    public string Channel
    {
        get => _channel;
        set
        {
            var cleaned = (value ?? "").Trim().TrimStart('#').ToLowerInvariant();
            if (_channel == cleaned) return;
            _channel = cleaned;
            Notify(nameof(Channel));
        }
    }

    /// <summary>Connect on launch. Not gated by <see cref="IsEditable"/> — it is a preference
    /// about the next start, so changing it while connected is perfectly sensible.</summary>
    public bool AutoConnect
    {
        get => _autoConnect;
        set
        {
            if (_autoConnect == value) return;
            _autoConnect = value;
            Notify(nameof(AutoConnect));
        }
    }

    public ServerState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State), nameof(StatusText), nameof(StatusBrush), nameof(ActionText),
                   nameof(IsEditable));
        }
    }

    public bool IsEditable => State is ServerState.Stopped or ServerState.Failed;
    public string ActionText => State is ServerState.Stopped or ServerState.Failed ? "Connect" : "Disconnect";

    public string StatusText => State switch
    {
        ServerState.Starting => "Connecting…",
        ServerState.Running => $"Reading #{Channel} · {_messages} messages",
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
        if (string.IsNullOrWhiteSpace(Channel))
        {
            Log("Set a channel name first.");
            State = ServerState.Failed;
            return;
        }

        _messages = 0;
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
        State = ServerState.Stopped;
        Log("Disconnected.");
    }

    private async Task Run(CancellationToken token)
    {
        try
        {
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(new Uri(Gateway), token);
            Log($"Connected to {Gateway}");

            // Anonymous login. The tags capability is what carries display name, colour and badges.
            await SendRaw("CAP REQ :twitch.tv/tags twitch.tv/commands", token);
            await SendRaw($"NICK justinfan{Random.Shared.Next(10000, 99999)}", token);
            await SendRaw($"JOIN #{Channel}", token);

            _ui.TryEnqueue(() => State = ServerState.Running);
            Log($"Joined #{Channel}");

            var buffer = new byte[16 * 1024];
            var pending = new StringBuilder();

            while (_socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                var text = pending.ToString();

                // IRC frames are newline-delimited and can split across reads.
                var lastBreak = text.LastIndexOf('\n');
                if (lastBreak < 0) continue;
                pending.Remove(0, lastBreak + 1);

                foreach (var line in text[..lastBreak].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    await HandleLine(line.TrimEnd('\r'), token);
            }
        }
        catch (OperationCanceledException) { /* asked to stop */ }
        catch (Exception ex)
        {
            Log($"Connection error: {ex.Message}");
            _ui.TryEnqueue(() => State = ServerState.Failed);
            return;
        }

        _ui.TryEnqueue(() => { if (State != ServerState.Stopped) State = ServerState.Stopped; });
    }

    private async Task HandleLine(string line, CancellationToken token)
    {
        if (line.StartsWith("PING"))
        {
            await SendRaw("PONG :tmi.twitch.tv", token);
            return;
        }

        if (!line.Contains("PRIVMSG")) return;

        var message = ParsePrivMsg(line, Channel);
        if (message is null) return;

        _messages++;
        _ui.TryEnqueue(() =>
        {
            ChatMessage?.Invoke(message);
            Notify(nameof(StatusText));
        });
    }

    /// <summary>
    /// Turns one IRC PRIVMSG line into a Streamer.bot ChatMessage payload.
    ///
    /// Carries both generations at once: the flat fields newer widgets read
    /// (data.text, data.user, data.parts) and the nested "message" object the published
    /// @streamerbot/client types describe. Widgets read one or the other, never both.
    /// </summary>
    private static JsonObject? ParsePrivMsg(string line, string channel)
    {
        var tags = new Dictionary<string, string>();
        var rest = line;

        if (rest.StartsWith('@'))
        {
            var split = rest.IndexOf(' ');
            if (split < 0) return null;
            foreach (var pair in rest[1..split].Split(';'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) tags[pair[..eq]] = pair[(eq + 1)..];
            }
            rest = rest[(split + 1)..];
        }

        // :nick!user@host PRIVMSG #channel :text
        if (!rest.StartsWith(':')) return null;
        var prefixEnd = rest.IndexOf(' ');
        if (prefixEnd < 0) return null;
        var prefix = rest[1..prefixEnd];
        var login = prefix.Split('!')[0];

        var textStart = rest.IndexOf(" :", prefixEnd, StringComparison.Ordinal);
        if (textStart < 0) return null;
        var text = rest[(textStart + 2)..];

        var role = 1;                                        // viewer
        var badges = tags.GetValueOrDefault("badges", "");
        if (badges.Contains("vip/")) role = 2;
        if (badges.Contains("moderator/")) role = 3;
        if (badges.Contains("broadcaster/")) role = 4;

        var display = tags.GetValueOrDefault("display-name", login);
        var color = tags.GetValueOrDefault("color", "");
        if (string.IsNullOrWhiteSpace(color)) color = "#9147FF";

        var msgId = tags.GetValueOrDefault("id", Guid.NewGuid().ToString());
        var userId = tags.GetValueOrDefault("user-id", "0");
        var subscriber = tags.GetValueOrDefault("subscriber", "0") == "1";
        var firstMessage = tags.GetValueOrDefault("first-msg", "0") == "1";
        var isReply = tags.ContainsKey("reply-parent-msg-id");
        var isMe = text.StartsWith('');   // /me arrives wrapped in ACTION

        if (isMe) text = text.Trim('').Replace("ACTION ", "", StringComparison.Ordinal);

        return new JsonObject
        {
            // ---- flat shape (Streamer.bot 0.3.x and newer widgets) ----
            ["messageId"] = msgId,
            ["text"] = text,
            ["isReply"] = isReply,
            ["isFromSharedChatGuest"] = false,
            ["meta"] = new JsonObject
            {
                ["firstMessage"] = firstMessage,
                ["isMe"] = isMe,
                ["subscriber"] = subscriber
            },
            ["user"] = new JsonObject
            {
                ["id"] = userId,
                ["login"] = login,
                ["name"] = display,
                ["color"] = color,
                ["role"] = role,
                ["badges"] = new JsonArray(),
                ["subscribed"] = subscriber
            },
            ["parts"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),

            // ---- nested shape (@streamerbot/client TwitchChatMessage type) ----
            ["message"] = new JsonObject
            {
                ["internal"] = false,
                ["msgId"] = msgId,
                ["userId"] = userId,
                ["username"] = login,
                ["role"] = role,
                ["subscriber"] = subscriber,
                ["displayName"] = display,
                ["color"] = color,
                ["channel"] = channel,
                ["message"] = text,
                ["isHighlighted"] = tags.GetValueOrDefault("msg-id", "") == "highlighted-message",
                ["isMe"] = isMe,
                ["isCustomReward"] = tags.ContainsKey("custom-reward-id"),
                ["isAnonymous"] = false,
                ["isReply"] = isReply,
                ["bits"] = int.TryParse(tags.GetValueOrDefault("bits", "0"), out var bits) ? bits : 0,
                ["firstMessage"] = firstMessage,
                ["hasBits"] = tags.ContainsKey("bits"),
                ["emotes"] = new JsonArray(),
                ["cheerEmotes"] = new JsonArray(),
                ["badges"] = new JsonArray(),
                ["monthsSubscribed"] = 0,
                ["isTest"] = false
            }
        };
    }

    private async Task SendRaw(string line, CancellationToken token)
    {
        if (_socket is not { State: WebSocketState.Open }) return;
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }

    private void Log(string line) => _ui.TryEnqueue(() => Logged?.Invoke(line));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
