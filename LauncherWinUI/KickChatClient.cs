using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KickChatSpy.Models;

namespace KickChatSpy;

public class KickChatClient
{
    private static readonly Uri WebSocketUri = new("wss://ws-us2.pusher.com/app/32cbd69e4b950bf97679");

    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private readonly ChatroomLookupService _lookupService = new();

    public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;
    public event Action<ChatMessage> OnMessageReceived;
    
    public async Task ConnectToChatroomAsync(string channelname)
    {
        if (string.IsNullOrWhiteSpace(channelname))
            throw new ArgumentException("Channel name is required.", nameof(channelname));

        var chatroomId = await _lookupService.GetChatroomIdAsync(channelname);
        if (!chatroomId.HasValue)
            throw new InvalidOperationException($"Chatroom '{channelname}' not found.");

        Console.WriteLine($"Found chatroom ID: {chatroomId.Value} for channel '{channelname}'");
        await ConnectToChatroomAsync(chatroomId.Value);
    }

    public async Task ConnectToChatroomAsync(long chatroomNumber)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");

        _ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();

        await _ws.ConnectAsync(WebSocketUri, _cts.Token);
        //Console.WriteLine($"Connected to chatroom: {chatroomNumber}");

        await SubscribeToChannelsAsync(chatroomNumber);

        _ = Task.Run(() => StartReceivingMessagesAsync(_cts.Token));
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected) return;

        _cts?.Cancel();

        try
        {
            await _ws!.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
        }
        catch
        {
            Console.WriteLine("Error while closing WebSocket connection.");
        }

        _ws?.Dispose();
        _ws = null;
        _cts?.Dispose();
        _cts = null;

        Console.WriteLine("Disconnected from chatroom.");
    }

    private async Task SubscribeToChannelsAsync(long chatroomNumber)
    {
        string[] channels =
        [
            $"chatroom_{chatroomNumber}",
            $"chatrooms.{chatroomNumber}.v2"
        ];

        foreach (var channel in channels)
        {
            var payload = new
            {
                @event = "pusher:subscribe",
                data = new
                {
                    auth = "",
                    channel = channel
                }
            };

            string json = JsonSerializer.Serialize(payload);
            var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));

            await _ws.SendAsync(buffer, WebSocketMessageType.Text, true, _cts!.Token);
            //Console.WriteLine($"Subscribed to: {channel}");
        }
    }

    private async Task StartReceivingMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder(); 

        while (!cancellationToken.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                sb.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var json = sb.ToString();

                var pusherMsg = JsonSerializer.Deserialize<PusherMessage>(json);
                if (pusherMsg?.Event?.Contains("ChatMessageEvent") == true)
                {
                    var chatMsg = JsonSerializer.Deserialize<ChatMessage>(pusherMsg.Data);
                    if (chatMsg != null)
                    {
                        OnMessageReceived?.Invoke(chatMsg);
                    }
                }
            }
            catch (JsonException)
            {
                //Console.WriteLine($"[JSON] {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReceiveError] {ex.Message}");
            }
        }

        Console.WriteLine("Stopped receiving messages.");
    }
}