using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Connects to Discord as a bot: the gateway for reading, the REST API for writing.
///
/// Two transports, because Discord splits them. Everything that *arrives* — messages, the
/// list of servers the bot is in — comes down a WebSocket at gateway.discord.gg, which is a
/// push-only stream: you cannot ask it a question. Everything that *goes out* is an ordinary
/// HTTPS request to discord.com/api. So unlike OBS or Streamlabs there is no request/reply
/// pairing to do here, and no id to match on.
///
/// The gateway handshake is fixed and unforgiving: the server opens with HELLO (op 10)
/// carrying a heartbeat interval, and the client must both IDENTIFY (op 2) *and* start
/// heartbeating (op 1) or it is closed within a minute. The heartbeat carries the sequence
/// number of the last dispatch received, which is why <see cref="_sequence"/> is tracked on
/// every op 0.
/// </summary>
public sealed class DiscordClient : INotifyPropertyChanged
{
    private const int LogCapacity = 60_000;

    private const string Api = "https://discord.com/api/v10";
    private const string Gateway = "wss://gateway.discord.gg/?v=10&encoding=json";

    // ---- Gateway intents -----------------------------------------------------
    //
    // A bot is sent only what it asks for. GUILDS is what makes GUILD_CREATE arrive, and
    // GUILD_CREATE is the only place the role list turns up — without it every Discord
    // chatter would look like an ordinary viewer to a permission gate.
    //
    // MESSAGE_CONTENT is different in kind: it is *privileged*, off by default, and has to
    // be switched on for the application in the developer portal. Asking for one that isn't
    // enabled does not degrade — Discord closes the socket with 4014 and the bot never
    // connects at all, which is why that close code gets its own explanation below.

    private const int IntentGuilds = 1 << 0;
    private const int IntentGuildMessages = 1 << 9;
    private const int IntentDirectMessages = 1 << 12;
    private const int IntentMessageContent = 1 << 15;

    /// <summary>Permissions that make someone a moderator as far as a trigger is concerned.</summary>
    private const ulong PermissionAdministrator = 1UL << 3;
    private const ulong PermissionManageMessages = 1UL << 13;

    /// <summary>
    /// What the invite link asks for: see channels, send messages and embeds, attach files,
    /// read history. Deliberately no moderation or management permissions — nothing here
    /// needs them, and an invite that asks for them is one a server owner should refuse.
    /// </summary>
    private const long InvitePermissions = (1L << 10) | (1L << 11) | (1L << 14) | (1L << 15) | (1L << 16);

    private static readonly HttpClient Rest = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly DispatcherQueue _ui;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly StringBuilder _log = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Role id -> its permission bits, filled from GUILD_CREATE.</summary>
    private readonly ConcurrentDictionary<string, ulong> _rolePermissions = new();

    /// <summary>Guild id -> the owner's user id, so the server owner reads as broadcaster.</summary>
    private readonly ConcurrentDictionary<string, string> _guildOwners = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellation;
    private ServerState _state = ServerState.Stopped;
    private int? _sequence;
    private bool _acknowledged = true;

    /// <summary>
    /// Set when the reason for failing cannot be fixed by trying again — a rejected token or
    /// an intent that isn't enabled. Reconnecting on those would hammer Discord forever while
    /// showing the user a connection that looks merely flaky rather than misconfigured.
    /// </summary>
    private bool _fatal;

    private string _applicationId;
    private string _token;
    private bool _readMessages;
    private bool _autoConnect;
    private string _detail = "";

