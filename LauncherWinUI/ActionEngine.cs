using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;

namespace StreamerKit;

/// <summary>
/// Everything a sub-action is allowed to reach. Blocks talk to the app only through this,
/// which is what keeps the engine independent of the window - and what lets a future
/// scripted block be handed exactly the same surface without reshaping anything.
/// </summary>
public interface IActionServices
{
    ObsClient Obs { get; }

    StreamlabsClient Streamlabs { get; }

    DiscordClient Discord { get; }

    /// <summary>Pushes a Streamer.bot-shaped event to every connected overlay.</summary>
    Task Broadcast(string source, string type, JsonObject data);

    /// <summary>Names of every server on the Servers page, for the block's dropdown.</summary>
    IReadOnlyList<string> ServerNames { get; }

    /// <summary>"start", "stop" or "restart" on a server by name. False if there's no such server.</summary>
    bool ControlServer(string name, string command);

    HttpClient Http { get; }

    /// <summary>Plays a sound file. Kept here because it needs a UI-thread media player.</summary>
    void PlaySound(string path, double volume);
}

/// <summary>
/// The state one run carries: the arguments, where to log, what may be reached, and the
/// token that ends it. Passed to every sub-action in turn.
/// </summary>
public sealed class ActionContext
{
    private readonly ActionEngine _engine;

    internal ActionContext(ActionEngine engine, ActionDefinition action, IActionServices services,
                           Dictionary<string, string> arguments, CancellationToken token, int depth)
    {
        _engine = engine;
        Action = action;
        Services = services;
        Arguments = arguments;
        Token = token;
        Depth = depth;
    }

    public ActionDefinition Action { get; }
    public IActionServices Services { get; }
    public Dictionary<string, string> Arguments { get; }
    public CancellationToken Token { get; }

    /// <summary>How many "Run action" hops deep this is, so a loop between two actions ends.</summary>
    public int Depth { get; }

    /// <summary>Set by a step that decides the rest of the action shouldn't run.</summary>
    public bool Stopped { get; set; }

    public void Log(string line) => Action.Log(line);

    /// <summary>
    /// Replaces %name% with the argument of that name. An unknown name is left alone rather
    /// than blanked, so a typo is visible in the log instead of silently vanishing.
    /// </summary>
    public string Expand(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('%')) return text ?? "";

        var result = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('%', index);
            if (open < 0) { result.Append(text, index, text.Length - index); break; }

            var close = text.IndexOf('%', open + 1);
            if (close < 0) { result.Append(text, index, text.Length - index); break; }

            result.Append(text, index, open - index);
            var name = text[(open + 1)..close];

            if (Arguments.TryGetValue(name, out var value)) result.Append(value);
            else result.Append('%').Append(name).Append('%');

            index = close + 1;
        }
        return result.ToString();
    }

    /// <summary>Reads a field with %arguments% already substituted.</summary>
    public string Field(SubActionDefinition definition, string key, string fallback = "")
        => Expand(definition.Config.Get(key, fallback));

    /// <summary>Lets the "Run action" block hand off without the engine being a global.</summary>
    internal Task RunNested(string nameOrId) => _engine.RunByName(nameOrId, Arguments, $"called by {Action.Name}", Depth + 1);
}

/// <summary>
/// Holds the actions, decides which ones a trigger event wakes up, and runs their steps
/// in order.
///
/// Everything public here is called on the UI thread; <see cref="Dispatch"/> marshals for
/// callers that aren't (the WebSocket server, mainly). Steps themselves are async and run
/// off the UI thread as soon as they hit real work.
/// </summary>
public sealed class ActionEngine
{
    /// <summary>An action calling an action calling an action. Past this, something is wrong.</summary>
    private const int MaxDepth = 5;

    private readonly IActionServices _services;
    private readonly DispatcherQueue _ui;
    private readonly Dictionary<string, SemaphoreSlim> _gates = new();
    private readonly string _path;

    private CancellationTokenSource _cancellation = new();

    // Held in a field, or it is collected and silently stops ticking.
    private DispatcherQueueTimer? _input;
    private int _lastMouseX = int.MinValue;
    private int _lastMouseY = int.MinValue;

    public ActionEngine(IActionServices services, string path)
    {
        _services = services;
        _path = path;
        _ui = DispatcherQueue.GetForCurrentThread();
    }

    public ObservableCollection<ActionDefinition> Actions { get; } = new();

    /// <summary>Raised for anything worth putting in a shared log.</summary>
    public event Action<string>? Logged;

    /// <summary>Raised when an action is added, removed or edited, so the page can refresh.</summary>
    public event Action? Changed;

