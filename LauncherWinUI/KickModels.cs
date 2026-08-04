using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KickChatSpy.Models;

/// <summary>Envelope Pusher wraps every event in. "data" is itself a JSON string.</summary>
public sealed class PusherMessage
{
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
}

/// <summary>One Kick chat message, as carried by App\ChatMessageEvent.</summary>
public sealed class ChatMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("chatroom_id")] public long ChatroomId { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("sender")] public KickSender? Sender { get; set; }
}

public sealed class KickSender
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("identity")] public KickIdentity? Identity { get; set; }
}

public sealed class KickIdentity
{
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("badges")] public List<KickBadge>? Badges { get; set; }
}

public sealed class KickBadge
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
}

/// <summary>
/// Turns a channel name into its chatroom id, which is what Pusher subscriptions are keyed on.
///
/// Kick's own API (kick.com/api/v2/channels/…) sits behind Cloudflare and answers 403 to
/// anything that isn't a real browser — verified, every channel fails. So this goes through
/// the same bridge service KickChatSpy uses. That is a third-party host: it sees the channel
/// name you look up, nothing else. Entering a numeric chatroom id in the app skips it entirely.
/// </summary>
public sealed class ChatroomLookupService
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://kick-auth-bridge-hdhdftd5a7dth5ge.westeurope-01.azurewebsites.net"),
        Timeout = TimeSpan.FromSeconds(25)   // the bridge cold-starts on Azure
    };

    public async Task<long?> GetChatroomIdAsync(string channelName)
    {
        var slug = channelName.Trim().TrimStart('#').ToLowerInvariant();

        try
        {
            using var response = await Http.GetAsync($"/api/chatroom/{Uri.EscapeDataString(slug)}");
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.TryGetProperty("chatroomId", out var id) && id.TryGetInt64(out var value))
                return value == 0 ? null : value;
        }
        catch (Exception)
        {
            // bridge unreachable or slow to wake; caller reports it
        }

        return null;
    }
}
