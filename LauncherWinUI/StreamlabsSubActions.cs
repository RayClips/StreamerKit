using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamerKit;

// ---------------------------------------------------------------------------
//  Streamlabs Desktop
// ---------------------------------------------------------------------------

/// <summary>
/// Shared plumbing for the Streamlabs steps.
///
/// Streamlabs is resource-oriented where obs-websocket is request-oriented: instead of
/// "SetCurrentProgramScene(sceneName)" you call a method on a *resource id*, and the ids are
/// opaque strings handed out by <c>getScenes</c> — a scene is <c>scene_&lt;guid&gt;</c>, a
/// scene item is <c>SceneItem["scene","item","source"]</c>. Nothing accepts a plain name, so
/// every step here starts by resolving the names the user typed against a fresh snapshot.
/// That snapshot is deliberately not cached: scenes get renamed and sources get added while
/// an action sits enabled, and a stale id fails in a way that reads like the step is broken.
/// </summary>
public abstract class StreamlabsSubActionBase : ISubActionBlock
{
    public abstract string Type { get; }
    public string Category => "Streamlabs";
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

    protected static readonly FieldChoice[] Muting =
    {
        new("mute", "Mute"),
        new("unmute", "Unmute"),
        new("toggle", "Toggle")
    };

    protected static readonly FieldSpec SceneField = new("scene", "Scene", FieldKind.Text, "");
    protected static readonly FieldSpec SourceField = new("source", "Source", FieldKind.Text, "");

    public async Task Run(SubActionDefinition definition, ActionContext context)
    {
        var streamlabs = context.Services.Streamlabs;
        if (!streamlabs.IsConnected)
            throw new InvalidOperationException(
                "Streamlabs isn't connected — connect it on the Integrations page.");

        await RunStreamlabs(definition, context, streamlabs);
    }

    protected abstract Task RunStreamlabs(SubActionDefinition definition, ActionContext context,
                                          StreamlabsClient streamlabs);

    /// <summary>Throws with the message Streamlabs gave, so it lands in the action's log verbatim.</summary>
    protected static StreamlabsClient.SlabsResult Ensure(StreamlabsClient.SlabsResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        return result;
    }

    /// <summary>
    /// The scene and source the user typed, resolved to something callable.
    ///
    /// Name resolution and its caching live on the client, because the cache has to be
    /// thrown away when the connection changes and because the throttle it exists to dodge
    /// is a fact about Streamlabs, not about any one block.
    /// </summary>
    protected static async Task<StreamlabsClient.SceneItemRef> Item(
        SubActionDefinition definition, ActionContext context, StreamlabsClient streamlabs)
    {
        var scene = context.Field(definition, "scene");
        var source = context.Field(definition, "source");
        if (scene.Length == 0 || source.Length == 0)
            throw new InvalidOperationException("both a scene and a source are needed");

        return await streamlabs.Item(scene, source);
    }
}

/// <summary>Switches the active scene.</summary>
public sealed class StreamlabsSceneSubAction : StreamlabsSubActionBase
{
    public override string Type => "slabsscene";
    public override string Label => "Set scene";
    public override string Description => "Switch Streamlabs to a scene.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("scene", "Scene name", FieldKind.Text, "", "Exactly as it appears in Streamlabs.")
    };

    public override string Describe(SubActionDefinition definition)
    {
        var scene = definition.Config.Get("scene");
        return scene.Length == 0 ? "Set scene" : $"Set scene to \"{scene}\"";
    }

    protected override async Task RunStreamlabs(SubActionDefinition definition, ActionContext context,
                                                StreamlabsClient streamlabs)
    {
        var name = context.Field(definition, "scene");
        if (name.Length == 0) throw new InvalidOperationException("no scene name set");

        // makeSceneActive takes the scene's id, never its name.
        var id = await streamlabs.SceneId(name);
        Ensure(await streamlabs.Call("ScenesService", "makeSceneActive", new JsonArray { id }));

        context.Log($"Streamlabs scene → {name}");
    }
}

