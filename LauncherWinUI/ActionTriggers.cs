using System.Text.RegularExpressions;

namespace StreamerKit;

/// <summary>
/// The kinds of event that reach <see cref="ActionEngine.Dispatch"/>. A trigger block only
/// sees events matching its <see cref="ITriggerBlock.Listens"/>.
/// </summary>
public static class TriggerKind
{
    public const string Chat = "chat";
    public const string Timer = "timer";
    public const string Server = "server";
    public const string DoAction = "doaction";
    public const string Mouse = "mouse";

    /// <summary>Streaming, recording or the replay buffer started or stopped, in OBS or Streamlabs.</summary>
    public const string Output = "output";

    /// <summary>A key or a mouse button changed state. Both are virtual keys.</summary>
    public const string Key = "key";
}

/// <summary>
/// Fires when the cursor crosses into (or out of) a rectangle on screen.
///
/// Edge-triggered: entering fires once, and holding still inside does nothing more. The
/// engine polls the cursor and works out the edge - see <see cref="TriggerDefinition.Edge"/> -
/// so this only reads the answer.
/// </summary>
public sealed class MouseRegionTrigger : ITriggerBlock
{
    public const string TypeName = "mouseregion";

    public string Type => TypeName;
    public string Category => "Screen";
    public string Label => "Mouse enters a region";
    public string Description => "The cursor moves into, or out of, a rectangle on your screen.";
    public string Listens => TriggerKind.Mouse;

    private static readonly FieldChoice[] Modes =
    {
        new("enter", "Enters it"),
        new("leave", "Leaves it")
    };

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("x", "Left", FieldKind.Number, "0",
                      "Screen pixels from the left of the primary monitor.", null, -32000, 32000),
        new FieldSpec("y", "Top", FieldKind.Number, "0", "Screen pixels from the top.", null, -32000, 32000),
        new FieldSpec("width", "Width", FieldKind.Number, "400", null, null, 1, 32000),
        new FieldSpec("height", "Height", FieldKind.Number, "300", null, null, 1, 32000),
        new FieldSpec("mode", "When the cursor", FieldKind.Choice, "enter", null, Modes),
        new FieldSpec("rearm", "Ignore repeats for", FieldKind.Number, "500",
                      "Milliseconds. Stops a cursor sitting on the edge firing over and over.",
                      null, 0, 60000)
    };

    public string Describe(TriggerDefinition definition)
    {
        var word = definition.Config.Get("mode", "enter") == "leave" ? "leaves" : "enters";
        return $"Mouse {word} {definition.Config.GetInt("width", 400)}×{definition.Config.GetInt("height", 300)}"
             + $" at ({definition.Config.GetInt("x")}, {definition.Config.GetInt("y")})";
    }

    /// <summary>Half-open on the far edges, so two regions side by side never both contain a point.</summary>
    public static bool Contains(TriggerDefinition definition, int x, int y)
    {
        var left = definition.Config.GetInt("x");
        var top = definition.Config.GetInt("y");
        var width = Math.Max(1, definition.Config.GetInt("width", 400));
        var height = Math.Max(1, definition.Config.GetInt("height", 300));

        return x >= left && x < left + width && y >= top && y < top + height;
    }

    public bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                        Dictionary<string, string> arguments)
    {
        var wanted = definition.Config.Get("mode", "enter") == "leave" ? -1 : 1;
        if (definition.Edge != wanted) return false;

        var rearm = definition.Config.GetInt("rearm", 500);
        if (rearm > 0 && DateTime.Now < definition.LastFired.AddMilliseconds(rearm)) return false;

        definition.LastFired = DateTime.Now;
        return true;
    }
}

/// <summary>
/// Fires when a key is pressed anywhere on the machine, whether or not StreamerKit has focus.
///
/// Edge-triggered from the engine's input poll, so holding the key down fires once rather
/// than repeating. Because it is global, a bare letter would fire while you type in any
/// other app - which is why it defaults to F8 and offers modifiers.
/// </summary>
public sealed class HotkeyTrigger : ITriggerBlock
{
    public const string TypeName = "hotkey";

