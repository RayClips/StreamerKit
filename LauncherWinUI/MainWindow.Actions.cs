using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace StreamerKit;

/// <summary>
/// The Actions page, and the bridge between the engine and the rest of the app.
///
/// Block editors are built here in code rather than in XAML on purpose: every block declares
/// its own fields, so one routine draws all of them and a block added later needs no markup.
/// </summary>
public sealed partial class MainWindow : IActionServices
{
    /// <summary>One client for every HTTP step, so actions don't exhaust sockets.</summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private ActionEngine _actions = null!;
    private ActionDefinition? _openAction;
    private MediaPlayer? _player;

    /// <summary>
    /// What GetActions answers with, rebuilt on the UI thread whenever the list changes.
    /// The WebSocket server reads it from its own thread, so it can't walk the live
    /// collection - it gets a copy of this instead.
    /// </summary>
    private JsonArray _actionSnapshot = new();

    private void InitializeActions()
    {
        _actions = new ActionEngine(this, Path.Combine(AppContext.BaseDirectory, "actions.json"));
        _actions.Logged += line => WebSocket?.LogExternal($"Actions: {line}");
        _actions.Changed += OnActionsChanged;
        _actions.Load();
        _actionSnapshot = _actions.Describe();

        if (WebSocket is { } server)
        {
            // A fresh copy per call: assigning a JsonNode that already has a parent throws.
            server.Actions = () => _actionSnapshot.DeepClone().AsArray();
            server.RunAction = RunFromProtocol;
        }

        ActionList.ItemsSource = _actions.Actions;
        AddTriggerButton.Flyout = BuildBlockMenu(Blocks.Triggers, AddTrigger);
        AddStepButton.Flyout = BuildBlockMenu(Blocks.SubActions, AddStep);

        UpdateActionEmptyState();
        WatchServersForTriggers();

        // Both fire on a background read loop. Dispatch marshals to the UI thread itself,
        // so these stay thin.
        _obs.Event += DispatchObsOutput;
        _streamlabs.Event += DispatchStreamlabsOutput;
    }

    private void OnActionsChanged()
    {
        _actionSnapshot = _actions.Describe();
        UpdateActionEmptyState();
    }

    private void UpdateActionEmptyState()
        => NoActions.Visibility = _actions.Actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- what blocks are allowed to reach ----------------

    ObsClient IActionServices.Obs => _obs;

    StreamlabsClient IActionServices.Streamlabs => _streamlabs;

    DiscordClient IActionServices.Discord => _discord;

    HttpClient IActionServices.Http => SharedHttp;

    async Task IActionServices.Broadcast(string source, string type, JsonObject data)
    {
        if (WebSocket is not { } server)
            throw new InvalidOperationException("there is no WebSocket server to broadcast on");

        // Same reasoning as connecting a chat source: a broadcast with nowhere to go is
        // not what the user meant, so bring the server up rather than quietly doing nothing.
        if (server.State is ServerState.Stopped or ServerState.Failed) server.Start();

        await server.BroadcastEvent(source, type, data);
    }

    IReadOnlyList<string> IActionServices.ServerNames =>
        _servers.Select(s => s.Name).Concat(_hosted.Select(h => h.Name)).ToList();

    bool IActionServices.ControlServer(string name, string command)
    {
        if (_servers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            is { } process)
        {
            switch (command)
            {
                case "stop": process.Stop(); break;
                case "restart": process.StopAndWait(3000); process.Start(); break;
                default: process.Start(); break;
            }
            return true;
        }

        if (_hosted.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            is { } hosted)
        {
            switch (command)
            {
                case "stop": hosted.Stop(); break;
                case "restart": hosted.Stop(); hosted.Start(); break;
                default: hosted.Start(); break;
            }
            return true;
        }

        return false;
    }

