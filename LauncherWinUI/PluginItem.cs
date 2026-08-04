using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamerKit;

public enum PluginState { Available, Installing, Installed }

/// <summary>
/// One entry in the plugin catalogue.
///
/// There is no backend yet: the catalogue is the bundled plugins.json, and "downloading"
/// is a timer walking a progress bar. Nothing is fetched, written or executed. Installed
/// state lives in memory only and resets when the app closes.
/// </summary>
public sealed class PluginItem : INotifyPropertyChanged
{
    private const int SimulatedStepMilliseconds = 90;

    private readonly DispatcherQueue _ui;
    private DispatcherQueueTimer? _timer;     // held, or it would be collected mid-"download"
    private PluginState _state = PluginState.Available;
    private double _progress;

    public PluginItem() => _ui = DispatcherQueue.GetForCurrentThread();

    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Size { get; set; } = "";
    public string Color { get; set; } = "#60CDFF";

    [JsonIgnore] public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    [JsonIgnore] public string Byline => $"{Author} · v{Version} · {Size}";

    [JsonIgnore]
    public Brush Tile
    {
        get
        {
            var hex = Color.TrimStart('#');
            var value = Convert.ToUInt32(hex, 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                (byte)(value >> 16), (byte)(value >> 8), (byte)value));
        }
    }

    [JsonIgnore]
    public PluginState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State), nameof(AvailableVisibility), nameof(InstallingVisibility),
                   nameof(InstalledVisibility));
        }
    }

    [JsonIgnore]
    public double Progress
    {
        get => _progress;
        private set
        {
            if (Math.Abs(_progress - value) < 0.01) return;
            _progress = value;
            Notify(nameof(Progress), nameof(ProgressText));
        }
    }

    [JsonIgnore] public string ProgressText => $"{Progress:0}%";
    [JsonIgnore] public Visibility AvailableVisibility => Vis(PluginState.Available);
    [JsonIgnore] public Visibility InstallingVisibility => Vis(PluginState.Installing);
    [JsonIgnore] public Visibility InstalledVisibility => Vis(PluginState.Installed);

    private Visibility Vis(PluginState state) => State == state ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Walks a progress bar to 100% and stops. No file is downloaded.</summary>
    public void Install()
    {
        if (State != PluginState.Available) return;

        Progress = 0;
        State = PluginState.Installing;

        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(SimulatedStepMilliseconds);
        _timer.Tick += (timer, _) =>
        {
            Progress += Random.Shared.Next(4, 13);
            if (Progress < 100) return;

            Progress = 100;
            timer.Stop();
            State = PluginState.Installed;
        };
        _timer.Start();
    }

    public void Remove()
    {
        _timer?.Stop();
        Progress = 0;
        State = PluginState.Available;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<PluginItem> LoadCatalog(string path)
    {
        if (!File.Exists(path)) return Array.Empty<PluginItem>();
        return JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(path), Options)
               ?? new List<PluginItem>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
