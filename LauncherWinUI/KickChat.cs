using System.ComponentModel;
using System.Text.Json.Nodes;
using KickChatSpy;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamerKit;

/// <summary>
/// Reads a Kick channel's chat by way of <see cref="KickChatClient"/>, which subscribes to
/// the channel's Pusher chatroom. Read-only and anonymous, same as the Twitch reader —
/// messages are converted to Streamer.bot's Kick.ChatMessage shape and handed on.
/// </summary>
public sealed class KickChat : INotifyPropertyChanged, IChatSource
{
    private readonly DispatcherQueue _ui;
    private readonly KickChatClient _client = new();
    private ServerState _state = ServerState.Stopped;
    private string _channel = "";
    private bool _autoConnect;
    private int _messages;

    public KickChat()
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _client.OnMessageReceived += OnKickMessage;
    }

    public event Action<JsonObject>? ChatMessage;
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
        ServerState.Running => $"Reading {Channel} · {_messages} messages",
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

    public async void Connect()
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

        try
        {
            // A numeric value is a chatroom id and skips the lookup entirely. That matters:
            // Kick's channel API is behind Cloudflare and answers 403 to anything that isn't
            // a real browser, so name lookup fails even for channels that plainly exist.
            if (long.TryParse(Channel, out var chatroomId))
            {
                Log($"Connecting to chatroom {chatroomId}…");
                await _client.ConnectToChatroomAsync(chatroomId);
            }
            else
            {
                Log($"Looking up chatroom for {Channel}…");
                await _client.ConnectToChatroomAsync(Channel);
            }

            State = ServerState.Running;
            Log($"Joined {Channel}");
        }
        catch (InvalidOperationException ex)
        {
            Log(ex.Message);
            Log("The name lookup goes through a bridge service, which may be asleep or down. "
              + "You can paste the numeric chatroom id here instead to connect directly.");
            State = ServerState.Failed;
        }
        catch (Exception ex)
        {
            Log($"Connection failed: {ex.Message}");
            State = ServerState.Failed;
        }
    }

    public async void Disconnect()
    {
        if (State is ServerState.Stopped) return;

        State = ServerState.Stopping;
        try { await _client.DisconnectAsync(); }
        catch (Exception ex) { Log($"Error while disconnecting: {ex.Message}"); }

        State = ServerState.Stopped;
        Log("Disconnected.");
    }

    /// <summary>Kick message -> the Kick.ChatMessage payload overlay widgets read.</summary>
    private void OnKickMessage(KickChatSpy.Models.ChatMessage message)
    {
        var sender = message.Sender;
        var login = sender?.Slug ?? sender?.Username ?? "unknown";
        var name = sender?.Username ?? login;
        var color = sender?.Identity?.Color;
        if (string.IsNullOrWhiteSpace(color)) color = "#53FC18";

        var role = 1;
        foreach (var badge in sender?.Identity?.Badges ?? new List<KickChatSpy.Models.KickBadge>())
        {
            switch (badge.Type)
            {
                case "vip": role = Math.Max(role, 2); break;
                case "moderator": role = Math.Max(role, 3); break;
                case "broadcaster": role = 4; break;
            }
        }

        var text = message.Content ?? "";
        var payload = new JsonObject
        {
            ["id"] = message.Id ?? Guid.NewGuid().ToString(),
            ["messageId"] = message.Id ?? Guid.NewGuid().ToString(),
            ["text"] = text,
            ["isReply"] = false,
            ["firstMessage"] = false,
            ["user"] = new JsonObject
            {
                ["id"] = sender?.Id.ToString() ?? "0",
                ["login"] = login,
                ["name"] = name,
                ["color"] = color,
                ["role"] = role,
                ["badges"] = new JsonArray(),
                ["subscribed"] = false
            },
            ["parts"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
        };

        _messages++;
        _ui.TryEnqueue(() =>
        {
            ChatMessage?.Invoke(payload);
            Notify(nameof(StatusText));
        });
    }

    private void Log(string line) => _ui.TryEnqueue(() => Logged?.Invoke(line));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Shared by the chat readers so one detail page can drive either.</summary>
public interface IChatSource
{
    void Toggle();
    ServerState State { get; }
    string Channel { get; set; }

    /// <summary>Connect on launch instead of waiting to be asked. Persisted in settings.json.</summary>
    bool AutoConnect { get; set; }
}