    public string Type => TypeName;
    public string Category => "Input";
    public string Label => "Key press";
    public string Description => "A keyboard shortcut, anywhere on your PC.";
    public string Listens => TriggerKind.Key;

    private static readonly FieldChoice[] Modes =
    {
        new("press", "Is pressed"),
        new("release", "Is let go")
    };

    private static readonly FieldChoice[] KeyChoices = BuildKeys();

    private static FieldChoice[] BuildKeys()
    {
        var keys = new List<FieldChoice>();
        for (var vk = 0x70; vk <= 0x7B; vk++) keys.Add(new FieldChoice(vk.ToString(), $"F{vk - 0x6F}"));
        for (var letter = 'A'; letter <= 'Z'; letter++) keys.Add(new FieldChoice(((int)letter).ToString(), letter.ToString()));
        for (var digit = 0; digit <= 9; digit++) keys.Add(new FieldChoice((0x30 + digit).ToString(), digit.ToString()));
        for (var pad = 0; pad <= 9; pad++) keys.Add(new FieldChoice((0x60 + pad).ToString(), $"Numpad {pad}"));

        keys.Add(new FieldChoice("32", "Space"));
        keys.Add(new FieldChoice("13", "Enter"));
        keys.Add(new FieldChoice("27", "Escape"));
        keys.Add(new FieldChoice("9", "Tab"));
        keys.Add(new FieldChoice("8", "Backspace"));
        keys.Add(new FieldChoice("37", "Left arrow"));
        keys.Add(new FieldChoice("38", "Up arrow"));
        keys.Add(new FieldChoice("39", "Right arrow"));
        keys.Add(new FieldChoice("40", "Down arrow"));
        keys.Add(new FieldChoice("45", "Insert"));
        keys.Add(new FieldChoice("46", "Delete"));
        keys.Add(new FieldChoice("36", "Home"));
        keys.Add(new FieldChoice("35", "End"));
        keys.Add(new FieldChoice("33", "Page up"));
        keys.Add(new FieldChoice("34", "Page down"));
        return keys.ToArray();
    }

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("key", "Key", FieldKind.Choice, "119",
                      "This works everywhere, not just in StreamerKit. A plain letter will fire "
                    + "while you type in other apps, so prefer a function key or add modifiers.",
                      KeyChoices),
        new FieldSpec("ctrl", "Hold Ctrl", FieldKind.Toggle, "false"),
        new FieldSpec("alt", "Hold Alt", FieldKind.Toggle, "false"),
        new FieldSpec("shift", "Hold Shift", FieldKind.Toggle, "false"),
        new FieldSpec("mode", "When the key", FieldKind.Choice, "press", null, Modes)
    };

    /// <summary>The label for a stored key code, for both the summary and the %key% argument.</summary>
    public static string KeyName(TriggerDefinition definition)
    {
        var code = definition.Config.Get("key", "119");
        return KeyChoices.FirstOrDefault(k => k.Value == code)?.Label ?? $"key {code}";
    }

    public static string Combo(TriggerDefinition definition)
    {
        var parts = new List<string>();
        if (definition.Config.GetBool("ctrl")) parts.Add("Ctrl");
        if (definition.Config.GetBool("alt")) parts.Add("Alt");
        if (definition.Config.GetBool("shift")) parts.Add("Shift");
        parts.Add(KeyName(definition));
        return string.Join("+", parts);
    }

    public string Describe(TriggerDefinition definition)
    {
        var word = definition.Config.Get("mode", "press") == "release" ? "released" : "pressed";
        return $"{Combo(definition)} {word}";
    }

    /// <summary>
    /// Read by the engine's poll, never here - see the note on <see cref="TriggerDefinition.Edge"/>
    /// about why edges are worked out before dispatch rather than inside Matches.
    /// </summary>
    public static bool IsDown(TriggerDefinition definition)
    {
        if (!int.TryParse(definition.Config.Get("key", "119"), out var code)) return false;
        if (!Native.IsKeyDown(code)) return false;

        // Modifiers are a requirement, not a filter: ticking Ctrl means Ctrl must be held,
        // leaving it unticked means we don't care either way.
        if (definition.Config.GetBool("ctrl") && !Native.IsKeyDown(Native.VirtualKey.Control)) return false;
        if (definition.Config.GetBool("alt") && !Native.IsKeyDown(Native.VirtualKey.Alt)) return false;
        if (definition.Config.GetBool("shift") && !Native.IsKeyDown(Native.VirtualKey.Shift)) return false;

        return true;
    }

    public bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                        Dictionary<string, string> arguments)
    {
        var wanted = definition.Config.Get("mode", "press") == "release" ? -1 : 1;
        if (definition.Edge != wanted) return false;

        arguments["key"] = KeyName(definition);
        arguments["combo"] = Combo(definition);
        return true;
    }
}

