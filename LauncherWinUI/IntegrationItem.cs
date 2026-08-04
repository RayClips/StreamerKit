using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace StreamerKit;

/// <summary>
/// One entry on the Integrations page.
///
/// Nothing here connects to anything yet — the page lists what StreamerKit is meant to
/// talk to, and every card is inert. Read from integrations.json next to the exe.
/// </summary>
public sealed class IntegrationItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Requirement { get; set; } = "";
    public string Color { get; set; } = "#60CDFF";

    /// <summary>Key into icons.json. Falls back to the initial-letter tile when absent.</summary>
    public string Icon { get; set; } = "";

    /// <summary>False means the platform page just explains why it isn't wired up.</summary>
    public bool Available { get; set; }

    [JsonIgnore] public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    [JsonIgnore] public bool HasIcon => IconSource is not null;
    [JsonIgnore] public Visibility IconVisibility => HasIcon ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility InitialVisibility => HasIcon ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore] public ImageSource? IconSource => Icons.Get(Icon);

    [JsonIgnore]
    public Brush Tile
    {
        get
        {
            var value = Convert.ToUInt32(Color.TrimStart('#'), 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                (byte)(value >> 16), (byte)(value >> 8), (byte)value));
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<IntegrationItem> Load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<IntegrationItem>();
        return JsonSerializer.Deserialize<List<IntegrationItem>>(File.ReadAllText(path), Options)
               ?? new List<IntegrationItem>();
    }
}
