using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;

namespace StreamerKit;

// ---------------------------------------------------------------------------
//  Field descriptors
//
//  Every block - trigger or sub-action - declares the fields it needs instead of
//  shipping its own editor. The Actions page walks these and builds the controls,
//  so one code path renders every block that exists now and every block added
//  later. A block with a Code field gets a code editor for free.
// ---------------------------------------------------------------------------

public enum FieldKind
{
    /// <summary>Single-line text. Accepts %arguments%.</summary>
    Text,
    /// <summary>Several lines of text - a JSON body, a message.</summary>
    LongText,
    /// <summary>A number, edited with a NumberBox.</summary>
    Number,
    /// <summary>On/off.</summary>
    Toggle,
    /// <summary>One of <see cref="FieldSpec.Choices"/>.</summary>
    Choice,
    /// <summary>Several of <see cref="FieldSpec.Choices"/>, stored comma-separated.</summary>
    MultiChoice,
    /// <summary>A path, with a Browse button.</summary>
    File,
    /// <summary>Source code, in a monospaced editor.</summary>
    Code
}

/// <summary>One option in a <see cref="FieldKind.Choice"/> field.</summary>
public sealed record FieldChoice(string Value, string Label);

/// <summary>Describes one editable field on a block.</summary>
/// <param name="Dynamic">
/// Names a list the editor fills in at render time, for choices that only exist at runtime -
/// see <see cref="DynamicChoices"/>. Takes precedence over <paramref name="Choices"/>.
/// </param>
public sealed record FieldSpec(
    string Key,
    string Label,
    FieldKind Kind = FieldKind.Text,
    string Default = "",
    string? Hint = null,
    IReadOnlyList<FieldChoice>? Choices = null,
    double Minimum = 0,
    double Maximum = 1_000_000,
    string? Dynamic = null);

/// <summary>Choice lists the editor resolves when it draws the field, not when it's declared.</summary>
public static class DynamicChoices
{
    /// <summary>Every server on the Servers page.</summary>
    public const string Servers = "servers";

    /// <summary>Every other action, for the "Run action" step.</summary>
    public const string Actions = "actions";
}

/// <summary>Reads a block's string-keyed settings without every caller parsing by hand.</summary>
public static class ConfigBag
{
    public static string Get(this IDictionary<string, string> config, string key, string fallback = "")
        => config.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

    public static int GetInt(this IDictionary<string, string> config, string key, int fallback = 0)
        => int.TryParse(config.Get(key), out var value) ? value : fallback;

    public static double GetDouble(this IDictionary<string, string> config, string key, double fallback = 0)
        => double.TryParse(config.Get(key), out var value) ? value : fallback;

    public static bool GetBool(this IDictionary<string, string> config, string key, bool fallback = false)
        => bool.TryParse(config.Get(key), out var value) ? value : fallback;

    /// <summary>Reads a MultiChoice field back into its parts.</summary>
    public static string[] GetList(this IDictionary<string, string> config, string key)
        => config.Get(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Fills in anything the user never touched, so Run() can read fields directly.</summary>
    public static void ApplyDefaults(this IDictionary<string, string> config, IReadOnlyList<FieldSpec> fields)
    {
        foreach (var field in fields)
        {
            if (!config.ContainsKey(field.Key)) config[field.Key] = field.Default;
        }
    }
}

// ---------------------------------------------------------------------------
//  Block contracts
// ---------------------------------------------------------------------------

/// <summary>What the picker and the editor need from any block.</summary>
public interface IBlock
{
    /// <summary>Stable id written to actions.json. Never rename one of these.</summary>
    string Type { get; }

    /// <summary>Heading it appears under in the "Add" menu.</summary>
    string Category { get; }

    string Label { get; }

    /// <summary>One line in the picker explaining what it does.</summary>
    string Description { get; }

    IReadOnlyList<FieldSpec> Fields { get; }
}

/// <summary>A step in an action. Runs against the context and nothing else.</summary>
public interface ISubActionBlock : IBlock
{
    /// <summary>The summary shown on the block once it is configured.</summary>
    string Describe(SubActionDefinition definition);

    Task Run(SubActionDefinition definition, ActionContext context);
}

/// <summary>Something that starts an action.</summary>
public interface ITriggerBlock : IBlock
{
    string Describe(TriggerDefinition definition);

    /// <summary>Which <see cref="TriggerEvent.Kind"/> this trigger listens to.</summary>
    string Listens { get; }

