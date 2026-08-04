using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamerKit;

// ---------------------------------------------------------------------------
//  Core
// ---------------------------------------------------------------------------

/// <summary>Waits, without holding the UI thread.</summary>
public sealed class DelaySubAction : ISubActionBlock
{
    public string Type => "delay";
    public string Category => "Core";
    public string Label => "Delay";
    public string Description => "Wait before the next step.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("ms", "Wait", FieldKind.Number, "1000", "Milliseconds.", null, 0, 600000)
    };

    public string Describe(SubActionDefinition definition)
    {
        var ms = definition.Config.GetInt("ms", 1000);
        return ms >= 1000 ? $"Wait {ms / 1000.0:0.##}s" : $"Wait {ms}ms";
    }

    public Task Run(SubActionDefinition definition, ActionContext context)
        => Task.Delay(Math.Max(0, definition.Config.GetInt("ms", 1000)), context.Token);
}

/// <summary>Waits a random length of time, so repeated messages don't look automated.</summary>
public sealed class RandomDelaySubAction : ISubActionBlock
{
    public string Type => "randomdelay";
    public string Category => "Core";
    public string Label => "Random delay";
    public string Description => "Wait somewhere between two lengths of time.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("min", "At least", FieldKind.Number, "500", "Milliseconds.", null, 0, 600000),
        new FieldSpec("max", "At most", FieldKind.Number, "2000", "Milliseconds.", null, 0, 600000)
    };

    public string Describe(SubActionDefinition definition)
        => $"Wait {definition.Config.GetInt("min", 500)}–{definition.Config.GetInt("max", 2000)}ms";

    public Task Run(SubActionDefinition definition, ActionContext context)
    {
        var min = Math.Max(0, definition.Config.GetInt("min", 500));
        var max = Math.Max(min, definition.Config.GetInt("max", 2000));
        return Task.Delay(Random.Shared.Next(min, max + 1), context.Token);
    }
}

/// <summary>Puts a value in the argument bag for later steps to read as %name%.</summary>
public sealed class SetArgumentSubAction : ISubActionBlock
{
    public string Type => "setargument";
    public string Category => "Core";
    public string Label => "Set argument";
    public string Description => "Store a value that later steps can use as %name%.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("name", "Name", FieldKind.Text, "myValue", "Referred to later as %myValue%."),
        new FieldSpec("value", "Value", FieldKind.Text, "", "May contain other %arguments%.")
    };

    public string Describe(SubActionDefinition definition)
        => $"%{definition.Config.Get("name", "myValue")}% = {definition.Config.Get("value")}";

    public Task Run(SubActionDefinition definition, ActionContext context)
    {
        var name = definition.Config.Get("name").Trim();
        if (name.Length == 0) throw new InvalidOperationException("no argument name set");

        context.Arguments[name] = context.Field(definition, "value");
        return Task.CompletedTask;
    }
}

/// <summary>Ends the action unless a comparison holds.</summary>
public sealed class ConditionSubAction : ISubActionBlock
{
    public string Type => "condition";
    public string Category => "Core";
    public string Label => "Only continue if";
    public string Description => "Stop the action here unless a comparison is true.";