/// <summary>Shows or hides one source inside a scene.</summary>
public sealed class StreamlabsSourceSubAction : StreamlabsSubActionBase
{
    public override string Type => "slabssource";
    public override string Label => "Show or hide a source";
    public override string Description => "Toggle a source's visibility in a Streamlabs scene.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        SceneField,
        SourceField,
        new FieldSpec("state", "Action", FieldKind.Choice, "toggle", null, Visibility)
    };

    public override string Describe(SubActionDefinition definition)
    {
        var state = definition.Config.Get("state", "toggle");
        var label = Visibility.FirstOrDefault(v => v.Value == state)?.Label ?? state;
        var source = definition.Config.Get("source");
        return source.Length == 0 ? "Show or hide a source" : $"{label} \"{source}\"";
    }

    protected override async Task RunStreamlabs(SubActionDefinition definition, ActionContext context,
                                                StreamlabsClient streamlabs)
    {
        var item = await Item(definition, context, streamlabs);

        // The lookup already read the item, so toggling costs no extra call.
        var wanted = definition.Config.Get("state", "toggle") switch
        {
            "show" => true,
            "hide" => false,
            _ => !item.Visible
        };

        Ensure(await streamlabs.Call(item.Resource, "setVisibility", new JsonArray { wanted }));
        context.Log($"Streamlabs {(wanted ? "showed" : "hid")} "
                    + $"\"{context.Field(definition, "source")}\" in \"{context.Field(definition, "scene")}\"");
    }
}

/// <summary>Puts a source at a fixed spot on the canvas.</summary>
public sealed class StreamlabsMoveSourceSubAction : StreamlabsSubActionBase
{
    public override string Type => "slabsmove";
    public override string Label => "Move a source";
    public override string Description => "Put a source at a position on the Streamlabs canvas.";

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

    protected override async Task RunStreamlabs(SubActionDefinition definition, ActionContext context,
                                                StreamlabsClient streamlabs)
    {
        var item = await Item(definition, context, streamlabs);
        var x = definition.Config.GetDouble("x");
        var y = definition.Config.GetDouble("y");

        await SlabsGlide.To(streamlabs, item.Resource, item.Position, x, y,
                            definition.Config.GetInt("glide"), context.Token);

        context.Log($"Streamlabs moved \"{context.Field(definition, "source")}\" to ({x:0}, {y:0})");
    }
}

/// <summary>
/// Sends a source to whichever of two spots it isn't currently at.
///
/// The Streamlabs twin of the OBS step, and the one that matters here: this machine runs
/// Streamlabs Desktop rather than OBS, so the cam-dodge action needs this to work at all.
/// It reads where the source is now, which is what lets the action stay stateless — nothing
/// drifts out of sync if the scene is dragged around by hand.
/// </summary>
public sealed class StreamlabsFlipSourceSubAction : StreamlabsSubActionBase
{
    public override string Type => "slabsflip";
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

    protected override async Task RunStreamlabs(SubActionDefinition definition, ActionContext context,
                                                StreamlabsClient streamlabs)
    {
        var item = await Item(definition, context, streamlabs);
        var current = item.Position;

        var ax = definition.Config.GetDouble("ax");
        var ay = definition.Config.GetDouble("ay");
        var bx = definition.Config.GetDouble("bx", 1300);
        var by = definition.Config.GetDouble("by");

        // Whichever spot it is further from is the one it should go to.
        var toA = Distance(current.X, current.Y, ax, ay);
        var toB = Distance(current.X, current.Y, bx, by);
        var (targetX, targetY, which) = toA > toB ? (ax, ay, "A") : (bx, by, "B");

        await SlabsGlide.To(streamlabs, item.Resource, current, targetX, targetY,
                            definition.Config.GetInt("glide", 250), context.Token);

        context.Arguments["flippedTo"] = which;
        context.Log($"Streamlabs flipped \"{context.Field(definition, "source")}\" "
                    + $"to spot {which} ({targetX:0}, {targetY:0})");
    }

    private static double Distance(double x1, double y1, double x2, double y2)
        => Math.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));
}