/// <summary>
/// Fires on a mouse button, optionally only while the cursor is inside a rectangle - which is
/// what makes the left button usable at all, since otherwise it would fire on every click
/// anywhere on the machine.
/// </summary>
public sealed class MouseButtonTrigger : ITriggerBlock
{
    public const string TypeName = "mousebutton";

    public string Type => TypeName;
    public string Category => "Input";
    public string Label => "Mouse click";
    public string Description => "A mouse button, anywhere or inside a rectangle on screen.";
    public string Listens => TriggerKind.Key;

    private static readonly FieldChoice[] Buttons =
    {
        new("1", "Left"),
        new("2", "Right"),
        new("4", "Middle"),
        new("5", "Mouse 4 (back)"),
        new("6", "Mouse 5 (forward)")
    };

    private static readonly FieldChoice[] Modes =
    {
        new("press", "Is pressed"),
        new("release", "Is let go")
    };

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("button", "Button", FieldKind.Choice, "5",
                      "Left and Right fire on every click on your PC unless you limit them to "
                    + "a rectangle below.", Buttons),
        new FieldSpec("mode", "When the button", FieldKind.Choice, "press", null, Modes),
        new FieldSpec("limit", "Only inside a rectangle", FieldKind.Toggle, "false",
                      "Leave off to catch the button anywhere on screen."),
        new FieldSpec("x", "Left", FieldKind.Number, "0",
                      "Screen pixels. Ignored unless the switch above is on.", null, -32000, 32000),
        new FieldSpec("y", "Top", FieldKind.Number, "0", null, null, -32000, 32000),
        new FieldSpec("width", "Width", FieldKind.Number, "400", null, null, 1, 32000),
        new FieldSpec("height", "Height", FieldKind.Number, "300", null, null, 1, 32000)
    };

    public static string ButtonName(TriggerDefinition definition)
    {
        var code = definition.Config.Get("button", "5");
        return Buttons.FirstOrDefault(b => b.Value == code)?.Label ?? $"button {code}";
    }

    public string Describe(TriggerDefinition definition)
    {
        var word = definition.Config.Get("mode", "press") == "release" ? "released" : "clicked";
        var where = definition.Config.GetBool("limit")
            ? $" inside {definition.Config.GetInt("width", 400)}×{definition.Config.GetInt("height", 300)}"
            + $" at ({definition.Config.GetInt("x")}, {definition.Config.GetInt("y")})"
            : "";
        return $"{ButtonName(definition)} {word}{where}";
    }

    /// <summary>Button state only. Where the cursor was is checked in Matches, which is stateless.</summary>
    public static bool IsDown(TriggerDefinition definition)
        => int.TryParse(definition.Config.Get("button", "5"), out var code) && Native.IsKeyDown(code);

    public bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                        Dictionary<string, string> arguments)
    {
        var wanted = definition.Config.Get("mode", "press") == "release" ? -1 : 1;
        if (definition.Edge != wanted) return false;

        if (definition.Config.GetBool("limit"))
        {
            var x = int.TryParse(incoming.Get("mouseX"), out var mx) ? mx : 0;
            var y = int.TryParse(incoming.Get("mouseY"), out var my) ? my : 0;

            var left = definition.Config.GetInt("x");
            var top = definition.Config.GetInt("y");
            var width = Math.Max(1, definition.Config.GetInt("width", 400));
            var height = Math.Max(1, definition.Config.GetInt("height", 300));

            if (x < left || x >= left + width || y < top || y >= top + height) return false;
        }

        arguments["button"] = ButtonName(definition);
        return true;
    }
}