    private static readonly FieldChoice[] Operators =
    {
        new("eq", "is"),
        new("ne", "is not"),
        new("contains", "contains"),
        new("startswith", "starts with"),
        new("gt", "is greater than"),
        new("lt", "is less than"),
        new("empty", "is empty"),
        new("notempty", "is not empty")
    };

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("left", "Value", FieldKind.Text, "%rawInput%"),
        new FieldSpec("op", "Comparison", FieldKind.Choice, "eq", null, Operators),
        new FieldSpec("right", "Compared to", FieldKind.Text, "", "Ignored by \"is empty\"."),
        new FieldSpec("caseSensitive", "Case sensitive", FieldKind.Toggle, "false")
    };

    public string Describe(SubActionDefinition definition)
    {
        var op = definition.Config.Get("op", "eq");
        var label = Operators.FirstOrDefault(o => o.Value == op)?.Label ?? op;
        var right = op is "empty" or "notempty" ? "" : $" {definition.Config.Get("right")}";
        return $"Continue if {definition.Config.Get("left", "%rawInput%")} {label}{right}";
    }

    public Task Run(SubActionDefinition definition, ActionContext context)
    {
        var left = context.Field(definition, "left");
        var right = context.Field(definition, "right");
        var comparison = definition.Config.GetBool("caseSensitive")
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var holds = definition.Config.Get("op", "eq") switch
        {
            "ne" => !left.Equals(right, comparison),
            "contains" => left.Contains(right, comparison),
            "startswith" => left.StartsWith(right, comparison),
            "gt" => Numeric(left) > Numeric(right),
            "lt" => Numeric(left) < Numeric(right),
            "empty" => left.Trim().Length == 0,
            "notempty" => left.Trim().Length > 0,
            _ => left.Equals(right, comparison)
        };

        if (!holds)
        {
            context.Log($"Stopped — {Describe(definition)} was false.");
            context.Stopped = true;
        }
        return Task.CompletedTask;
    }

    private static double Numeric(string text)
        => double.TryParse(text, out var value) ? value : double.NaN;
}

/// <summary>Runs another action, passing the arguments along.</summary>
public sealed class RunActionSubAction : ISubActionBlock
{
    public string Type => "runaction";
    public string Category => "Core";
    public string Label => "Run another action";
    public string Description => "Hand off to another action, keeping the arguments.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("action", "Action", FieldKind.Choice, "", null, null, 0, 1_000_000,
                      DynamicChoices.Actions)
    };

    public string Describe(SubActionDefinition definition)
    {
        var name = definition.Config.Get("action");
        return name.Length == 0 ? "Run another action" : $"Run \"{name}\"";
    }

    public Task Run(SubActionDefinition definition, ActionContext context)
    {
        var name = definition.Config.Get("action").Trim();
        if (name.Length == 0) throw new InvalidOperationException("no action picked");

        return context.RunNested(name);
    }
}

/// <summary>Does nothing. Somewhere to write down why the steps around it exist.</summary>
public sealed class CommentSubAction : ISubActionBlock
{
    public string Type => "comment";
    public string Category => "Core";
    public string Label => "Comment";
    public string Description => "A note to yourself. Does nothing when the action runs.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("text", "Note", FieldKind.LongText, "")
    };

    public string Describe(SubActionDefinition definition)
    {
        var text = definition.Config.Get("text");
        return text.Length == 0 ? "Comment" : text.Length <= 60 ? text : text[..60] + "…";
    }

    public Task Run(SubActionDefinition definition, ActionContext context) => Task.CompletedTask;
}

// ---------------------------------------------------------------------------
//  OBS
// ---------------------------------------------------------------------------