    // ---------------- persistence ----------------

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public void Load()
    {
        Actions.Clear();
        if (!File.Exists(_path)) return;

        List<ActionDefinition>? loaded = null;
        try
        {
            loaded = JsonSerializer.Deserialize<List<ActionDefinition>>(File.ReadAllText(_path), Options);
        }
        catch (JsonException ex)
        {
            // An unreadable file shouldn't cost the user their other pages.
            Logged?.Invoke($"actions.json could not be read: {ex.Message}");
            return;
        }

        foreach (var action in loaded ?? new List<ActionDefinition>())
        {
            // Drop anything referring to a block this build doesn't have, rather than
            // keeping a step that would silently do nothing when the action runs.
            action.Triggers.RemoveAll(t => t.Block is null);
            action.SubActions.RemoveAll(s => s.Block is null);
            Attach(action);
            Actions.Add(action);
        }
        ScheduleTimers();
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Actions, Options)); }
        catch (Exception ex) { Logged?.Invoke($"Could not save actions.json: {ex.Message}"); }
    }

    private void Attach(ActionDefinition action)
    {
        action.Changed += () =>
        {
            ScheduleTimers();
            Save();
            Changed?.Invoke();
        };
    }

    public ActionDefinition Add()
    {
        var action = new ActionDefinition { Name = $"New action {Actions.Count + 1}", Enabled = false };
        Attach(action);
        Actions.Add(action);
        Save();
        Changed?.Invoke();
        return action;
    }

    public void Remove(ActionDefinition action)
    {
        Actions.Remove(action);
        Save();
        EnsureInputWatcher();
        Changed?.Invoke();
    }

    /// <summary>
    /// Writes every action to a file of the user's choosing, in the same shape as
    /// actions.json - so an exported file can also be dropped in as one.
    /// </summary>
    public int ExportTo(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(Actions, Options));
        return Actions.Count;
    }

    /// <summary>
    /// Adds the actions in a file to the list. Deliberately additive: importing never
    /// replaces what is already there, because losing a page of work to a mis-clicked file
    /// is not recoverable.
    ///
    /// Everything arrives switched off. An imported action can hold a global hotkey or move
    /// something in OBS, and that should be a decision the user makes after reading it, not
    /// a side effect of opening a file.
    /// </summary>
    /// <returns>How many were added, and how many were skipped as unusable.</returns>
    public (int Added, int Skipped) ImportFrom(string path)
    {
        var loaded = JsonSerializer.Deserialize<List<ActionDefinition>>(File.ReadAllText(path), Options)
                     ?? new List<ActionDefinition>();

        int added = 0, skipped = 0;
        foreach (var action in loaded)
        {
            // Same rule as Load: drop blocks this build doesn't have rather than keep a step
            // that would silently do nothing.
            action.Triggers.RemoveAll(t => t.Block is null);
            action.SubActions.RemoveAll(s => s.Block is null);

            if (action.SubActions.Count == 0) { skipped++; continue; }

            action.Id = Guid.NewGuid().ToString("N")[..12];   // never collide with one we already have
            action.Name = UniqueName(action.Name);
            action.Enabled = false;

            Attach(action);
            Actions.Add(action);
            added++;
        }

        if (added > 0)
        {
            Save();
            Changed?.Invoke();
        }
        return (added, skipped);
    }

    /// <summary>Keeps two actions from sharing a name, since DoAction and "Run action" look up by it.</summary>
    private string UniqueName(string wanted)
    {
        if (Actions.All(a => !string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            return wanted;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{wanted} ({suffix})";
            if (Actions.All(a => !string.Equals(a.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    public ActionDefinition Duplicate(ActionDefinition source)
    {
        var copy = new ActionDefinition
        {
            Name = $"{source.Name} (copy)",
            Group = source.Group,
            Enabled = false,
            SubActions = source.SubActions.Select(s => s.Clone()).ToList(),
            Triggers = source.Triggers.Select(t => new TriggerDefinition
            {
                Type = t.Type,
                Enabled = t.Enabled,
                Config = new Dictionary<string, string>(t.Config)
            }).ToList()
        };
        Attach(copy);
        Actions.Add(copy);
        Save();
        Changed?.Invoke();
        return copy;
    }

    // ---------------- dispatch ----------------

    /// <summary>Feeds an event to every enabled action whose triggers match. Thread-safe.</summary>
    public void Dispatch(TriggerEvent incoming)
    {
        if (!_ui.HasThreadAccess) { _ui.TryEnqueue(() => DispatchCore(incoming)); return; }
        DispatchCore(incoming);
    }

    private void DispatchCore(TriggerEvent incoming)
    {
        // ToList: a step could add or remove an action while this runs.
        foreach (var action in Actions.ToList())
        {
            if (!action.Enabled || action.SubActions.Count == 0) continue;

            foreach (var trigger in action.Triggers)
            {
                if (!trigger.Enabled) continue;

                var block = trigger.Block;
                if (block is null || block.Listens != incoming.Kind) continue;

                var arguments = new Dictionary<string, string>(incoming.Arguments, StringComparer.OrdinalIgnoreCase);
                if (!block.Matches(trigger, incoming, arguments)) continue;

                _ = Run(action, arguments, block.Describe(trigger));
                break;   // one trigger firing is enough; don't run the action twice
            }
        }
    }

    /// <summary>
    /// Timer triggers, checked from the same UI pump that drains the logs. A timer that
    /// falls behind is not replayed - it's simply due again from now.
    /// </summary>
    public void Tick()
    {
        var now = DateTime.Now;
        foreach (var action in Actions.ToList())
        {
            if (!action.Enabled || action.SubActions.Count == 0) continue;

            foreach (var trigger in action.Triggers)
            {
                if (!trigger.Enabled || trigger.Type != TimerTrigger.TypeName) continue;

                var seconds = Math.Max(1, trigger.Config.GetInt("seconds", 300));
                if (trigger.NextDue == DateTime.MinValue) { trigger.NextDue = now.AddSeconds(seconds); continue; }
                if (now < trigger.NextDue) continue;

                trigger.NextDue = now.AddSeconds(seconds);
                _ = Run(action, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        trigger.Block?.Describe(trigger) ?? "Timer");
            }
        }
    }

    /// <summary>Puts every timer back on the clock - after a load, an edit or an enable.</summary>
    private void ScheduleTimers()
    {
        var now = DateTime.Now;
        foreach (var trigger in Actions.SelectMany(a => a.Triggers).Where(t => t.Type == TimerTrigger.TypeName))
            trigger.NextDue = now.AddSeconds(Math.Max(1, trigger.Config.GetInt("seconds", 300)));

        EnsureInputWatcher();
    }

    // ---------------- input: cursor, keys, mouse buttons ----------------

    /// <summary>Trigger types that need the input poll running.</summary>
    private static bool IsPolled(TriggerDefinition trigger)
        => trigger.Type is MouseRegionTrigger.TypeName
                        or HotkeyTrigger.TypeName
                        or MouseButtonTrigger.TypeName;

    /// <summary>
    /// Input is only polled while some enabled action actually wants it, so an app with no
    /// input triggers does no work at all. When it is running it costs one GetCursorPos plus
    /// one GetAsyncKeyState per watched key per tick, and nothing is dispatched unless
    /// something actually changed.
    /// </summary>
    private void EnsureInputWatcher()
    {
        var wanted = Actions.Any(a => a.Enabled && a.SubActions.Count > 0 &&
                                      a.Triggers.Any(t => t.Enabled && IsPolled(t)));

        if (wanted && _input is null)
        {
            _input = _ui.CreateTimer();
            _input.Interval = TimeSpan.FromMilliseconds(40);
            _input.Tick += (_, _) => PollInput();
            _input.Start();
            Logged?.Invoke("Watching the mouse and keyboard for input triggers.");
        }
        else if (!wanted && _input is not null)
        {
            _input.Stop();
            _input = null;
            _lastMouseX = _lastMouseY = int.MinValue;
            Logged?.Invoke("No input triggers left, stopped watching.");
        }
    }

    /// <summary>
    /// One cursor read serves both halves. Keys are polled even when the cursor is still,
    /// so the region check keeps its own early-out rather than short-circuiting the tick.
    /// </summary>
    private void PollInput()
    {
        var (x, y) = Native.CursorPosition();
        PollMouseRegions(x, y);
        PollKeys(x, y);
    }

    /// <summary>
    /// Key and mouse-button edges, worked out for every trigger before anything is dispatched -
    /// same reason as the regions below. DispatchCore stops at an action's first matching
    /// trigger, so a trigger that decided its own edge would leave the others in that action
    /// holding stale state and double-fire later.
    /// </summary>
    private void PollKeys(int x, int y)
    {
        var changed = false;
        foreach (var action in Actions)
        {
            var live = action.Enabled && action.SubActions.Count > 0;
            foreach (var trigger in action.Triggers)
            {
                bool down;
                switch (trigger.Type)
                {
                    case HotkeyTrigger.TypeName:
                        if (!live || !trigger.Enabled) { trigger.Edge = 0; continue; }
                        down = HotkeyTrigger.IsDown(trigger);
                        break;

                    case MouseButtonTrigger.TypeName:
                        if (!live || !trigger.Enabled) { trigger.Edge = 0; continue; }
                        down = MouseButtonTrigger.IsDown(trigger);
                        break;

                    default:
                        continue;
                }

                trigger.Edge = down == trigger.WasDown ? 0 : down ? 1 : -1;
                trigger.WasDown = down;
                if (trigger.Edge != 0) changed = true;
            }
        }

        if (!changed) return;

        // The cursor rides along so "Mouse click" can be limited to a rectangle.
        DispatchCore(new TriggerEvent(TriggerKind.Key,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mouseX"] = x.ToString(),
                ["mouseY"] = y.ToString()
            }));
    }

    private void PollMouseRegions(int x, int y)
    {
        if (x == _lastMouseX && y == _lastMouseY) return;   // a still cursor can't cross anything

        _lastMouseX = x;
        _lastMouseY = y;

        // Work out every region's edge first. Dispatch stops at an action's first matching
        // trigger, so leaving this to Matches would let one trigger starve the others.
        var crossed = false;
        foreach (var action in Actions)
        {
            var live = action.Enabled && action.SubActions.Count > 0;
            foreach (var trigger in action.Triggers)
            {
                if (trigger.Type != MouseRegionTrigger.TypeName) continue;

                if (!live || !trigger.Enabled) { trigger.Edge = 0; continue; }

                var inside = MouseRegionTrigger.Contains(trigger, x, y);
                trigger.Edge = inside == trigger.WasInside ? 0 : inside ? 1 : -1;
                trigger.WasInside = inside;
                if (trigger.Edge != 0) crossed = true;
            }
        }

        if (!crossed) return;

        DispatchCore(new TriggerEvent(TriggerKind.Mouse,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mouseX"] = x.ToString(),
                ["mouseY"] = y.ToString()
            }));
    }

    // ---------------- running ----------------

    /// <summary>Runs an action by name or id. Used by the DoAction request and the "Run action" step.</summary>
    public Task RunByName(string nameOrId, IDictionary<string, string>? seed, string reason, int depth = 0)
    {
        var action = Find(nameOrId);
        if (action is null)
        {
            Logged?.Invoke($"No action called '{nameOrId}'.");
            return Task.CompletedTask;
        }
        return Run(action, seed, reason, depth);
    }

    public ActionDefinition? Find(string nameOrId)
        => Actions.FirstOrDefault(a => a.Id == nameOrId)
        ?? Actions.FirstOrDefault(a => string.Equals(a.Name, nameOrId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs every enabled step in order. A second trigger while a run is in flight is
    /// skipped rather than queued - queueing behind a long Delay is how you end up with
    /// forty pending copies of the same action.
    /// </summary>
    public async Task Run(ActionDefinition action, IDictionary<string, string>? seed, string reason, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            action.Log($"Stopped: actions are calling each other more than {MaxDepth} deep.");
            return;
        }

        var gate = Gate(action.Id);
        if (!await gate.WaitAsync(0))
        {
            action.Log($"Skipped ({reason}): already running.");
            return;
        }

        var arguments = seed is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(seed, StringComparer.OrdinalIgnoreCase);

        arguments["actionName"] = action.Name;
        arguments["actionId"] = action.Id;

        var context = new ActionContext(this, action, _services, arguments, _cancellation.Token, depth);

        action.IsRunning = true;
        action.Log($"▶ {reason}");

        try
        {
            foreach (var step in action.SubActions)
            {
                if (context.Stopped || context.Token.IsCancellationRequested) break;
                if (!step.Enabled) continue;

                var block = step.Block;
                if (block is null) continue;

                step.Config.ApplyDefaults(block.Fields);

                try
                {
                    await block.Run(step, context);
                }
                catch (OperationCanceledException)
                {
                    action.Log("Stopped.");
                    break;
                }
                catch (Exception ex)
                {
                    // Stop rather than carry on: later steps almost always assume the
                    // earlier ones worked, and half an action is worse than none.
                    action.Log($"{block.Label} failed — {ex.Message}");
                    break;
                }
            }
            action.RecordRun();
        }
        finally
        {
            action.IsRunning = false;
            gate.Release();
        }
    }

    /// <summary>Cancels everything in flight and gives the next run a fresh token.</summary>
    public void StopAll()
    {
        _cancellation.Cancel();
        _cancellation = new CancellationTokenSource();
        Logged?.Invoke("Stopped all running actions.");
    }

    private SemaphoreSlim Gate(string id)
    {
        if (!_gates.TryGetValue(id, out var gate)) _gates[id] = gate = new SemaphoreSlim(1, 1);
        return gate;
    }

    /// <summary>Drained by the window's pump, alongside every other log in the app.</summary>
    public void FlushLogs()
    {
        foreach (var action in Actions) action.FlushLog();
    }

    /// <summary>The action list as the Streamer.bot GetActions response wants it.</summary>
    public JsonArray Describe()
    {
        var list = new JsonArray();
        foreach (var action in Actions)
        {
            list.Add(new JsonObject
            {
                ["id"] = action.Id,
                ["name"] = action.Name,
                ["group"] = action.Group,
                ["enabled"] = action.Enabled,
                ["subaction_count"] = action.SubActions.Count
            });
        }
        return list;
    }
}