/// <summary>Shared bits of the two chat triggers: source filtering, permission and cooldowns.</summary>
public abstract class ChatTriggerBase : ITriggerBlock
{
    public abstract string Type { get; }
    public string Category => "Chat";
    public abstract string Label { get; }
    public abstract string Description { get; }
    public string Listens => TriggerKind.Chat;

    public abstract IReadOnlyList<FieldSpec> Fields { get; }
    public abstract string Describe(TriggerDefinition definition);

    protected static readonly FieldChoice[] Sources =
    {
        new("twitch", "Twitch"),
        new("kick", "Kick"),
        new("discord", "Discord")
    };

    protected static readonly FieldChoice[] Permissions =
    {
        new("1", "Anyone"),
        new("2", "VIP or above"),
        new("3", "Moderator or above"),
        new("4", "Broadcaster only")
    };

    protected static FieldSpec SourceField => new("sources", "Read from", FieldKind.MultiChoice,
        "twitch,kick,discord", "Leave them all ticked to accept any of them.", Sources);

    protected static FieldSpec PermissionField => new("permission", "Who may use it", FieldKind.Choice,
        "1", null, Permissions);

    protected static FieldSpec GlobalCooldownField => new("globalCooldown", "Cooldown (seconds)",
        FieldKind.Number, "0", "0 means no cooldown.", null, 0, 86400);

    protected static FieldSpec UserCooldownField => new("userCooldown", "Per-user cooldown (seconds)",
        FieldKind.Number, "0", "Applies to each chatter separately.", null, 0, 86400);

    /// <summary>
    /// Names the platforms a trigger listens to, for its one-line summary. Everything ticked
    /// (or nothing, which accepts everything) reads as plain "chat" rather than as a list
    /// that would only grow every time a platform is added.
    /// </summary>
    protected static string Platforms(TriggerDefinition definition)
    {
        var picked = definition.Config.GetList("sources");
        if (picked.Length == 0 || picked.Length >= Sources.Length) return "chat";

        var names = picked.Select(value =>
            Sources.FirstOrDefault(s => s.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.Label
            ?? value);

        return string.Join(" and ", names);
    }

    /// <summary>Platform, role and cooldown gates, in the order that costs least to check.</summary>
    protected static bool PassesGates(TriggerDefinition definition, TriggerEvent incoming)
    {
        var allowed = definition.Config.GetList("sources");
        if (allowed.Length > 0 &&
            !allowed.Contains(incoming.Get("source"), StringComparer.OrdinalIgnoreCase)) return false;

        var required = definition.Config.GetInt("permission", 1);
        if (required > 1)
        {
            var role = int.TryParse(incoming.Get("role"), out var parsed) ? parsed : 1;
            if (role < required) return false;
        }

        var now = DateTime.Now;
        var global = definition.Config.GetInt("globalCooldown");
        if (global > 0 && now < definition.LastFired.AddSeconds(global)) return false;

        var perUser = definition.Config.GetInt("userCooldown");
        var user = incoming.Get("user");
        if (perUser > 0 && user.Length > 0 &&
            definition.LastFiredByUser.TryGetValue(user, out var last) &&
            now < last.AddSeconds(perUser)) return false;

        return true;
    }

    /// <summary>Only called once a trigger has actually fired, so cooldowns track real runs.</summary>
    protected static void RecordFired(TriggerDefinition definition, TriggerEvent incoming)
    {
        definition.LastFired = DateTime.Now;
        var user = incoming.Get("user");
        if (user.Length > 0) definition.LastFiredByUser[user] = DateTime.Now;
    }

    /// <summary>
    /// Splits what followed the command into %rawInput% and %input0%, %input1%… the same way
    /// Streamer.bot does, so instructions written for it read across.
    /// </summary>
    protected static void AddInputs(Dictionary<string, string> arguments, string rest)
    {
        rest = rest.Trim();
        arguments["rawInput"] = rest;

        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++) arguments[$"input{i}"] = words[i];
        arguments["inputCount"] = words.Length.ToString();
    }

    public abstract bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                                Dictionary<string, string> arguments);
}

/// <summary>Fires on a chat command like "!ads".</summary>
public sealed class CommandTrigger : ChatTriggerBase
{
    public const string TypeName = "command";