    void IActionServices.PlaySound(string path, double volume) => DispatcherQueue.TryEnqueue(() =>
    {
        try
        {
            _player ??= new MediaPlayer();
            _player.Volume = volume;
            _player.Source = MediaSource.CreateFromUri(new Uri(path));
            _player.Play();
        }
        catch (Exception ex)
        {
            _openAction?.Log($"Could not play {Path.GetFileName(path)}: {ex.Message}");
        }
    });

    // ---------------- feeding the engine ----------------

    /// <summary>Turns a chat message into the arguments a chat trigger matches against.</summary>
    private void DispatchChat(string source, JsonObject data)
    {
        var user = data["user"]?.AsObject();
        _actions.Dispatch(new TriggerEvent(TriggerKind.Chat, new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = source,
            ["message"] = data["text"]?.ToString() ?? "",
            ["messageId"] = data["messageId"]?.ToString() ?? "",
            ["user"] = user?["login"]?.ToString() ?? "",
            ["userName"] = user?["name"]?.ToString() ?? "",
            ["userId"] = user?["id"]?.ToString() ?? "",
            ["userColor"] = user?["color"]?.ToString() ?? "",
            ["role"] = user?["role"]?.ToString() ?? "1"
        }));
    }

    // ---------------- "the stream just started" ----------------
    //
    // OBS and Streamlabs both push this, and describe it completely differently. Flattening
    // happens here rather than in the trigger so the block compares three plain strings and
    // never has to know which program it came from.

    /// <summary>
    /// OBS: an event type naming the output, and a state that distinguishes settled from
    /// transitional. Only the settled ones are passed on - STARTING and STOPPING would fire
    /// the action a second time, and the first time before the stream was actually up.
    /// </summary>
    private void DispatchObsOutput(string eventType, JsonNode? data)
    {
        var output = eventType switch
        {
            "StreamStateChanged" => "streaming",
            "RecordStateChanged" => "recording",
            "ReplayBufferStateChanged" => "replaybuffer",
            "VirtualcamStateChanged" => "virtualcam",
            _ => ""
        };
        if (output.Length == 0) return;

        var settled = data?["outputState"]?.ToString() ?? "";
        var state = settled.EndsWith("_STARTED", StringComparison.Ordinal) ? "started"
                  : settled.EndsWith("_STOPPED", StringComparison.Ordinal) ? "stopped"
                  : "";
        if (state.Length == 0) return;

        DispatchOutput("obs", output, state);
    }

    /// <summary>
    /// Streamlabs: a resource id naming the output, and a bare status word as the payload.
    /// The vocabulary differs per output - streaming goes "live", recording goes "recording",
    /// the replay buffer goes "running" - so the settled words are matched as a set.
    /// </summary>
    private void DispatchStreamlabsOutput(string resource, JsonNode? data)
    {
        var output = resource switch
        {
            var r when r.EndsWith("streamingStatusChange", StringComparison.OrdinalIgnoreCase) => "streaming",
            var r when r.EndsWith("recordingStatusChange", StringComparison.OrdinalIgnoreCase) => "recording",
            var r when r.EndsWith("replayBufferStatusChange", StringComparison.OrdinalIgnoreCase) => "replaybuffer",
            _ => ""
        };
        if (output.Length == 0) return;

        var status = (data?.GetValueKind() == JsonValueKind.String ? data.ToString() : "").ToLowerInvariant();
        var state = status switch
        {
            "live" or "recording" or "running" => "started",
            "offline" => "stopped",
            _ => ""                                  // starting, ending, stopping, saving
        };
        if (state.Length == 0) return;

        DispatchOutput("streamlabs", output, state);
    }

    private void DispatchOutput(string source, string output, string state)
        => _actions.Dispatch(new TriggerEvent(TriggerKind.Output,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = source,
                ["output"] = output,
                ["state"] = state
            }));

    /// <summary>
    /// Server state changes, mapped to the four cases a trigger can ask for. Restarting is
    /// the crash signal - it's the state a server enters the moment it falls over.
    /// </summary>
    private void WatchServersForTriggers()
    {
        foreach (var server in _servers)
        {
            var watched = server;
            watched.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ServerProcess.State)) return;

                var state = watched.State switch
                {
                    ServerState.Running => "started",
                    ServerState.Stopped => "stopped",
                    ServerState.Restarting => "crashed",
                    ServerState.Failed => "gaveup",
                    _ => null
                };
                if (state is null) return;

                _actions.Dispatch(new TriggerEvent(TriggerKind.Server,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["serverName"] = watched.Name,
                        ["serverState"] = state
                    }));
            };
        }
    }

    /// <summary>
    /// Entry point for a DoAction request. The receive loop that calls this is not the UI
    /// thread and the engine is, so the work is marshalled and the answer awaited.
    /// </summary>
    private Task<string?> RunFromProtocol(string nameOrId, Dictionary<string, string> arguments)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!DispatcherQueue.TryEnqueue(() => answer.TrySetResult(RunFromProtocolCore(nameOrId, arguments))))
            answer.TrySetResult("StreamerKit is closing.");

        return answer.Task;
    }

    /// <summary>
    /// Only actions that opted in with a "Called over WebSocket" trigger are reachable -
    /// see <see cref="DoActionTrigger"/>. Returns null if it started, else why it didn't.
    /// </summary>
    private string? RunFromProtocolCore(string nameOrId, Dictionary<string, string> arguments)
    {
        var action = _actions.Find(nameOrId);
        if (action is null) return $"No action called '{nameOrId}'.";

        if (!action.Triggers.Any(t => t.Enabled && t.Type == DoActionTrigger.TypeName))
            return $"'{action.Name}' is not exposed. Add a \"Called over WebSocket\" trigger to it.";

        if (!action.Enabled) return $"'{action.Name}' is turned off.";

        _ = _actions.Run(action, arguments, "Called over WebSocket");
        return null;
    }

    // ---------------- list ----------------

    private void OnNewAction(object sender, RoutedEventArgs e) => ShowAction(_actions.Add());

    private async void OnExportActions(object sender, RoutedEventArgs e)
    {
        if (_actions.Actions.Count == 0)
        {
            await Tell("Nothing to export", "There are no actions yet.");
            return;
        }

        try
        {
            var path = await Native.SaveFileDialog(WinRT.Interop.WindowNative.GetWindowHandle(this),
                                             "Export actions", "streamerkit-actions.json");
            if (path is null) return;

            var count = _actions.ExportTo(path);
            await Tell("Exported", $"{Count(count, "action")} written to {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            await Tell("Could not export", ex.Message);
        }
    }

    private async void OnImportActions(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await Native.OpenFileDialog(WinRT.Interop.WindowNative.GetWindowHandle(this),
                                             "Import actions");
            if (path is null) return;

            var (added, skipped) = _actions.ImportFrom(path);

            var message = added == 0
                ? "Nothing in that file could be used by this build."
                : $"{Count(added, "action")} added, switched off so you can look before turning them on.";

            if (skipped > 0)
                message += $" {Count(skipped, "action")} skipped, with no steps this build understands.";

            await Tell(added == 0 ? "Nothing imported" : "Imported", message);
        }
        catch (System.Text.Json.JsonException ex)
        {
            await Tell("Could not import", $"That file isn't valid JSON — {ex.Message}");
        }
        catch (Exception ex)
        {
            await Tell("Could not import", ex.Message);
        }
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private async Task Tell(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        }.ShowAsync();
    }

    private void OnTestAction(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ActionDefinition action)
            _ = _actions.Run(action, null, "Test");
    }

    private void OnOpenAction(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ActionDefinition action) ShowAction(action);
    }

    private void OnBackToActions(object sender, RoutedEventArgs e) => ShowActionList();

    private void ShowAction(ActionDefinition action)
    {
        _openAction = action;
        ActionDetailPanel.DataContext = action;
        RebuildBlocks();

        ActionsPanel.Visibility = Visibility.Collapsed;
        ActionDetailPanel.Visibility = Visibility.Visible;
    }

    private void ShowActionList()
    {
        _openAction = null;
        ActionDetailPanel.Visibility = Visibility.Collapsed;
        ActionsPanel.Visibility = Visibility.Visible;
        UpdateActionEmptyState();
    }

    private void OnDuplicateAction(object sender, RoutedEventArgs e)
    {
        if (_openAction is { } action) ShowAction(_actions.Duplicate(action));
    }

    private void OnClearActionLog(object sender, RoutedEventArgs e) => _openAction?.ClearLog();

    private async void OnDeleteAction(object sender, RoutedEventArgs e)
    {
        if (_openAction is not { } action) return;

        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Delete \"{action.Name}\"?",
            Content = "This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        _actions.Remove(action);
        ShowActionList();
    }

    // ---------------- blocks ----------------

    private void RebuildBlocks()
    {
        TriggerBlocks.Children.Clear();
        StepBlocks.Children.Clear();
        if (_openAction is not { } action) return;

        foreach (var trigger in action.Triggers.ToList())
            TriggerBlocks.Children.Add(BuildTriggerBlock(action, trigger));

        foreach (var step in action.SubActions.ToList())
            StepBlocks.Children.Add(BuildStepBlock(action, step));

        NoTriggers.Visibility = action.Triggers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoSteps.Visibility = action.SubActions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildTriggerBlock(ActionDefinition action, TriggerDefinition trigger)
    {
        var block = trigger.Block!;
        return BuildBlockCard(block, trigger.Config, trigger.Enabled,
            summary: () => block.Describe(trigger),
            onEnabled: value => { trigger.Enabled = value; action.MarkChanged(); },
            onMove: offset => Move(action.Triggers, trigger, offset),
            onRemove: () => { action.Triggers.Remove(trigger); Commit(action); },
            onCommit: () => action.MarkChanged());
    }

    private FrameworkElement BuildStepBlock(ActionDefinition action, SubActionDefinition step)
    {
        var block = step.Block!;
        return BuildBlockCard(block, step.Config, step.Enabled,
            summary: () => block.Describe(step),
            onEnabled: value => { step.Enabled = value; action.MarkChanged(); },
            onMove: offset => Move(action.SubActions, step, offset),
            onRemove: () => { action.SubActions.Remove(step); Commit(action); },
            onCommit: () => action.MarkChanged());
    }

    /// <summary>
    /// One block: a coloured category chip and its summary in the header, the generated
    /// fields and the row of block controls inside.
    ///
    /// The controls live in the content rather than the header because an Expander header
    /// swallows clicks for its own toggle, and a Remove button that sometimes collapses the
    /// block instead of removing it is worse than no Remove button.
    /// </summary>
    private FrameworkElement BuildBlockCard(IBlock block, Dictionary<string, string> config, bool enabled,
                                            Func<string> summary, Action<bool> onEnabled,
                                            Action<int> onMove, Action onRemove, Action onCommit)
    {
        var summaryText = new TextBlock
        {
            Text = summary(),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };

        var header = new Grid { ColumnSpacing = 10, Opacity = enabled ? 1 : 0.5 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Width = 4,
            MinHeight = 30,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Blocks.Accent(block.Category))
        };
        Grid.SetColumn(chip, 0);
        header.Children.Add(chip);

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = block.Label,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });
        titles.Children.Add(summaryText);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(0, 4, 0, 0) };

        if (block.Fields.Count > 0)
            body.Children.Add(BuildFields(block.Fields, config,
                onEdit: () => summaryText.Text = summary(), onCommit: onCommit));
        else
            body.Children.Add(new TextBlock
            {
                Text = block.Description,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });

        body.Children.Add(BuildBlockControls(enabled, header, onEnabled, onMove, onRemove));

        return new Expander
        {
            Header = header,
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private StackPanel BuildBlockControls(bool enabled, Grid header, Action<bool> onEnabled,
                                          Action<int> onMove, Action onRemove)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var toggle = new CheckBox { Content = "Enabled", IsChecked = enabled, MinWidth = 0 };
        toggle.Checked += (_, _) => { header.Opacity = 1; onEnabled(true); };
        toggle.Unchecked += (_, _) => { header.Opacity = 0.5; onEnabled(false); };
        row.Children.Add(toggle);

        row.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(6, 4, 6, 4),
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
        });

        row.Children.Add(IconButton("", "Move up", () => onMove(-1)));
        row.Children.Add(IconButton("", "Move down", () => onMove(1)));
        row.Children.Add(IconButton("", "Remove", onRemove));

        return row;
    }

    private static Button IconButton(string glyph, string tooltip, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
            Padding = new Thickness(9, 5, 9, 5)
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => click();
        return button;
    }

    private void Move<T>(List<T> list, T item, int offset)
    {
        var from = list.IndexOf(item);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= list.Count) return;

        list.RemoveAt(from);
        list.Insert(to, item);
        if (_openAction is { } action) Commit(action);
    }

    private void Commit(ActionDefinition action)
    {
        action.MarkChanged();
        RebuildBlocks();
    }

    // ---------------- generated field editors ----------------

    private StackPanel BuildFields(IReadOnlyList<FieldSpec> fields, Dictionary<string, string> config,
                                   Action onEdit, Action onCommit)
    {
        var panel = new StackPanel { Spacing = 12 };

        foreach (var field in fields)
        {
            if (!config.TryGetValue(field.Key, out var current)) config[field.Key] = current = field.Default;

            var group = new StackPanel { Spacing = 4 };
            group.Children.Add(new TextBlock
            {
                Text = field.Label,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });

            group.Children.Add(BuildEditor(field, current, value =>
            {
                config[field.Key] = value;
                onEdit();
            }, onCommit));

            if (field.Hint is { Length: > 0 } hint)
            {
                group.Children.Add(new TextBlock
                {
                    Text = hint,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                });
            }

            panel.Children.Add(group);
        }

        return panel;
    }

    /// <summary>
    /// Typing writes straight into the config so the summary keeps up, but only commits -
    /// and so only writes actions.json - when the field is left. Everything else commits
    /// immediately, since one click is already the whole edit.
    /// </summary>
    private FrameworkElement BuildEditor(FieldSpec field, string current, Action<string> onEdit, Action onCommit)
    {
        switch (field.Kind)
        {
            case FieldKind.Number:
            {
                var box = new NumberBox
                {
                    Value = double.TryParse(current, out var value) ? value : 0,
                    Minimum = field.Minimum,
                    Maximum = field.Maximum,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MinWidth = 180
                };
                box.ValueChanged += (_, _) =>
                {
                    if (double.IsNaN(box.Value)) return;
                    onEdit(((long)box.Value).ToString());
                    onCommit();
                };
                return box;
            }

            case FieldKind.Toggle:
            {
                var toggle = new ToggleSwitch
                {
                    IsOn = bool.TryParse(current, out var on) && on,
                    OnContent = "Yes",
                    OffContent = "No",
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                toggle.Toggled += (_, _) =>
                {
                    onEdit(toggle.IsOn ? "true" : "false");
                    onCommit();
                };
                return toggle;
            }

            case FieldKind.Choice:
            {
                var choices = ResolveChoices(field);
                var box = new ComboBox
                {
                    MinWidth = 240,
                    PlaceholderText = "Pick one",
                    ItemsSource = choices.Select(c => c.Label).ToList()
                };

                var index = choices.ToList().FindIndex(c => c.Value == current);
                if (index >= 0) box.SelectedIndex = index;

                box.SelectionChanged += (_, _) =>
                {
                    if (box.SelectedIndex < 0 || box.SelectedIndex >= choices.Count) return;
                    onEdit(choices[box.SelectedIndex].Value);
                    onCommit();
                };
                return box;
            }

            case FieldKind.MultiChoice:
            {
                var choices = ResolveChoices(field);
                var picked = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
                foreach (var choice in choices)
                {
                    var check = new CheckBox { Content = choice.Label, IsChecked = picked.Contains(choice.Value) };
                    var value = choice.Value;

                    void Apply(bool on)
                    {
                        if (on) picked.Add(value); else picked.Remove(value);
                        onEdit(string.Join(",", choices.Select(c => c.Value).Where(picked.Contains)));
                        onCommit();
                    }

                    check.Checked += (_, _) => Apply(true);
                    check.Unchecked += (_, _) => Apply(false);
                    row.Children.Add(check);
                }
                return row;
            }

            case FieldKind.File:
            {
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var box = new TextBox { Text = current, PlaceholderText = "Path to a file" };
                box.TextChanged += (_, _) => onEdit(box.Text);
                box.LostFocus += (_, _) => onCommit();
                Grid.SetColumn(box, 0);
                grid.Children.Add(box);

                var browse = new Button { Content = "Browse" };
                browse.Click += async (_, _) =>
                {
                    var picked = await PickFile();
                    if (picked is null) return;
                    box.Text = picked;      // TextChanged writes it through
                    onCommit();
                };
                Grid.SetColumn(browse, 1);
                grid.Children.Add(browse);
                return grid;
            }

            case FieldKind.LongText:
            case FieldKind.Code:
            {
                var box = new TextBox
                {
                    Text = current,
                    AcceptsReturn = true,
                    TextWrapping = field.Kind == FieldKind.Code ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    MinHeight = field.Kind == FieldKind.Code ? 200 : 90,
                    MaxHeight = 320,
                    IsSpellCheckEnabled = false
                };
                if (field.Kind == FieldKind.Code) box.FontFamily = new FontFamily("Consolas");

                box.TextChanged += (_, _) => onEdit(box.Text);
                box.LostFocus += (_, _) => onCommit();
                return box;
            }

            default:
            {
                var box = new TextBox { Text = current, IsSpellCheckEnabled = false };
                box.TextChanged += (_, _) => onEdit(box.Text);
                box.LostFocus += (_, _) => onCommit();
                return box;
            }
        }
    }

    /// <summary>Choice lists that only exist once the app is running.</summary>
    private IReadOnlyList<FieldChoice> ResolveChoices(FieldSpec field) => field.Dynamic switch
    {
        DynamicChoices.Servers => ((IActionServices)this).ServerNames
            .Select(name => new FieldChoice(name, name)).ToList(),

        // An action can't call itself, so it isn't offered.
        DynamicChoices.Actions => _actions.Actions.Where(a => a != _openAction)
            .Select(a => new FieldChoice(a.Name, a.Name)).ToList(),

        _ => field.Choices ?? Array.Empty<FieldChoice>()
    };

    private async Task<string?> PickFile()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");

        // Unpackaged apps have to tell the picker which window owns it, or it never appears.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    // ---------------- adding blocks ----------------

    private MenuFlyout BuildBlockMenu<T>(IReadOnlyList<T> blocks, Action<string> pick) where T : IBlock
    {
        var flyout = new MenuFlyout();

        foreach (var category in blocks.Select(b => b.Category).Distinct())
        {
            var group = new MenuFlyoutSubItem { Text = category };
            foreach (var block in blocks.Where(b => b.Category == category))
            {
                var item = new MenuFlyoutItem { Text = block.Label };
                ToolTipService.SetToolTip(item, block.Description);

                var type = block.Type;
                item.Click += (_, _) => pick(type);
                group.Items.Add(item);
            }
            flyout.Items.Add(group);
        }

        return flyout;
    }

    private void AddTrigger(string type)
    {
        if (_openAction is not { } action || Blocks.Trigger(type) is not { } block) return;

        var trigger = new TriggerDefinition { Type = type };
        trigger.Config.ApplyDefaults(block.Fields);
        action.Triggers.Add(trigger);
        Commit(action);
    }

    private void AddStep(string type)
    {
        if (_openAction is not { } action || Blocks.SubAction(type) is not { } block) return;

        var step = new SubActionDefinition { Type = type };
        step.Config.ApplyDefaults(block.Fields);
        action.SubActions.Add(step);
        Commit(action);
    }
}