/// <summary>Shared plumbing: every OBS step needs a live connection and reports failures the same way.</summary>
public abstract class ObsSubActionBase : ISubActionBlock
{
    public abstract string Type { get; }
    public string Category => "OBS";
    public abstract string Label { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<FieldSpec> Fields { get; }
    public abstract string Describe(SubActionDefinition definition);

    protected static readonly FieldChoice[] Visibility =
    {
        new("show", "Show"),
        new("hide", "Hide"),
        new("toggle", "Toggle")
    };

    public async Task Run(SubActionDefinition definition, ActionContext context)
    {
        var obs = context.Services.Obs;
        if (!obs.IsConnected)
            throw new InvalidOperationException("OBS isn't connected — connect it on the Integrations page.");

        await RunObs(definition, context, obs);
    }

    protected abstract Task RunObs(SubActionDefinition definition, ActionContext context, ObsClient obs);

    /// <summary>Throws with the message OBS gave, so it lands in the action's log verbatim.</summary>
    protected static ObsClient.ObsResult Ensure(ObsClient.ObsResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        return result;
    }

    /// <summary>
    /// obs-websocket 5 addresses scene items by numeric id, so a source name always has to
    /// be resolved first - there is no "do something to the source called X" request.
    /// </summary>
    protected static async Task<int> ItemId(ObsClient obs, string scene, string source)
    {
        var lookup = Ensure(await obs.Call("GetSceneItemId",
            new JsonObject { ["sceneName"] = scene, ["sourceName"] = source }));

        return lookup["sceneItemId"]?.GetValue<int>()
            ?? throw new InvalidOperationException($"\"{source}\" isn't in scene \"{scene}\"");
    }

    protected static async Task<(double X, double Y)> Position(ObsClient obs, string scene, int itemId)
    {
        var result = Ensure(await obs.Call("GetSceneItemTransform",
            new JsonObject { ["sceneName"] = scene, ["sceneItemId"] = itemId }));

        var transform = result["sceneItemTransform"]?.AsObject();
        return (transform?["positionX"]?.GetValue<double>() ?? 0,
                transform?["positionY"]?.GetValue<double>() ?? 0);
    }

    protected static readonly FieldSpec SceneField = new("scene", "Scene", FieldKind.Text, "");
    protected static readonly FieldSpec SourceField = new("source", "Source", FieldKind.Text, "");
}

/// <summary>Puts a source at a fixed spot on the canvas.</summary>
public sealed class ObsMoveSourceSubAction : ObsSubActionBase
{
    public override string Type => "obsmove";
    public override string Label => "Move a source";
    public override string Description => "Put a source at a position on the OBS canvas.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        SceneField,
        SourceField,
        new FieldSpec("x", "X", FieldKind.Number, "0", "Canvas units, not screen pixels.", null, -20000, 20000),
        new FieldSpec("y", "Y", FieldKind.Number, "0", null, null, -20000, 20000),
        new FieldSpec("glide", "Glide over", FieldKind.Number, "0",
                      "Milliseconds. 0 jumps straight there.", null, 0, 5000)
    };

    public override string Describe(SubActionDefinition definition)
    {
        var source = definition.Config.Get("source");
        return source.Length == 0
            ? "Move a source"
            : $"Move \"{source}\" to ({definition.Config.GetInt("x")}, {definition.Config.GetInt("y")})";
    }

    protected override async Task RunObs(SubActionDefinition definition, ActionContext context, ObsClient obs)
    {
        var scene = context.Field(definition, "scene");
        var source = context.Field(definition, "source");
        if (scene.Length == 0 || source.Length == 0)
            throw new InvalidOperationException("both a scene and a source are needed");

        var itemId = await ItemId(obs, scene, source);
        var x = definition.Config.GetDouble("x");
        var y = definition.Config.GetDouble("y");

        await Glide.To(obs, scene, itemId, x, y, definition.Config.GetInt("glide"), context.Token);
        context.Log($"OBS moved \"{source}\" to ({x:0}, {y:0})");
    }
}