    public override string Type => TypeName;
    public override string Label => "Chat command";
    public override string Description => "Someone types !something in chat.";

    private static readonly FieldChoice[] Modes =
    {
        new("start", "Message starts with it"),
        new("exact", "Message is exactly it"),
        new("anywhere", "Anywhere in the message")
    };

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("command", "Command", FieldKind.Text, "!hello", "Include the ! if you want one."),
        new FieldSpec("mode", "Match", FieldKind.Choice, "start", null, Modes),
        new FieldSpec("caseSensitive", "Case sensitive", FieldKind.Toggle, "false"),
        SourceField,
        PermissionField,
        GlobalCooldownField,
        UserCooldownField
    };

    public override string Describe(TriggerDefinition definition)
    {
        var command = definition.Config.Get("command", "!hello");
        return $"{command} in {Platforms(definition)}";
    }

    public override bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                                 Dictionary<string, string> arguments)
    {
        var command = definition.Config.Get("command", "!hello").Trim();
        if (command.Length == 0) return false;

        var message = incoming.Get("message").Trim();
        if (message.Length == 0) return false;

        var comparison = definition.Config.GetBool("caseSensitive")
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        string rest;
        switch (definition.Config.Get("mode", "start"))
        {
            case "exact":
                if (!message.Equals(command, comparison)) return false;
                rest = "";
                break;

            case "anywhere":
                var at = message.IndexOf(command, comparison);
                if (at < 0) return false;
                rest = message[(at + command.Length)..];
                break;

            default:
                if (!message.StartsWith(command, comparison)) return false;

                // "!ad" must not fire on "!ads" - the command has to end at a word boundary.
                if (message.Length > command.Length && message[command.Length] != ' ') return false;
                rest = message[command.Length..];
                break;
        }

        if (!PassesGates(definition, incoming)) return false;

        AddInputs(arguments, rest);
        arguments["command"] = command;
        RecordFired(definition, incoming);
        return true;
    }
}

/// <summary>Fires on the message text itself, rather than a command word.</summary>
public sealed class ChatMessageTrigger : ChatTriggerBase
{
    public const string TypeName = "chatmessage";

    public override string Type => TypeName;
    public override string Label => "Chat message";
    public override string Description => "Any message, or one matching text you choose.";

    private static readonly FieldChoice[] Modes =
    {
        new("any", "Every message"),
        new("contains", "Contains"),
        new("starts", "Starts with"),
        new("regex", "Matches regular expression")
    };

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("mode", "When", FieldKind.Choice, "contains", null, Modes),
        new FieldSpec("text", "Text", FieldKind.Text, "", "Ignored when \"Every message\" is picked."),
        new FieldSpec("caseSensitive", "Case sensitive", FieldKind.Toggle, "false"),
        SourceField,
        PermissionField,
        GlobalCooldownField,
        UserCooldownField
    };

    public override string Describe(TriggerDefinition definition)
    {
        var text = definition.Config.Get("text");
        return definition.Config.Get("mode", "contains") switch
        {
            "any" => "Any chat message",
            "starts" => $"Message starts with \"{text}\"",
            "regex" => $"Message matches /{text}/",
            _ => $"Message contains \"{text}\""
        };
    }

    public override bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                                 Dictionary<string, string> arguments)
    {
        var message = incoming.Get("message");
        var text = definition.Config.Get("text");
        var ignoreCase = !definition.Config.GetBool("caseSensitive");
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var mode = definition.Config.Get("mode", "contains");
        if (mode != "any" && text.Length == 0) return false;

        var hit = mode switch
        {
            "any" => true,
            "starts" => message.StartsWith(text, comparison),
            "regex" => SafeRegex(message, text, ignoreCase),
            _ => message.Contains(text, comparison)
        };
        if (!hit) return false;

        if (!PassesGates(definition, incoming)) return false;

        AddInputs(arguments, message);
        RecordFired(definition, incoming);
        return true;
    }

    /// <summary>
    /// A user-written pattern runs on every chat message, so it gets a timeout - a bad
    /// pattern should cost one message, not lock up the UI thread on a busy channel.
    /// </summary>
    private static bool SafeRegex(string message, string pattern, bool ignoreCase)
    {
        try
        {
            var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
            return Regex.IsMatch(message, pattern, options, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException) { return false; }        // pattern doesn't compile
        catch (RegexMatchTimeoutException) { return false; }
    }
}

