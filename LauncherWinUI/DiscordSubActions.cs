using System.Text.Json.Nodes;

namespace StreamerKit;

// ---------------------------------------------------------------------------
//  Discord
// ---------------------------------------------------------------------------

/// <summary>
/// Shared plumbing for the Discord steps that go through the bot.
///
/// Note what this base class does *not* cover: posting to a webhook. A webhook URL is its own
/// credential and needs no bot at all, so that block sits outside this and works whether or
/// not the gateway is connected — which is the whole reason it is worth having.
/// </summary>
public abstract class DiscordSubActionBase : ISubActionBlock
{
    public abstract string Type { get; }
    public string Category => "Discord";
    public abstract string Label { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<FieldSpec> Fields { get; }
    public abstract string Describe(SubActionDefinition definition);

    protected static readonly FieldSpec ChannelField = new(
        "channel", "Channel id", FieldKind.Text, "",
        "Turn on Discord's Developer Mode, then right-click the channel and Copy Channel ID.");

    public async Task Run(SubActionDefinition definition, ActionContext context)
    {
        var discord = context.Services.Discord;
        if (!discord.IsConnected)
            throw new InvalidOperationException(
                "Discord isn't connected — connect it on the Integrations page.");

        await RunDiscord(definition, context, discord);
    }

    protected abstract Task RunDiscord(SubActionDefinition definition, ActionContext context,
                                       DiscordClient discord);

    /// <summary>Throws with the message Discord gave, so it lands in the action's log verbatim.</summary>
    protected static DiscordClient.DiscordResult Ensure(DiscordClient.DiscordResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        return result;
    }

    /// <summary>Trims a long body down to something that reads well in a one-line summary.</summary>
    protected static string Preview(string text, int length = 40)
    {
        text = (text ?? "").Replace('\n', ' ').Trim();
        return text.Length <= length ? text : text[..(length - 1)] + "…";
    }
}

/// <summary>Posts a plain message to a channel.</summary>
public sealed class DiscordSendSubAction : DiscordSubActionBase
{
    public override string Type => "discordsend";
    public override string Label => "Send a message";
    public override string Description => "Post text to a Discord channel as the bot.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        ChannelField,
        new FieldSpec("message", "Message", FieldKind.LongText, "",
                      "Accepts %arguments% — %user% and %rawInput% from a chat trigger, for instance.")
    };

    public override string Describe(SubActionDefinition definition)
    {
        var message = definition.Config.Get("message");
        return message.Length == 0 ? "Send a Discord message" : $"Discord: \"{Preview(message)}\"";
    }

    protected override async Task RunDiscord(SubActionDefinition definition, ActionContext context,
                                             DiscordClient discord)
    {
        var message = context.Field(definition, "message");
        if (message.Length == 0) throw new InvalidOperationException("no message to send");

        // Discord refuses anything over 2000 characters outright, and an expanded %argument%
        // is easy to overrun. Trimming keeps a long message from losing the whole step.
        if (message.Length > 2000) message = message[..2000];

        Ensure(await discord.SendMessage(context.Field(definition, "channel"), message));
        context.Log($"Discord message sent ({message.Length} characters)");
    }
}

/// <summary>Posts an embed — the boxed, coloured message Discord uses for announcements.</summary>
public sealed class DiscordEmbedSubAction : DiscordSubActionBase
{
    public override string Type => "discordembed";
    public override string Label => "Send an embed";
    public override string Description => "Post a titled, coloured card to a Discord channel.";

    public override IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        ChannelField,
        new FieldSpec("title", "Title", FieldKind.Text, ""),
        new FieldSpec("description", "Description", FieldKind.LongText, ""),
        new FieldSpec("url", "Title links to", FieldKind.Text, "",
                      "Optional. Makes the title clickable — your channel, for a going-live post."),
        new FieldSpec("image", "Image URL", FieldKind.Text, "", "Optional."),
        new FieldSpec("colour", "Colour", FieldKind.Text, "#9443CC", "The stripe down the left edge."),
        new FieldSpec("content", "Text above the embed", FieldKind.Text, "",
                      "Optional. This is the part that can mention a role, e.g. <@&123…>.")
    };

    public override string Describe(SubActionDefinition definition)
    {
        var title = definition.Config.Get("title");
        return title.Length == 0 ? "Send a Discord embed" : $"Discord embed: \"{Preview(title)}\"";
    }

    protected override async Task RunDiscord(SubActionDefinition definition, ActionContext context,
                                             DiscordClient discord)
    {
        var title = context.Field(definition, "title");
        var description = context.Field(definition, "description");
        if (title.Length == 0 && description.Length == 0)
            throw new InvalidOperationException("an embed needs at least a title or a description");

        Ensure(await discord.SendEmbed(
            context.Field(definition, "channel"),
            title,
            description,
            context.Field(definition, "url"),
            context.Field(definition, "colour"),
            context.Field(definition, "image"),
            context.Field(definition, "content")));

        context.Log($"Discord embed sent{(title.Length == 0 ? "" : $": {title}")}");
    }
}

/// <summary>
/// Posts through a channel webhook.
///
/// Deliberately not derived from <see cref="DiscordSubActionBase"/>: a webhook URL carries its
/// own authorisation, so this is the one Discord step that works with no bot, no token and no
/// gateway connection. Requiring one would be a lie about what it needs.
/// </summary>
public sealed class DiscordWebhookSubAction : ISubActionBlock
{
    public string Type => "discordwebhook";
    public string Category => "Discord";
    public string Label => "Post to a webhook";
    public string Description => "Post to a Discord channel through its webhook URL. Needs no bot.";

    public IReadOnlyList<FieldSpec> Fields { get; } = new[]
    {
        new FieldSpec("url", "Webhook URL", FieldKind.Text, "",
                      "Discord: the channel's Edit Channel -> Integrations -> Webhooks. "
                    + "Anyone holding this URL can post to that channel."),
        new FieldSpec("message", "Message", FieldKind.LongText, ""),
        new FieldSpec("username", "Post as", FieldKind.Text, "",
                      "Optional. Overrides the name the webhook was created with."),
        new FieldSpec("avatar", "Avatar URL", FieldKind.Text, "", "Optional.")
    };

    public string Describe(SubActionDefinition definition)
    {
        var message = definition.Config.Get("message").Replace('\n', ' ').Trim();
        if (message.Length == 0) return "Post to a Discord webhook";
        if (message.Length > 40) message = message[..39] + "…";
        return $"Discord webhook: \"{message}\"";
    }

    public async Task Run(SubActionDefinition definition, ActionContext context)
    {
        var message = context.Field(definition, "message");
        if (message.Length == 0) throw new InvalidOperationException("no message to post");
        if (message.Length > 2000) message = message[..2000];

        var body = new JsonObject { ["content"] = message };

        var username = context.Field(definition, "username");
        if (username.Length > 0) body["username"] = username;

        var avatar = context.Field(definition, "avatar");
        if (avatar.Length > 0) body["avatar_url"] = avatar;

        var result = await context.Services.Discord.PostWebhook(context.Field(definition, "url"), body);
        if (!result.Ok) throw new InvalidOperationException(result.Error);

        context.Log("Posted to the Discord webhook");
    }
}