/// <summary>
/// Sends a source to whichever of two spots it isn't currently at.
///
/// This exists so "get out of the way of my cursor" is one step instead of a branch: the
/// step reads where the source is now, so the action needs no memory of which side it left
/// it on, and nothing drifts out of sync if OBS is changed by hand.
/// </summary>
public sealed class ObsFlipSourceSubAction : ObsSubActionBase
{
    public override string Type => "obsflip";
    public override string Label => "Flip a source between two spots";
    public override string Description => "Send a source to whichever of two positions it is not at.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        SceneField,
        SourceField,
        new FieldSpec("ax", "Spot A · X", FieldKind.Number, "0", "Canvas units.", null, -20000, 20000),
        new FieldSpec("ay", "Spot A · Y", FieldKind.Number, "0", null, null, -20000, 20000),
        new FieldSpec("bx", "Spot B · X", FieldKind.Number, "1300", null, null, -20000, 20000),
        new FieldSpec("by", "Spot B · Y", FieldKind.Number, "0", null, null, -20000, 20000),
        new FieldSpec("glide", "Glide over", FieldKind.Number, "250",
                      "Milliseconds. 0 jumps straight there.", null, 0, 5000)
    };

    public override string Describe(SubActionDefinition definition)
    {
        var source = definition.Config.Get("source");
        return source.Length == 0 ? "Flip a source between two spots" : $"Flip \"{source}\" to the other spot";
    }

    protected override async Task RunObs(SubActionDefinition definition, ActionContext context, ObsClient obs)
    {
        var scene = context.Field(definition, "scene");
        var source = context.Field(definition, "source");
        if (scene.Length == 0 || source.Length == 0)
            throw new InvalidOperationException("both a scene and a source are needed");

        var itemId = await ItemId(obs, scene, source);
        var (currentX, currentY) = await Position(obs, scene, itemId);

        var ax = definition.Config.GetDouble("ax");
        var ay = definition.Config.GetDouble("ay");
        var bx = definition.Config.GetDouble("bx", 1300);
        var by = definition.Config.GetDouble("by");

        // Whichever spot it is further from is the one it should go to.
        var toA = Distance(currentX, currentY, ax, ay);
        var toB = Distance(currentX, currentY, bx, by);
        var (targetX, targetY, which) = toA > toB ? (ax, ay, "A") : (bx, by, "B");

        await Glide.To(obs, scene, itemId, targetX, targetY, definition.Config.GetInt("glide", 250), context.Token);

        context.Arguments["flippedTo"] = which;
        context.Log($"OBS flipped \"{source}\" to spot {which} ({targetX:0}, {targetY:0})");
    }

    private static double Distance(double x1, double y1, double x2, double y2)
        => Math.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));
}

/// <summary>Moves a scene item, optionally easing it there instead of teleporting.</summary>
internal static class Glide
{
    public static async Task To(ObsClient obs, string scene, int itemId,
                                double x, double y, int milliseconds, CancellationToken token)
    {
        if (milliseconds <= 0)
        {
            await Set(obs, scene, itemId, x, y);
            return;
        }

        var start = await Read(obs, scene, itemId);

        // ~60 fps, capped so a long glide doesn't turn into hundreds of round trips.
        var frames = Math.Clamp(milliseconds / 16, 2, 60);
        var delay = milliseconds / frames;

        for (var frame = 1; frame <= frames; frame++)
        {
            token.ThrowIfCancellationRequested();

            var progress = (double)frame / frames;
            var eased = progress < 0.5
                ? 2 * progress * progress
                : 1 - (Math.Pow((-2 * progress) + 2, 2) / 2);      // ease in and out

            await Set(obs, scene, itemId,
                      start.X + ((x - start.X) * eased),
                      start.Y + ((y - start.Y) * eased));

            if (frame < frames) await Task.Delay(delay, token);
        }
    }

    private static async Task<(double X, double Y)> Read(ObsClient obs, string scene, int itemId)
    {
        var result = await obs.Call("GetSceneItemTransform",
            new JsonObject { ["sceneName"] = scene, ["sceneItemId"] = itemId });

        var transform = result.Data?["sceneItemTransform"]?.AsObject();
        return (transform?["positionX"]?.GetValue<double>() ?? 0,
                transform?["positionY"]?.GetValue<double>() ?? 0);
    }

    private static Task Set(ObsClient obs, string scene, int itemId, double x, double y)
        => obs.Call("SetSceneItemTransform", new JsonObject
        {
            ["sceneName"] = scene,
            ["sceneItemId"] = itemId,
            ["sceneItemTransform"] = new JsonObject { ["positionX"] = x, ["positionY"] = y }
        });
}