/// <summary>Mutes or unmutes one of the audio sources on the mixer.</summary>
public sealed class StreamlabsMuteSubAction : StreamlabsSubActionBase
{
    public override string Type => "slabsmute";
    public override string Label => "Mute or unmute audio";
    public override string Description => "Mute a Streamlabs audio source by the name on the mixer.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("source", "Audio source", FieldKind.Text, "",
                      "As named on the Streamlabs mixer, e.g. Desktop Audio."),
        new FieldSpec("state", "Action", FieldKind.Choice, "toggle", null, Muting)
    };

    public override string Describe(SubActionDefinition definition)
    {
        var state = definition.Config.Get("state", "toggle");
        var label = Muting.FirstOrDefault(m => m.Value == state)?.Label ?? state;
        var source = definition.Config.Get("source");
        return source.Length == 0 ? "Mute or unmute audio" : $"{label} \"{source}\"";
    }

    protected override async Task RunStreamlabs(SubActionDefinition definition, ActionContext context,
                                                StreamlabsClient streamlabs)
    {
        var name = context.Field(definition, "source");
        if (name.Length == 0) throw new InvalidOperationException("no audio source set");

        var sources = Ensure(await streamlabs.Call("AudioService", "getSources")).Result as JsonArray
                      ?? throw new InvalidOperationException("Streamlabs returned no audio sources");

        JsonObject? match = null;
        foreach (var source in sources)
            if (source is JsonObject entry &&
                string.Equals(entry["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                match = entry;
                break;
            }

        if (match is null)
        {
            var known = string.Join(", ", sources.Select(s => $"\"{s?["name"]}\""));
            throw new InvalidOperationException($"no audio source called \"{name}\" — the mixer has {known}");
        }

        var wanted = definition.Config.Get("state", "toggle") switch
        {
            "mute" => true,
            "unmute" => false,
            _ => !(match["muted"]?.GetValue<bool>() ?? false)
        };

        var resource = match["resourceId"]?.ToString()
                       ?? throw new InvalidOperationException("Streamlabs gave that source no resource id");

        Ensure(await streamlabs.Call(resource, "setMuted", new JsonArray { wanted }));
        context.Log($"Streamlabs {(wanted ? "muted" : "unmuted")} \"{name}\"");
    }
}

/// <summary>Any Streamlabs method, for everything these blocks don't cover.</summary>
public sealed class StreamlabsRawSubAction : StreamlabsSubActionBase
{
    public override string Type => "slabsraw";
    public override string Label => "Raw Streamlabs call";
    public override string Description => "Any Streamlabs API method, with your own arguments.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("resource", "Resource", FieldKind.Text, "ScenesService",
                      "A service name, or a resource id such as SceneItem[\"…\",\"…\",\"…\"]."),
        new FieldSpec("method", "Method", FieldKind.Text, "getScenes",
                      "e.g. makeSceneActive, toggleStreaming, toggleRecording."),
        new FieldSpec("args", "Arguments", FieldKind.LongText, "[]",
                      "A JSON array, in the order the method takes them. May contain %arguments%."),
        new FieldSpec("saveTo", "Save response to", FieldKind.Text, "",
                      "Optional argument name to hold the JSON that comes back.")
    };

    public override string Describe(SubActionDefinition definition)
        => $"Streamlabs {definition.Config.Get("resource", "ScenesService")}."
           + definition.Config.Get("method", "getScenes");

    protected override async Task RunStreamlabs(SubActionDefinition definition, ActionContext context,
                                                StreamlabsClient streamlabs)
    {
        var resource = context.Field(definition, "resource");
        var method = context.Field(definition, "method");
        if (resource.Length == 0 || method.Length == 0)
            throw new InvalidOperationException("both a resource and a method are needed");

        JsonArray args;
        var raw = context.Field(definition, "args", "[]").Trim();
        try
        {
            args = raw.Length == 0 ? new JsonArray() : JsonNode.Parse(raw) as JsonArray ?? new JsonArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"arguments aren't valid JSON — {ex.Message}");
        }

        var result = Ensure(await streamlabs.Call(resource, method, args));

        var saveTo = definition.Config.Get("saveTo").Trim();
        if (saveTo.Length > 0) context.Arguments[saveTo] = result.Result?.ToJsonString() ?? "";
    }
}

/// <summary>
/// Eases a Streamlabs source from where it is to where it should be.
///
/// Separate from the OBS <c>Glide</c> because the two APIs address items differently, and
/// because the start position is already known here — it came back with the scene, so the
/// glide costs no extra read.
/// </summary>
internal static class SlabsGlide
{
    public static async Task To(StreamlabsClient streamlabs, string resourceId, (double X, double Y) start,
                                double x, double y, int milliseconds, CancellationToken token)
    {
        if (milliseconds <= 0)
        {
            await Set(streamlabs, resourceId, x, y);
            return;
        }

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

            await Set(streamlabs, resourceId,
                      start.X + ((x - start.X) * eased),
                      start.Y + ((y - start.Y) * eased));

            if (frame < frames) await Task.Delay(delay, token);
        }
    }

    private static Task Set(StreamlabsClient streamlabs, string resourceId, double x, double y)
        => streamlabs.Call(resourceId, "setTransform", new JsonArray
        {
            new JsonObject { ["position"] = new JsonObject { ["x"] = x, ["y"] = y } }
        });
}