    /// <summary>
    /// Decides whether this event fires this trigger, and contributes any arguments the
    /// action should see. Called on the UI thread, so keep it cheap and don't block.
    /// </summary>
    bool Matches(TriggerDefinition definition, TriggerEvent incoming, Dictionary<string, string> arguments);
}

/// <summary>
/// Something that happened, on its way to whichever triggers care. <see cref="Kind"/> selects
/// the trigger types that get a look ("chat", "timer", "server", "doaction").
/// </summary>
public sealed record TriggerEvent(string Kind, IReadOnlyDictionary<string, string> Arguments)
{
    public string Get(string key) => Arguments.TryGetValue(key, out var value) ? value : "";
}

// ---------------------------------------------------------------------------
//  Definitions - what actually gets written to actions.json
// ---------------------------------------------------------------------------

/// <summary>One step of an action, as stored.</summary>
public sealed class SubActionDefinition
{
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Config { get; set; } = new();

    [JsonIgnore] public ISubActionBlock? Block => Blocks.SubAction(Type);

    public SubActionDefinition Clone() => new()
    {
        Type = Type,
        Enabled = Enabled,
        Config = new Dictionary<string, string>(Config)
    };
}

/// <summary>One way an action can start, as stored.</summary>
public sealed class TriggerDefinition
{
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Config { get; set; } = new();

    [JsonIgnore] public ITriggerBlock? Block => Blocks.Trigger(Type);

    // ---- runtime only: cooldowns, timer scheduling, mouse state. Never serialized. ----

    [JsonIgnore] public DateTime LastFired { get; set; } = DateTime.MinValue;
    [JsonIgnore] public DateTime NextDue { get; set; } = DateTime.MinValue;
    [JsonIgnore] public Dictionary<string, DateTime> LastFiredByUser { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Was the cursor inside this trigger's region last time we looked?</summary>
    [JsonIgnore] public bool WasInside { get; set; }

    /// <summary>Was this trigger's key or mouse button held last time we looked?</summary>
    [JsonIgnore] public bool WasDown { get; set; }

    /// <summary>
    /// What the cursor just did about this region: 1 entered, -1 left, 0 nothing. The engine
    /// works this out for every mouse trigger before dispatching, so a trigger that fires
    /// first can't leave the others holding stale state.
    /// </summary>
    [JsonIgnore] public int Edge { get; set; }
}

/// <summary>
/// A named action: some triggers, and the steps to run when one of them fires.
///
/// Raises PropertyChanged so the Actions page tracks it live. The run log follows the same
/// pattern as every other component here - background code enqueues, the UI timer drains.
/// </summary>
public sealed class ActionDefinition : INotifyPropertyChanged
{
    private const int LogCapacity = 12_000;

    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly StringBuilder _log = new();

    private string _name = "New action";
    private string _group = "";
    private bool _enabled = true;
    private int _runs;
    private DateTime? _lastRun;
    private bool _running;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name
    {
        get => _name;
        set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? "Untitled action" : value.Trim(), nameof(Name));
    }

    public string Group
    {
        get => _group;
        set => Set(ref _group, (value ?? "").Trim(), nameof(Group), nameof(GroupVisibility));
    }

    /// <summary>A disabled action keeps its triggers but never fires. Test still works.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value, nameof(Enabled));
    }

    public List<TriggerDefinition> Triggers { get; set; } = new();
    public List<SubActionDefinition> SubActions { get; set; } = new();

    /// <summary>Raised when a saved field changes, so the engine can write actions.json.</summary>
    public event Action? Changed;

    public void MarkChanged()
    {
        Notify(nameof(TriggerSummary), nameof(StepSummary));
        Changed?.Invoke();
    }

    // ---- what the card shows ----

    [JsonIgnore]
    public string TriggerSummary => Triggers.Count == 0
        ? "No trigger"
        : string.Join("  ·  ", Triggers.Select(t => t.Block?.Describe(t) ?? t.Type));

    [JsonIgnore]
    public string StepSummary => SubActions.Count switch
    {
        0 => "No steps yet",
        1 => "1 step",
        _ => $"{SubActions.Count} steps"
    };

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility GroupVisibility => string.IsNullOrEmpty(_group)
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    [JsonIgnore]
    public string LastRunText => _lastRun is null
        ? "Never run"
        : $"Last run {_lastRun:HH:mm:ss} · {_runs} time{(_runs == 1 ? "" : "s")}";

    /// <summary>True while a run is in flight, so the card can show it and Stop can appear.</summary>
    [JsonIgnore]
    public bool IsRunning
    {
        get => _running;
        set => Set(ref _running, value, nameof(IsRunning), nameof(RunningVisibility), nameof(TestText));
    }

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility RunningVisibility => _running
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    [JsonIgnore] public string TestText => _running ? "Running…" : "Test";

    [JsonIgnore] public string LogText => _log.ToString();

    public void RecordRun()
    {
        _runs++;
        _lastRun = DateTime.Now;
        Notify(nameof(LastRunText));
    }