/// <summary>Switches the program scene.</summary>
public sealed class ObsSceneSubAction : ObsSubActionBase
{
    public override string Type => "obsscene";
    public override string Label => "Set scene";
    public override string Description => "Switch OBS to a scene.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("scene", "Scene name", FieldKind.Text, "", "Exactly as it appears in OBS.")
    };

    public override string Describe(SubActionDefinition definition)
    {
        var scene = definition.Config.Get("scene");
        return scene.Length == 0 ? "Set scene" : $"Set scene to \"{scene}\"";
    }

    protected override async Task RunObs(SubActionDefinition definition, ActionContext context, ObsClient obs)
    {
        var scene = context.Field(definition, "scene");
        if (scene.Length == 0) throw new InvalidOperationException("no scene name set");

        Ensure(await obs.Call("SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene }));
        context.Log($"OBS scene → {scene}");
    }
}

/// <summary>Shows or hides a source within a scene.</summary>
public sealed class ObsSourceSubAction : ObsSubActionBase
{
    public override string Type => "obssource";
    public override string Label => "Show or hide a source";
    public override string Description => "Toggle a source's visibility in a scene.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("scene", "Scene", FieldKind.Text, ""),
        new FieldSpec("source", "Source", FieldKind.Text, ""),
        new FieldSpec("state", "Action", FieldKind.Choice, "toggle", null, Visibility)
    };

    public override string Describe(SubActionDefinition definition)
    {
        var state = definition.Config.Get("state", "toggle");
        var label = Visibility.FirstOrDefault(v => v.Value == state)?.Label ?? state;
        var source = definition.Config.Get("source");
        return source.Length == 0 ? "Show or hide a source" : $"{label} \"{source}\"";
    }

    protected override async Task RunObs(SubActionDefinition definition, ActionContext context, ObsClient obs)
    {
        var scene = context.Field(definition, "scene");
        var source = context.Field(definition, "source");
        if (scene.Length == 0 || source.Length == 0)
            throw new InvalidOperationException("both a scene and a source are needed");

        // obs-websocket 5 addresses scene items by numeric id, so the name has to be looked
        // up first - there is no "set visible by source name" request.
        var lookup = Ensure(await obs.Call("GetSceneItemId",
            new JsonObject { ["sceneName"] = scene, ["sourceName"] = source }));

        var itemId = lookup["sceneItemId"]?.GetValue<int>()
            ?? throw new InvalidOperationException($"\"{source}\" isn't in scene \"{scene}\"");

        var wanted = definition.Config.Get("state", "toggle") switch
        {
            "show" => true,
            "hide" => false,
            _ => !(Ensure(await obs.Call("GetSceneItemEnabled",
                     new JsonObject { ["sceneName"] = scene, ["sceneItemId"] = itemId }))
                   ["sceneItemEnabled"]?.GetValue<bool>() ?? false)
        };

        Ensure(await obs.Call("SetSceneItemEnabled", new JsonObject
        {
            ["sceneName"] = scene,
            ["sceneItemId"] = itemId,
            ["sceneItemEnabled"] = wanted
        }));
        context.Log($"OBS {(wanted ? "showed" : "hid")} \"{source}\" in \"{scene}\"");
    }
}