    public DiscordClient(Settings.DiscordConfig config)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _applicationId = config.ApplicationId;
        _token = config.Token;
        _readMessages = config.ReadMessages;
        _autoConnect = config.AutoConnect;
    }

    public event Action? ConfigChanged;

    /// <summary>Raised for every message the bot can see, shaped like the other chat sources.</summary>
    public event Action<JsonObject>? ChatMessage;

    // ---- configuration ------------------------------------------------------

    /// <summary>The application (client) id. Only used to build the invite link.</summary>
    public string ApplicationId
    {
        get => _applicationId;
        set => Set(ref _applicationId, (value ?? "").Trim(), nameof(ApplicationId), nameof(InviteUrl));
    }

    /// <summary>The bot token. A real credential — it is the bot.</summary>
    public string Token
    {
        get => _token;
        set => Set(ref _token, value ?? "", nameof(Token));
    }

    /// <summary>Whether to ask for the privileged Message Content intent.</summary>
    public bool ReadMessages
    {
        get => _readMessages;
        set => Set(ref _readMessages, value, nameof(ReadMessages));
    }

    /// <summary>Connect on launch instead of waiting to be asked.</summary>
    public bool AutoConnect
    {
        get => _autoConnect;
        set => Set(ref _autoConnect, value, nameof(AutoConnect));
    }

    /// <summary>The URL that adds this bot to a server, or an explanation if the id is missing.</summary>
    public string InviteUrl => ApplicationId.Length == 0
        ? "Enter the application id to build an invite link."
        : $"https://discord.com/api/oauth2/authorize?client_id={ApplicationId}"
          + $"&permissions={InvitePermissions}&scope=bot%20applications.commands";

    public bool HasInvite => ApplicationId.Length > 0;

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (Equals(field, value)) return;
        field = value;
        Notify(names);
        ConfigChanged?.Invoke();
    }

    public void CopyTo(Settings.DiscordConfig config)
    {
        config.ApplicationId = ApplicationId;
        config.Token = Token;
        config.ReadMessages = ReadMessages;
        config.AutoConnect = AutoConnect;
    }

    // ---- status, named to match the other integrations ----------------------

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

    // ---- lifecycle ----------------------------------------------------------

    public void Toggle()
    {
        if (State is ServerState.Stopped or ServerState.Failed) Connect();
        else Disconnect();
    }

    public void Connect()
    {
        if (State is ServerState.Running or ServerState.Starting) return;

        if (string.IsNullOrWhiteSpace(Token))
        {
            _detail = "No bot token.";
            Log("No bot token. Discord Developer Portal -> your application -> Bot -> Reset Token.");
            State = ServerState.Failed;
            FlushLog();
            return;
        }

        _detail = "";
        _fatal = false;
        Forget();
        State = ServerState.Starting;
        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => Supervise(_cancellation.Token));
    }

    public void Disconnect()
    {
        if (State is ServerState.Stopped) return;

        State = ServerState.Stopping;
        _cancellation?.Cancel();
        try { _socket?.Abort(); } catch { }
        _socket = null;
        _detail = "";
        Forget();
        State = ServerState.Stopped;
        Log("Disconnected.");
        FlushLog();
    }

    /// <summary>Nothing learned about a guild survives a reconnect — the bot may have been removed.</summary>
    private void Forget()
    {
        _rolePermissions.Clear();
        _guildOwners.Clear();
        _sequence = null;
    }

    /// <summary>
    /// Keeps a gateway session up. Discord drops connections routinely — during its own
    /// deploys, and with op 7 whenever it wants the client to come back — so a single
    /// connection attempt would leave the integration quietly dead after a few hours.
    /// Backoff exists so a server-side outage doesn't turn into a reconnect storm.
    /// </summary>
    private async Task Supervise(CancellationToken token)
    {
        var delay = 5;

        while (!token.IsCancellationRequested)
        {
            var connected = await RunSession(token);
            if (token.IsCancellationRequested || _fatal) break;

            // A session that actually reached READY is treated as healthy, so a drop after
            // six hours online doesn't inherit the backoff from six hours ago.
            if (connected) delay = 5;

            Log($"Reconnecting in {delay} seconds…");
            _ui.TryEnqueue(() =>
            {
                if (State == ServerState.Running) { _detail = "reconnecting"; State = ServerState.Starting; }
            });
            FlushLog();

            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); }
            catch (OperationCanceledException) { break; }

            delay = Math.Min(delay * 2, 60);
        }

        _ui.TryEnqueue(() => { if (State != ServerState.Stopped && !_fatal) State = ServerState.Stopped; });
        FlushLog();
    }

    /// <summary>One gateway connection, from open to close. True if it got as far as READY.</summary>
    private async Task<bool> RunSession(CancellationToken token)
    {
        var ready = false;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            _socket = new ClientWebSocket();
            Log("Connecting to the Discord gateway…");
            await _socket.ConnectAsync(new Uri(Gateway), token);

            var buffer = new byte[64 * 1024];
            var pending = new StringBuilder();

            while (_socket is { State: WebSocketState.Open } && !session.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), session.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Explain(_socket.CloseStatus, _socket.CloseStatusDescription);
                    break;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;      // a payload can span several reads

                var text = pending.ToString();
                pending.Clear();

                try { ready |= Handle(text, session); }
                catch (Exception ex) { Log($"Error handling a gateway payload: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A refused handshake does not always come back as a tidy Close frame — often
            // ReceiveAsync throws instead, with the close code left on the socket. Reading it
            // here is what keeps "the token is wrong" from surfacing as a generic socket error.
            if (_socket?.CloseStatus is { } status)
                Explain(status, _socket.CloseStatusDescription);
            else if (!_fatal)
                Log($"Gateway connection failed: {ex.Message}");
        }
        finally
        {
            session.Cancel();                       // stops the heartbeat loop
            try { _socket?.Abort(); } catch { }
        }

        FlushLog();
        return ready;
    }

    /// <summary>
    /// Discord reports every refusal as a WebSocket close code, never as a payload. The two
    /// that matter are the two a user can actually fix, and they look identical from the
    /// outside — the socket simply shuts — so they are spelled out rather than passed through.
    /// </summary>
    private void Explain(WebSocketCloseStatus? status, string? description)
    {
        var code = (int?)status ?? 0;

        switch (code)
        {
            case 4004:
                Fatal("Discord rejected the bot token. Copy it again from the Developer Portal "
                    + "(Bot -> Reset Token); it is shown once, and resetting invalidates the old one.");
                break;

            case 4014:
                Fatal("Discord refused the Message Content intent. It is privileged and off by "
                    + "default: Developer Portal -> your application -> Bot -> Privileged Gateway "
                    + "Intents -> Message Content. Until then, untick \"Read message text\" to "
                    + "connect without it.");
                break;

            case 4013:
                Fatal("Discord rejected the intents StreamerKit asked for.");
                break;

            case 4010:
            case 4011:
            case 4012:
                Fatal($"Discord closed the connection with {code}: {description}");
                break;

            default:
                Log($"Gateway closed ({code}{(string.IsNullOrEmpty(description) ? "" : $": {description}")}).");
                break;
        }
    }

    /// <summary>A failure that retrying cannot fix. Stops the supervisor and says why.</summary>
    private void Fatal(string reason)
    {
        _fatal = true;
        Log(reason);
        _ui.TryEnqueue(() =>
        {
            _detail = reason.Length <= 60 ? reason : reason[..57] + "…";
            State = ServerState.Failed;
            Notify(nameof(StatusText));
        });
        FlushLog();
    }

    /// <summary>Handles one gateway payload. Returns true when this was the READY dispatch.</summary>
    private bool Handle(string text, CancellationTokenSource session)
    {
        var root = JsonNode.Parse(text)?.AsObject();
        if (root is null) return false;

        var op = root["op"]?.GetValue<int>() ?? -1;

        // Every dispatch carries a sequence number, and the heartbeat has to echo the last
        // one seen or Discord assumes messages were missed.
        if (root["s"] is { } s && s.GetValueKind() == JsonValueKind.Number) _sequence = s.GetValue<int>();

        switch (op)
        {
            case 10:                                  // HELLO
                var interval = root["d"]?["heartbeat_interval"]?.GetValue<int>() ?? 41_250;
                Log($"Gateway open. Heartbeat every {interval} ms.");
                _acknowledged = true;
                _ = Task.Run(() => Heartbeat(interval, session.Token));
                _ = Identify();
                return false;

            case 11:                                  // HEARTBEAT ACK
                _acknowledged = true;
                return false;

            case 1:                                   // the gateway asking for one early
                _ = SendJson(new JsonObject { ["op"] = 1, ["d"] = Sequence() });
                return false;

            case 7:                                   // RECONNECT
                Log("Discord asked us to reconnect.");
                session.Cancel();
                return false;

            case 9:                                   // INVALID SESSION
                Log("Discord invalidated the session; starting a new one.");
                session.Cancel();
                return false;

            case 0:                                   // DISPATCH
                return Dispatch(root["t"]?.ToString() ?? "", root["d"]?.AsObject());

            default:
                return false;
        }
    }

    private JsonNode? Sequence() => _sequence is { } value ? JsonValue.Create(value) : null;

    private bool Dispatch(string type, JsonObject? data)
    {
        switch (type)
        {
            case "READY":
                var user = data?["user"]?.AsObject();
                var name = user?["username"]?.ToString() ?? "the bot";
                var tag = user?["discriminator"]?.ToString();
                if (!string.IsNullOrEmpty(tag) && tag != "0") name = $"{name}#{tag}";

                var guilds = data?["guilds"]?.AsArray().Count ?? 0;
                Log($"Ready as {name}, in {guilds} server(s).");

                _ui.TryEnqueue(() =>
                {
                    _detail = guilds == 1 ? $"{name} · 1 server" : $"{name} · {guilds} servers";
                    State = ServerState.Running;
                });
                FlushLog();
                return true;

            case "GUILD_CREATE":
                Remember(data);
                return false;

            case "MESSAGE_CREATE":
                Received(data);
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Caches what a guild's roles mean. GUILD_CREATE is the only push that carries full role
    /// objects; MESSAGE_CREATE gives a member's role *ids* and nothing about what they grant,
    /// so without this every Discord chatter would fall through a "moderator only" gate.
    /// </summary>
    private void Remember(JsonObject? guild)
    {
        if (guild is null) return;

        if (guild["id"]?.ToString() is { Length: > 0 } id &&
            guild["owner_id"]?.ToString() is { Length: > 0 } owner)
            _guildOwners[id] = owner;

        if (guild["roles"] is not JsonArray roles) return;

        foreach (var role in roles)
        {
            if (role?["id"]?.ToString() is not { Length: > 0 } roleId) continue;

            // Permissions come as a decimal string, because the bitfield outgrew a JS number.
            if (ulong.TryParse(role["permissions"]?.ToString(), out var bits))
                _rolePermissions[roleId] = bits;
        }

        Log($"Learned {roles.Count} role(s) in \"{guild["name"]}\".");
    }

    /// <summary>
    /// Turns a Discord message into the same payload shape Twitch and Kick produce, so it
    /// reaches chat triggers and overlay widgets without either knowing where it came from.
    /// </summary>
    private void Received(JsonObject? message)
    {
        if (message is null) return;

        var author = message["author"]?.AsObject();
        if (author is null) return;

        // Bots are skipped, including this one. A "send a Discord message" step reacting to a
        // chat message would otherwise answer itself, forever, as fast as Discord allows.
        if (author["bot"]?.GetValueKind() == JsonValueKind.True) return;

        var content = message["content"]?.ToString() ?? "";
        var userId = author["id"]?.ToString() ?? "";
        var login = author["username"]?.ToString() ?? "";
        var display = message["member"]?["nick"]?.ToString();
        if (string.IsNullOrEmpty(display)) display = author["global_name"]?.ToString();
        if (string.IsNullOrEmpty(display)) display = login;

        var guildId = message["guild_id"]?.ToString() ?? "";
        var channelId = message["channel_id"]?.ToString() ?? "";
        var role = RoleOf(guildId, userId, message["member"]?["roles"] as JsonArray);

        // Message Content is privileged: without it every message arrives with an empty
        // content field rather than not arriving at all, which reads exactly like a broken
        // parser. Say so once per message rather than leaving the user guessing.
        if (content.Length == 0 && !ReadMessages)
            Log($"{display}: (no text — \"Read message text\" is off)");
        else
            Log($"#{channelId}  {display}: {content}");

        var payload = new JsonObject
        {
            // ---- flat shape, the one DispatchChat and newer widgets read ----
            ["messageId"] = message["id"]?.ToString() ?? Guid.NewGuid().ToString(),
            ["text"] = content,
            ["isReply"] = message["referenced_message"] is not null,
            ["channelId"] = channelId,
            ["guildId"] = guildId,
            ["user"] = new JsonObject
            {
                ["id"] = userId,
                ["login"] = login,
                ["name"] = display,
                ["color"] = "#5865F2",
                ["role"] = role,
                ["badges"] = new JsonArray(),
                ["subscribed"] = false
            },
            ["parts"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content }),

            // ---- nested shape, for widgets typed against @streamerbot/client ----
            ["message"] = new JsonObject
            {
                ["internal"] = false,
                ["msgId"] = message["id"]?.ToString() ?? "",
                ["userId"] = userId,
                ["username"] = login,
                ["displayName"] = display,
                ["role"] = role,
                ["color"] = "#5865F2",
                ["channel"] = channelId,
                ["message"] = content,
                ["isMe"] = false,
                ["isAnonymous"] = false,
                ["isTest"] = false
            }
        };

        FlushLog();
        ChatMessage?.Invoke(payload);
    }

    /// <summary>
    /// Discord's nearest thing to Twitch's role ladder. There is no VIP equivalent, so the
    /// middle rung is never handed out rather than being faked.
    /// </summary>
    private int RoleOf(string guildId, string userId, JsonArray? roles)
    {
        if (guildId.Length > 0 && _guildOwners.TryGetValue(guildId, out var owner) && owner == userId)
            return 4;                                  // server owner ~ broadcaster

        if (roles is null) return 1;

        foreach (var role in roles)
        {
            if (role?.ToString() is not { Length: > 0 } id) continue;
            if (!_rolePermissions.TryGetValue(id, out var bits)) continue;

            if ((bits & (PermissionAdministrator | PermissionManageMessages)) != 0) return 3;
        }

        return 1;
    }

    private async Task Identify()
    {
        var intents = IntentGuilds | IntentGuildMessages | IntentDirectMessages;
        if (ReadMessages) intents |= IntentMessageContent;

        Log($"Identifying (intents {intents}"
            + $"{(ReadMessages ? ", including the privileged Message Content" : "")}).");

        await SendJson(new JsonObject
        {
            ["op"] = 2,
            ["d"] = new JsonObject
            {
                ["token"] = Token,
                ["intents"] = intents,
                ["properties"] = new JsonObject
                {
                    ["os"] = "windows",
                    ["browser"] = "StreamerKit",
                    ["device"] = "StreamerKit"
                }
            }
        });
    }

    /// <summary>
    /// Beats until the session ends. A beat that is never acknowledged means the connection
    /// is a zombie — the socket is open, nothing is arriving — so it is torn down rather than
    /// left looking connected.
    /// </summary>
    private async Task Heartbeat(int interval, CancellationToken token)
    {
        try
        {
            // The first beat is jittered, as Discord asks, so a thousand clients reconnecting
            // after an outage don't all beat on the same millisecond.
            await Task.Delay((int)(interval * Random.Shared.NextDouble()), token);

            while (!token.IsCancellationRequested)
            {
                if (!_acknowledged)
                {
                    Log("Discord stopped acknowledging heartbeats; dropping the connection.");
                    FlushLog();
                    try { _socket?.Abort(); } catch { }
                    return;
                }

                _acknowledged = false;
                await SendJson(new JsonObject { ["op"] = 1, ["d"] = Sequence() });
                await Task.Delay(interval, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Heartbeat stopped: {ex.Message}"); }
    }

    private async Task SendJson(JsonObject payload)
    {
        if (_socket is not { State: WebSocketState.Open }) return;

        // A WebSocket allows one outstanding SendAsync; the heartbeat and identify overlap.
        await _sendGate.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                                    CancellationToken.None);
        }
        catch (Exception ex) { Log($"Could not send to the gateway: {ex.Message}"); }
        finally { _sendGate.Release(); }
    }

    // ---- REST ---------------------------------------------------------------

    /// <summary>What a REST call did. Discord explains refusals in the body, not the status.</summary>
    public sealed record DiscordResult(bool Ok, JsonNode? Result, string Error);

    /// <summary>Posts a plain message to a channel.</summary>
    public Task<DiscordResult> SendMessage(string channelId, string content)
        => Post(channelId, new JsonObject { ["content"] = content });

    /// <summary>
    /// Posts an embed — the boxed, coloured message Discord uses for announcements.
    /// </summary>
    public Task<DiscordResult> SendEmbed(string channelId, string title, string description,
                                         string url, string colour, string image, string content)
    {
        var embed = new JsonObject();
        if (title.Length > 0) embed["title"] = title;
        if (description.Length > 0) embed["description"] = description;
        if (url.Length > 0) embed["url"] = url;
        if (image.Length > 0) embed["image"] = new JsonObject { ["url"] = image };
        if (ParseColour(colour) is { } rgb) embed["color"] = rgb;

        var body = new JsonObject { ["embeds"] = new JsonArray(embed) };
        if (content.Length > 0) body["content"] = content;

        return Post(channelId, body);
    }

    /// <summary>
    /// "#9443CC", "9443CC" or a plain number, as the integer Discord wants.
    ///
    /// The ambiguous case is a string of digits: "255" is both valid hex and valid decimal,
    /// and they are different colours. A leading # means hex outright; without one, all-digits
    /// is read as decimal and anything containing a-f as hex, so neither spelling is silently
    /// reinterpreted as the other.
    /// </summary>
    public static int? ParseColour(string colour)
    {
        colour = (colour ?? "").Trim();
        if (colour.Length == 0) return null;

        var hexOnly = colour.StartsWith('#');
        colour = colour.TrimStart('#');
        if (colour.Length == 0) return null;

        if (!hexOnly && colour.All(char.IsAsciiDigit))
            return int.TryParse(colour, out var plain) ? plain : null;

        return int.TryParse(colour, System.Globalization.NumberStyles.HexNumber, null, out var hex)
            ? hex
            : null;
    }

    private async Task<DiscordResult> Post(string channelId, JsonObject body)
    {
        channelId = (channelId ?? "").Trim();
        if (channelId.Length == 0)
            return Refuse("no channel id — right-click a channel in Discord and Copy Channel ID.");

        return await Send(HttpMethod.Post, $"channels/{channelId}/messages", body);
    }

    /// <summary>
    /// Any REST call, for the raw block and the panel's explore buttons. The path is relative
    /// to /api/v10, so "users/@me" rather than the whole URL.
    /// </summary>
    public async Task<DiscordResult> Send(HttpMethod method, string path, JsonObject? body = null)
    {
        if (string.IsNullOrWhiteSpace(Token)) return Refuse("no bot token.");

        path = (path ?? "").Trim().TrimStart('/');
        if (path.Length == 0) return Refuse("no API path.");

        using var request = new HttpRequestMessage(method, $"{Api}/{path}");

        // "Bot " is not optional and not a scheme Discord infers — without it every call is a
        // 401 that says nothing about why.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", Token);
        request.Headers.UserAgent.ParseAdd("StreamerKit (https://streamerkit.local, 1.0)");

        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        Log($"---> {method} /{path}{(body is null ? "" : $"  {Compact(body)}")}");

        try
        {
            using var response = await Rest.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var parsed = Parse(text);

            if (response.IsSuccessStatusCode)
            {
                Log($"<--- {(int)response.StatusCode} ok");
                FlushLog();
                return new DiscordResult(true, parsed, "");
            }

            // 429 carries the wait in the body; everything else carries a message and a code.
            var reason = parsed?["message"]?.ToString() ?? $"HTTP {(int)response.StatusCode}";
            if (parsed?["retry_after"] is { } wait) reason += $" (retry after {wait}s)";
            if (parsed?["code"] is { } code) reason += $" [{code}]";

            Log($"<--- {(int)response.StatusCode} FAILED  {reason}");
            FlushLog();
            return new DiscordResult(false, parsed, reason);
        }
        catch (Exception ex)
        {
            Log($"<--- {method} /{path} failed: {ex.Message}");
            FlushLog();
            return new DiscordResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Posts to a webhook URL. The one thing here that needs no bot at all — a webhook is its
    /// own credential, so this works whether or not the gateway is connected.
    /// </summary>
    public async Task<DiscordResult> PostWebhook(string url, JsonObject body)
    {
        url = (url ?? "").Trim();
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Refuse("a webhook URL must start with https:// — copy it from the channel's "
                        + "Integrations settings.");

        Log($"---> webhook  {Compact(body)}");

        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Rest.PostAsync(url, content);
            var text = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Log($"<--- webhook {(int)response.StatusCode} ok");
                FlushLog();
                return new DiscordResult(true, Parse(text), "");
            }

            var reason = Parse(text)?["message"]?.ToString() ?? $"HTTP {(int)response.StatusCode}";
            Log($"<--- webhook {(int)response.StatusCode} FAILED  {reason}");
            FlushLog();
            return new DiscordResult(false, null, reason);
        }
        catch (Exception ex)
        {
            Log($"<--- webhook failed: {ex.Message}");
            FlushLog();
            return new DiscordResult(false, null, ex.Message);
        }
    }

    private DiscordResult Refuse(string reason)
    {
        Log(reason);
        FlushLog();
        return new DiscordResult(false, null, reason);
    }

    private static JsonNode? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); } catch { return null; }
    }

    private static string Compact(JsonNode? node)
    {
        if (node is null) return "";
        var text = node.ToJsonString();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    // ---- logging ------------------------------------------------------------

    public void ClearLog()
    {
        _log.Clear();
        Notify(nameof(LogText));
    }

    private void Log(string line) => _incoming.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");

    public void FlushLog()
    {
        if (_incoming.IsEmpty) return;

        // Same trap as the other clients: draining on a background thread raises
        // PropertyChanged where the binding ignores it, and leaves the queue empty so the
        // next pump tick returns before notifying either.
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
