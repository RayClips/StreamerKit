using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamerKit;

/// <summary>One entry in servers.json.</summary>
public sealed class ServerConfig
{
    public string Name { get; set; } = "Server";
    public string? Subtitle { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";

    /// <summary>Used when <see cref="Command"/> isn't on PATH (e.g. py -> python).</summary>
    public string? FallbackCommand { get; set; }
    public string? FallbackArguments { get; set; }

    public bool AutoStart { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static void Save(string path, IEnumerable<ServerConfig> servers)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(servers,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* read-only folder: the change just won't survive a restart */ }
    }

    public static IReadOnlyList<ServerConfig> Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config not found: {path}");

        return JsonSerializer.Deserialize<List<ServerConfig>>(File.ReadAllText(path), Options)
               ?? new List<ServerConfig>();
    }
}