/// <summary>Turns a filter on or off.</summary>
public sealed class ObsFilterSubAction : ObsSubActionBase
{
    public override string Type => "obsfilter";
    public override string Label => "Enable or disable a filter";
    public override string Description => "Switch a source filter on or off.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("source", "Source", FieldKind.Text, ""),
        new FieldSpec("filter", "Filter", FieldKind.Text, ""),
        new FieldSpec("state", "Action", FieldKind.Choice, "toggle", null, Visibility)
    };

    public override string Describe(SubActionDefinition definition)
    {
        var filter = definition.Config.Get("filter");
        var state = definition.Config.Get("state", "toggle");
        var label = state == "show" ? "Enable" : state == "hide" ? "Disable" : "Toggle";
        return filter.Length == 0 ? "Enable or disable a filter" : $"{label} filter \"{filter}\"";
    }

    protected override async Task RunObs(SubActionDefinition definition, ActionContext context, ObsClient obs)
    {
        var source = context.Field(definition, "source");
        var filter = context.Field(definition, "filter");
        if (source.Length == 0 || filter.Length == 0)
            throw new InvalidOperationException("both a source and a filter are needed");

        var wanted = definition.Config.Get("state", "toggle") switch
        {
            "show" => true,
            "hide" => false,
            _ => !(Ensure(await obs.Call("GetSourceFilter",
                     new JsonObject { ["sourceName"] = source, ["filterName"] = filter }))
                   ["filterEnabled"]?.GetValue<bool>() ?? false)
        };

        Ensure(await obs.Call("SetSourceFilterEnabled", new JsonObject
        {
            ["sourceName"] = source,
            ["filterName"] = filter,
            ["filterEnabled"] = wanted
        }));
        context.Log($"OBS {(wanted ? "enabled" : "disabled")} filter \"{filter}\" on \"{source}\"");
    }
}

/// <summary>Sends any obs-websocket request, for whatever the friendlier steps don't cover.</summary>
public sealed class ObsRawSubAction : ObsSubActionBase
{
    public override string Type => "obsraw";
    public override string Label => "Raw OBS request";
    public override string Description => "Any obs-websocket request, with your own JSON.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("requestType", "Request", FieldKind.Text, "GetVersion",
                      "An obs-websocket 5 request name, e.g. SetCurrentProgramScene."),
        new FieldSpec("requestData", "Request data", FieldKind.LongText, "{}", "JSON. May contain %arguments%."),
        new FieldSpec("saveTo", "Save response to", FieldKind.Text, "",
                      "Optional argument name to hold the JSON that comes back.")
    };

    public override string Describe(SubActionDefinition definition)
        => $"OBS {definition.Config.Get("requestType", "GetVersion")}";

    protected override async Task RunObs(SubActionDefinition definition, ActionContext context, ObsClient obs)
    {
        var request = context.Field(definition, "requestType");
        if (request.Length == 0) throw new InvalidOperationException("no request type set");

        JsonObject data;
        var raw = context.Field(definition, "requestData", "{}").Trim();
        try
        {
            data = raw.Length == 0 ? new JsonObject() : JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"request data isn't valid JSON — {ex.Message}");
        }

        var result = Ensure(await obs.Call(request, data));

        var saveTo = definition.Config.Get("saveTo").Trim();
        if (saveTo.Length > 0) context.Arguments[saveTo] = result.Data?.ToJsonString() ?? "";
    }
}

// ---------------------------------------------------------------------------
//  Overlays
// ---------------------------------------------------------------------------

/// <summary>Pushes a custom event to every connected overlay widget.</summary>
public sealed class BroadcastSubAction : ISubActionBlock
{
    public string Type => "broadcast";
    public string Category => "Overlays";
    public string Label => "Send to overlays";
    public string Description => "Push an event over the WebSocket server to every connected widget.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("source", "Source", FieldKind.Text, "StreamerKit", "Shown to the widget as event.source."),
        new FieldSpec("type", "Type", FieldKind.Text, "Custom", "Shown to the widget as event.type."),
        new FieldSpec("data", "Data", FieldKind.LongText, "{}", "JSON. May contain %arguments%."),
        new FieldSpec("includeArguments", "Include all arguments", FieldKind.Toggle, "true",
                      "Adds every %argument% under \"arguments\".")
    };

    public string Describe(SubActionDefinition definition)
        => $"Send {definition.Config.Get("source", "StreamerKit")}.{definition.Config.Get("type", "Custom")} to overlays";

    public async Task Run(SubActionDefinition definition, ActionContext context)
    {
        JsonObject payload;
        var raw = context.Field(definition, "data", "{}").Trim();
        try
        {
            payload = raw.Length == 0 ? new JsonObject() : JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"data isn't valid JSON — {ex.Message}");
        }

        if (definition.Config.GetBool("includeArguments", true))
        {
            var arguments = new JsonObject();
            foreach (var (key, value) in context.Arguments) arguments[key] = value;
            payload["arguments"] = arguments;
        }

        await context.Services.Broadcast(
            context.Field(definition, "source", "StreamerKit"),
            context.Field(definition, "type", "Custom"),
            payload);
    }
}

