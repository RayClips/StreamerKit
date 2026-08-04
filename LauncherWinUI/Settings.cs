using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace StreamerKit;

/// <summary>App preferences, stored as settings.json next to the exe.</summary>
public sealed class Settings
{
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Which page to open on launch: "home", "servers" or "plugins".</summary>
    public string StartPage { get; set; } = "home";

    /// <summary>Restart a server automatically after it exits unexpectedly.</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>Channels to read chat from, anonymously. Empty means not set up.</summary>
    public string TwitchChannel { get; set; } = "";
    public string KickChannel { get; set; } = "";

    /// <summary>
    /// Start reading that channel's chat on launch. Separate from the channel name because
    /// forgetting the channel to stop auto-connecting would be a rotten trade.
    /// </summary>
    public bool TwitchAutoConnect { get; set; }
    public bool KickAutoConnect { get; set; }

    /// <summary>Configuration for the servers StreamerKit hosts itself.</summary>
    public HostedConfig Http { get; set; } = new() { Port = 8080, Endpoint = "/" };
    public HostedConfig WebSocket { get; set; } = new() { Port = 8081, Endpoint = "/" };
    public HostedConfig Udp { get; set; } = new() { Port = 8082, Endpoint = "/" };

    /// <summary>Per-server settings for a hosted server, as shown on its detail page.</summary>
    public sealed class HostedConfig
    {
        public string Name { get; set; } = "";
        public bool AutoStart { get; set; }
        public string Address { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 8080;
        public string Endpoint { get; set; } = "/";
        public bool Authentication { get; set; }
        public string Token { get; set; } = "";
    }

    /// <summary>An outbound WebSocket connection StreamerKit dials.</summary>
    public sealed class ClientConfig
    {
        public string Name { get; set; } = "WebSocket Client";
        public string Endpoint { get; set; } = "ws://127.0.0.1:8080/";
        public int RetryInterval { get; set; } = 5;      // seconds between reconnects
        public bool Tls { get; set; }                    // force wss://
        public bool AutoStart { get; set; }
    }

    /// <summary>obs-websocket connection. The password is a real credential.</summary>
    public sealed class ObsConfig
    {
        public string Address { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 4455;
        public string Password { get; set; } = "";
        public bool AutoConnect { get; set; }
    }

    public ObsConfig Obs { get; set; } = new();

    /// <summary>
    /// Streamlabs Desktop's remote-control API. The token is a real credential — it is the
    /// only thing standing between this port and anything else that can reach it.
    /// </summary>
    public sealed class StreamlabsConfig
    {
        public string Address { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 59650;
        public string Token { get; set; } = "";
        public bool AutoConnect { get; set; }
    }

    public StreamlabsConfig Streamlabs { get; set; } = new();

    /// <summary>
    /// A Discord bot. The application id is public — it is half of every invite link — but
    /// the token *is* the bot: anyone holding it can act as it in every server it has joined.
    /// </summary>
    public sealed class DiscordConfig
    {
        public string ApplicationId { get; set; } = "";
        public string Token { get; set; } = "";

        /// <summary>Ask for the privileged Message Content intent. Without it, text arrives empty.</summary>
        public bool ReadMessages { get; set; } = true;

        public bool AutoConnect { get; set; }
    }

    public DiscordConfig Discord { get; set; } = new();

    /// <summary>Extra WebSocket servers and clients the user added themselves.</summary>
    public List<HostedConfig> CustomServers { get; set; } = new();
    public List<ClientConfig> CustomClients { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), Options) ?? new Settings()
                : new Settings();
        }
        catch
        {
            return new Settings();   // a corrupt file shouldn't stop the app opening
        }
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Options)); }
        catch { /* read-only folder: preferences just won't persist */ }
    }

    // ---- "Start when I sign in" lives in the registry, not in settings.json ----

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "StreamerKit";

    public static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled)
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