/// <summary>Fires on a fixed interval while the action is enabled.</summary>
public sealed class TimerTrigger : ITriggerBlock
{
    public const string TypeName = "timer";

    public string Type => TypeName;
    public string Category => "Time";
    public string Label => "Timer";
    public string Description => "Every so many seconds, on its own.";

    // Timers are driven by ActionEngine.Tick rather than dispatched, so nothing routes here.
    public string Listens => TriggerKind.Timer;

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("seconds", "Every", FieldKind.Number, "300", "Seconds between runs.", null, 1, 86400)
    };

    public string Describe(TriggerDefinition definition)
    {
        var seconds = Math.Max(1, definition.Config.GetInt("seconds", 300));
        if (seconds < 60) return $"Every {seconds}s";
        if (seconds % 3600 == 0) return $"Every {seconds / 3600}h";
        if (seconds % 60 == 0) return $"Every {seconds / 60} min";
        return $"Every {seconds / 60} min {seconds % 60}s";
    }

    public bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                        Dictionary<string, string> arguments) => true;
}

/// <summary>Fires when one of the app's own servers changes state.</summary>
public sealed class ServerStateTrigger : ITriggerBlock
{
    public const string TypeName = "serverstate";

    public string Type => TypeName;
    public string Category => "Servers";
    public string Label => "Server state";
    public string Description => "A server starts, stops, or falls over.";
    public string Listens => TriggerKind.Server;