// ---------------------------------------------------------------------------
//  Servers
// ---------------------------------------------------------------------------

/// <summary>Starts, stops or restarts one of the servers on the Servers page.</summary>
public sealed class ServerControlSubAction : ISubActionBlock
{
    public string Type => "servercontrol";
    public string Category => "Servers";
    public string Label => "Start or stop a server";
    public string Description => "Control one of the servers StreamerKit runs.";

    private static readonly FieldChoice[] Commands =
    {
        new("start", "Start"),
        new("stop", "Stop"),
        new("restart", "Restart")
    };

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("server", "Server", FieldKind.Choice, "", null, null, 0, 1_000_000,
                      DynamicChoices.Servers),
        new FieldSpec("command", "Do", FieldKind.Choice, "start", null, Commands)
    };

    public string Describe(SubActionDefinition definition)
    {
        var server = definition.Config.Get("server");
        var command = definition.Config.Get("command", "start");
        var label = Commands.FirstOrDefault(c => c.Value == command)?.Label ?? command;
        return server.Length == 0 ? "Start or stop a server" : $"{label} {server}";
    }

    public Task Run(SubActionDefinition definition, ActionContext context)
    {
        var server = context.Field(definition, "server");
        if (server.Length == 0) throw new InvalidOperationException("no server picked");

        var command = definition.Config.Get("command", "start");
        if (!context.Services.ControlServer(server, command))
            throw new InvalidOperationException($"there's no server called \"{server}\"");

        context.Log($"{command} {server}");
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
//  System
// ---------------------------------------------------------------------------

/// <summary>Calls a URL — a Discord webhook, another local server, anything HTTP.</summary>
public sealed class HttpRequestSubAction : ISubActionBlock
{
    public string Type => "http";
    public string Category => "System";
    public string Label => "HTTP request";
    public string Description => "Call a URL. Webhooks, local servers, anything that speaks HTTP.";

    private static readonly FieldChoice[] Methods =
    {
        new("GET", "GET"),
        new("POST", "POST"),
        new("PUT", "PUT"),
        new("DELETE", "DELETE")
    };

    private static readonly FieldChoice[] ContentTypes =
    {
        new("application/json", "JSON"),
        new("text/plain", "Plain text"),
        new("application/x-www-form-urlencoded", "Form")
    };

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("method", "Method", FieldKind.Choice, "GET", null, Methods),
        new FieldSpec("url", "URL", FieldKind.Text, "", "May contain %arguments%."),
        new FieldSpec("contentType", "Body type", FieldKind.Choice, "application/json", null, ContentTypes),
        new FieldSpec("body", "Body", FieldKind.LongText, "", "Ignored by GET."),
        new FieldSpec("saveTo", "Save response to", FieldKind.Text, "",
                      "Optional argument name to hold the response text.")
    };

    public string Describe(SubActionDefinition definition)
    {
        var url = definition.Config.Get("url");
        var method = definition.Config.Get("method", "GET");
        return url.Length == 0 ? "HTTP request" : $"{method} {(url.Length <= 48 ? url : url[..48] + "…")}";
    }

    public async Task Run(SubActionDefinition definition, ActionContext context)
    {
        var url = context.Field(definition, "url");
        if (url.Length == 0) throw new InvalidOperationException("no URL set");

        var method = new HttpMethod(definition.Config.Get("method", "GET"));
        using var request = new HttpRequestMessage(method, url);

        if (method != HttpMethod.Get)
        {
            var body = context.Field(definition, "body");
            if (body.Length > 0)
                request.Content = new StringContent(body, Encoding.UTF8,
                    definition.Config.Get("contentType", "application/json"));
        }

        using var response = await context.Services.Http.SendAsync(request, context.Token);
        var text = await response.Content.ReadAsStringAsync(context.Token);

        var saveTo = definition.Config.Get("saveTo").Trim();
        if (saveTo.Length > 0) context.Arguments[saveTo] = text;

        context.Log($"{method} {url} → {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}");
    }
}