    /// <summary>Safe from any thread; drained by the UI timer like every other log here.</summary>
    public void Log(string line) => _incoming.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");

    public void ClearLog()
    {
        _log.Clear();
        Notify(nameof(LogText));
    }

    public void FlushLog()
    {
        if (_incoming.IsEmpty) return;

        while (_incoming.TryDequeue(out var line)) _log.Append(line).Append('\n');
        if (_log.Length > LogCapacity) _log.Remove(0, _log.Length - LogCapacity);
        Notify(nameof(LogText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (Equals(field, value)) return;
        field = value;
        Notify(names);
        Changed?.Invoke();
    }

    public void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

// ---------------------------------------------------------------------------
//  Registry
// ---------------------------------------------------------------------------

/// <summary>
/// Every block the app knows about, looked up by the type string stored in actions.json.
///
/// Adding a block means adding it to one of these lists - the picker, the editor and the
/// executor all read from here, so nothing else needs to change.
/// </summary>
public static class Blocks
{
    public static IReadOnlyList<ITriggerBlock> Triggers { get; } = new ITriggerBlock[]
    {
        new CommandTrigger(),
        new ChatMessageTrigger(),
        new HotkeyTrigger(),
        new MouseButtonTrigger(),
        new MouseRegionTrigger(),
        new TimerTrigger(),
        new ServerStateTrigger(),
        new ObsStateTrigger(),
        new StreamlabsStateTrigger(),
        new DoActionTrigger()
    };

    public static IReadOnlyList<ISubActionBlock> SubActions { get; } = new ISubActionBlock[]
    {
        // Core
        new DelaySubAction(),
        new RandomDelaySubAction(),
        new SetArgumentSubAction(),
        new ConditionSubAction(),
        new RunActionSubAction(),
        new CommentSubAction(),
        // OBS
        new ObsSceneSubAction(),
        new ObsSourceSubAction(),
        new ObsMoveSourceSubAction(),
        new ObsFlipSourceSubAction(),
        new ObsFilterSubAction(),
        new ObsRawSubAction(),
        // Streamlabs
        new StreamlabsSceneSubAction(),
        new StreamlabsSourceSubAction(),
        new StreamlabsMoveSourceSubAction(),
        new StreamlabsFlipSourceSubAction(),
        new StreamlabsMuteSubAction(),
        new StreamlabsRawSubAction(),
        // Discord
        new DiscordSendSubAction(),
        new DiscordEmbedSubAction(),
        new DiscordWebhookSubAction(),
        // Overlays
        new BroadcastSubAction(),
        // Servers
        new ServerControlSubAction(),
        // System
        new HttpRequestSubAction(),
        new RunProgramSubAction(),
        new PlaySoundSubAction(),
        new LogSubAction()
    };

    public static ITriggerBlock? Trigger(string type)
        => Triggers.FirstOrDefault(t => t.Type == type);

    public static ISubActionBlock? SubAction(string type)
        => SubActions.FirstOrDefault(s => s.Type == type);

    /// <summary>Block categories in the order the "Add" menu should show them.</summary>
    public static IEnumerable<string> Categories(IEnumerable<IBlock> blocks)
        => blocks.Select(b => b.Category).Distinct();

    /// <summary>
    /// The colour a category's chip gets. Kept in one place rather than on each block, since
    /// two blocks in the same category should never disagree about it.
    /// </summary>
    public static Windows.UI.Color Accent(string category) => category switch
    {
        "Chat" => Windows.UI.Color.FromArgb(255, 0x94, 0x43, 0xCC),      // brand purple
        "Time" => Windows.UI.Color.FromArgb(255, 0xE3, 0xB3, 0x41),
        "Input" => Windows.UI.Color.FromArgb(255, 0xC0, 0x6B, 0xE8),
        "Screen" => Windows.UI.Color.FromArgb(255, 0xE0, 0x7A, 0x3C),
        "Servers" => Windows.UI.Color.FromArgb(255, 0x4A, 0x9E, 0xFF),
        "External" => Windows.UI.Color.FromArgb(255, 0x00, 0xC2, 0xA8),
        "OBS" => Windows.UI.Color.FromArgb(255, 0x36, 0xD7, 0x67),       // brand green
        "Streamlabs" => Windows.UI.Color.FromArgb(255, 0x31, 0xC3, 0xA2),
        "Discord" => Windows.UI.Color.FromArgb(255, 0x58, 0x65, 0xF2),   // blurple
        "Overlays" => Windows.UI.Color.FromArgb(255, 0xFF, 0x6B, 0x9D),
        "System" => Windows.UI.Color.FromArgb(255, 0x7A, 0x7A, 0x80),
        _ => Windows.UI.Color.FromArgb(255, 0x8E, 0x8E, 0x93)            // Core
    };
}