    private static readonly FieldChoice[] States =
    {
        new("started", "Started"),
        new("stopped", "Stopped"),
        new("crashed", "Crashed"),
        new("gaveup", "Gave up restarting")
    };

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("server", "Server", FieldKind.Choice, "", "Leave empty for any server.",
                      null, 0, 1_000_000, DynamicChoices.Servers),
        new FieldSpec("state", "When it", FieldKind.Choice, "crashed", null, States)
    };

    public string Describe(TriggerDefinition definition)
    {
        var server = definition.Config.Get("server");
        var state = definition.Config.Get("state", "crashed");
        var what = States.FirstOrDefault(s => s.Value == state)?.Label.ToLowerInvariant() ?? state;
        return server.Length == 0 ? $"Any server {what}" : $"{server} {what}";
    }

    public bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                        Dictionary<string, string> arguments)
    {
        if (definition.Config.Get("state", "crashed") != incoming.Get("serverState")) return false;

        var server = definition.Config.Get("server");
        return server.Length == 0 ||
               string.Equals(server, incoming.Get("serverName"), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// "Going live started" and its relatives, for whichever program is doing the encoding.
///
/// OBS and Streamlabs both push these rather than needing to be asked, but they disagree
/// about everything else: OBS names an event type and carries a settled/transitional flag,
/// Streamlabs names a resource and carries a bare status word. Both are flattened to the
/// same three arguments before they reach a trigger — see <c>MainWindow.DispatchOutput</c> —
/// so this block only has to compare strings.
///
/// Only *settled* states arrive. OBS reports STARTING and STOPPING as well, and Streamlabs
/// reports "starting" and "ending"; firing on those would run the action twice for one
/// go-live, and the second time before the stream was actually up.
/// </summary>
public abstract class OutputStateTriggerBase : ITriggerBlock
{
    public abstract string Type { get; }
    public abstract string Category { get; }
    public abstract string Label { get; }
    public abstract string Description { get; }
    public string Listens => TriggerKind.Output;

    /// <summary>Which integration this block listens to. Events from the other one are ignored.</summary>
    protected abstract string Source { get; }

    protected abstract IReadOnlyList<FieldChoice> Outputs { get; }

    protected static readonly FieldChoice[] Streaming = { new("streaming", "Streaming") };
    protected static readonly FieldChoice[] Recording = { new("recording", "Recording") };
    protected static readonly FieldChoice[] ReplayBuffer = { new("replaybuffer", "Replay buffer") };
    protected static readonly FieldChoice[] VirtualCamera = { new("virtualcam", "Virtual camera") };

    protected static readonly FieldChoice[] States =
    {
        new("started", "Starts"),
        new("stopped", "Stops"),
        new("either", "Starts or stops")
    };

    private IReadOnlyList<FieldSpec>? _fields;

    /// <summary>Built once rather than in a field initializer, since it needs the subclass's
    /// <see cref="Outputs"/> — which isn't set until after the base class is constructed.</summary>
    public IReadOnlyList<FieldSpec> Fields => _fields ??= new[]
    {
        new FieldSpec("output", "Watch", FieldKind.Choice, "streaming", null, Outputs),
        new FieldSpec("state", "When it", FieldKind.Choice, "started",
                      "Only fires while the integration is connected on the Integrations page.",
                      States)
    };

    public string Describe(TriggerDefinition definition)
    {
        var output = definition.Config.Get("output", "streaming");
        var what = Outputs.FirstOrDefault(o => o.Value == output)?.Label.ToLowerInvariant() ?? output;

        // Category is the program's name, which is exactly what the summary should read as.
        return definition.Config.Get("state", "started") switch
        {
            "stopped" => $"{Category} stops {what}",
            "either" => $"{Category} starts or stops {what}",
            _ => $"{Category} starts {what}"
        };
    }

    public bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                        Dictionary<string, string> arguments)
    {
        if (!string.Equals(Source, incoming.Get("source"), StringComparison.OrdinalIgnoreCase)) return false;
        if (definition.Config.Get("output", "streaming") != incoming.Get("output")) return false;

        var wanted = definition.Config.Get("state", "started");
        var actual = incoming.Get("state");
        if (wanted != "either" && wanted != actual) return false;

        // Handed to the steps so one action can cover both directions - a Discord message of
        // "Stream %state%" reads correctly either way.
        arguments["state"] = actual;
        arguments["output"] = incoming.Get("output");
        arguments["source"] = incoming.Get("source");
        return true;
    }
}

/// <summary>Streaming, recording, the replay buffer or the virtual camera, in OBS Studio.</summary>
public sealed class ObsStateTrigger : OutputStateTriggerBase
{
    public const string TypeName = "obsstate";

    public override string Type => TypeName;
    public override string Category => "OBS";
    public override string Label => "OBS starts or stops";
    public override string Description => "OBS begins or ends streaming, recording, the replay buffer or the virtual camera.";

    protected override string Source => "obs";

    protected override IReadOnlyList<FieldChoice> Outputs { get; } =
        Streaming.Concat(Recording).Concat(ReplayBuffer).Concat(VirtualCamera).ToArray();
}

/// <summary>Streaming, recording or the replay buffer, in Streamlabs Desktop.</summary>
public sealed class StreamlabsStateTrigger : OutputStateTriggerBase
{
    public const string TypeName = "slabsstate";

    public override string Type => TypeName;
    public override string Category => "Streamlabs";
    public override string Label => "Streamlabs starts or stops";
    public override string Description => "Streamlabs begins or ends streaming, recording or the replay buffer.";

    protected override string Source => "streamlabs";

    protected override IReadOnlyList<FieldChoice> Outputs { get; } =
        Streaming.Concat(Recording).Concat(ReplayBuffer).ToArray();
}

/// <summary>
/// Lets a connected overlay or another program run this action by name over the WebSocket
/// server, the way a Streamer.bot client would.
///
/// This is opt-in on purpose. The WebSocket server can be bound to an address other than
/// localhost, and without a gate every action in the list would be callable by anything that
/// could reach the port.
/// </summary>
public sealed class DoActionTrigger : ITriggerBlock
{
    public const string TypeName = "doaction";

    public string Type => TypeName;
    public string Category => "External";
    public string Label => "Called over WebSocket";
    public string Description => "An overlay or app asks for this action by name (DoAction).";
    public string Listens => TriggerKind.DoAction;

    public IReadOnlyList<FieldSpec> Fields { get; } = Array.Empty<FieldSpec>();

    public string Describe(TriggerDefinition definition) => "Called over WebSocket";

    public bool Matches(TriggerDefinition definition, TriggerEvent incoming,
                        Dictionary<string, string> arguments) => true;
}