/// <summary>Launches a program.</summary>
public sealed class RunProgramSubAction : ISubActionBlock
{
    public string Type => "runprogram";
    public string Category => "System";
    public string Label => "Run a program";
    public string Description => "Launch an executable or script.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("path", "Program", FieldKind.File, ""),
        new FieldSpec("arguments", "Arguments", FieldKind.Text, "", "May contain %arguments%."),
        new FieldSpec("workingDirectory", "Start in", FieldKind.Text, "", "Optional."),
        new FieldSpec("wait", "Wait for it to finish", FieldKind.Toggle, "false",
                      "The action pauses here until the program exits.")
    };

    public string Describe(SubActionDefinition definition)
    {
        var path = definition.Config.Get("path");
        return path.Length == 0 ? "Run a program" : $"Run {System.IO.Path.GetFileName(path)}";
    }

    public async Task Run(SubActionDefinition definition, ActionContext context)
    {
        var path = context.Field(definition, "path");
        if (path.Length == 0) throw new InvalidOperationException("no program set");

        var info = new ProcessStartInfo
        {
            FileName = path,
            Arguments = context.Field(definition, "arguments"),
            UseShellExecute = true
        };

        var directory = context.Field(definition, "workingDirectory");
        if (directory.Length > 0) info.WorkingDirectory = directory;

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"could not start {path}");

        context.Log($"Started {System.IO.Path.GetFileName(path)}");

        // Not adopted into the job object on purpose: this is something the user asked to
        // launch, not a server we supervise, so closing StreamerKit shouldn't kill it.
        if (definition.Config.GetBool("wait")) await process.WaitForExitAsync(context.Token);
    }
}

/// <summary>Plays a sound file.</summary>
public sealed class PlaySoundSubAction : ISubActionBlock
{
    public string Type => "playsound";
    public string Category => "System";
    public string Label => "Play a sound";
    public string Description => "Play an audio file through the default device.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("path", "Sound file", FieldKind.File, ""),
        new FieldSpec("volume", "Volume", FieldKind.Number, "80", "0 to 100.", null, 0, 100)
    };

    public string Describe(SubActionDefinition definition)
    {
        var path = definition.Config.Get("path");
        return path.Length == 0 ? "Play a sound" : $"Play {System.IO.Path.GetFileName(path)}";
    }

    public Task Run(SubActionDefinition definition, ActionContext context)
    {
        var path = context.Field(definition, "path");
        if (path.Length == 0) throw new InvalidOperationException("no sound file set");
        if (!System.IO.File.Exists(path)) throw new InvalidOperationException($"no such file: {path}");

        context.Services.PlaySound(path, Math.Clamp(definition.Config.GetDouble("volume", 80), 0, 100) / 100.0);
        return Task.CompletedTask;
    }
}

/// <summary>Writes a line into this action's log. Mostly for working out what an action is doing.</summary>
public sealed class LogSubAction : ISubActionBlock
{
    public string Type => "log";
    public string Category => "Core";
    public string Label => "Write to the log";
    public string Description => "Put a line in this action's log — useful while building one.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("text", "Text", FieldKind.Text, "", "May contain %arguments%.")
    };

    public string Describe(SubActionDefinition definition)
        => $"Log \"{definition.Config.Get("text")}\"";

    public Task Run(SubActionDefinition definition, ActionContext context)
    {
        context.Log(context.Field(definition, "text"));
        return Task.CompletedTask;
    }
}
