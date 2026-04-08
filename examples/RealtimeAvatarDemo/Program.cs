extern alias DIdSdk;
extern alias SimliSdk;

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DIdSdk::DId;
using SimliSdk::Simli;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    var sessionId = Guid.NewGuid().ToString("N");
    var cts = new CancellationTokenSource();

    try
    {
        await HandleWebSocketAsync(ws, sessionId, builder.Configuration, cts);
    }
    finally
    {
        cts.Cancel();
        if (AvatarSessions.Sessions.TryRemove(sessionId, out var ctx))
        {
            await ctx.DisposeAsync();
        }
    }
});

app.Run();

static async Task HandleWebSocketAsync(
    WebSocket ws,
    string sessionId,
    IConfiguration config,
    CancellationTokenSource cts)
{
    var buffer = new byte[64 * 1024];
    var ct = cts.Token;

    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        WebSocketReceiveResult result;
        try
        {
            result = await ws.ReceiveAsync(buffer, ct);
        }
        catch (OperationCanceledException) { break; }
        catch (WebSocketException) { break; }

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            break;
        }

        if (result.MessageType == WebSocketMessageType.Text)
        {
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var msg = JsonSerializer.Deserialize<JsonElement>(json);
            var action = msg.GetProperty("action").GetString();

            switch (action)
            {
                case "connect":
                    await HandleConnect(ws, sessionId, config, msg, cts);
                    break;
                case "send_text":
                    await HandleSendText(ws, sessionId, msg, ct);
                    break;
                case "send_audio":
                    // Text-encoded audio (base64) -- prefer binary WebSocket frames instead
                    break;
                case "disconnect":
                    await HandleDisconnect(sessionId, ws);
                    break;
            }
        }
        else if (result.MessageType == WebSocketMessageType.Binary)
        {
            // Binary = PCM16 audio for Simli
            await HandleSendAudio(sessionId, buffer.AsMemory(0, result.Count), ct);
        }
    }
}

static async Task HandleConnect(
    WebSocket ws, string sessionId,
    IConfiguration config, JsonElement msg, CancellationTokenSource cts)
{
    var provider = msg.GetProperty("provider").GetString()!;
    var ct = cts.Token;

    try
    {
        switch (provider)
        {
            case "did":
            {
                var apiKey = config["DId:ApiKey"] is { Length: > 0 } cfgKey
                    ? cfgKey
                    : global::System.Environment.GetEnvironmentVariable("DID_API_KEY") ?? "";
                var agentId = msg.GetProperty("agentId").GetString()!;
                var client = new DIdClient(apiKey);
                var avatar = await DIdRealtimeAvatarClient.ConnectAsync(
                    client, agentId, cancellationToken: ct);
                var ctx = new AvatarSession(provider, didClient: avatar);
                AvatarSessions.Sessions[sessionId] = ctx;

                await SendJsonAsync(ws, new { @event = "connected", provider }, ct);

                // Start frame forwarding in background
                _ = ForwardDIdVideoFrames(ws, avatar, ct);
                _ = ForwardDIdAudioFrames(ws, avatar, ct);
                break;
            }
            case "simli":
            {
                var apiKey = config["Simli:ApiKey"] is { Length: > 0 } cfgKey
                    ? cfgKey
                    : global::System.Environment.GetEnvironmentVariable("SIMLI_API_KEY") ?? "";
                var faceId = msg.GetProperty("faceId").GetString()!;
                var client = new SimliClient(apiKey);
                var avatar = await SimliRealtimeAvatarClient.ConnectAsync(
                    client, faceId, cancellationToken: ct);
                var ctx = new AvatarSession(provider, simliClient: avatar);
                AvatarSessions.Sessions[sessionId] = ctx;

                await SendJsonAsync(ws, new { @event = "connected", provider }, ct);

                _ = ForwardSimliVideoFrames(ws, avatar, ct);
                _ = ForwardSimliAudioFrames(ws, avatar, ct);
                break;
            }
            case "avatartalk":
            {
                var apiKey = config["AvatarTalk:ApiKey"] is { Length: > 0 } cfgKey
                    ? cfgKey
                    : global::System.Environment.GetEnvironmentVariable("AVATARTALK_API_KEY") ?? "";
                var avatarTalkClient = new AvatarTalk.AvatarTalkClient(apiKey);
                var ctx = new AvatarSession(provider, avatarTalkClient: avatarTalkClient);
                AvatarSessions.Sessions[sessionId] = ctx;
                await SendJsonAsync(ws, new { @event = "connected", provider }, ct);
                break;
            }
        }
    }
    catch (Exception ex)
    {
        await SendJsonAsync(ws, new { @event = "error", message = ex.Message }, ct);
    }
}

static async Task HandleSendText(
    WebSocket ws, string sessionId,
    JsonElement msg, CancellationToken ct)
{
    if (!AvatarSessions.Sessions.TryGetValue(sessionId, out var ctx)) return;
    var text = msg.GetProperty("text").GetString()!;

    try
    {
        switch (ctx.Provider)
        {
            case "did" when ctx.DIdClient is not null:
                await ctx.DIdClient.SendTextAsync(text, ct);
                await SendJsonAsync(ws, new { @event = "text_sent", text }, ct);
                break;

            case "simli":
                await SendJsonAsync(ws, new
                {
                    @event = "error",
                    message = "Simli uses audio input only. Use the microphone to send PCM16 audio."
                }, ct);
                break;

            case "avatartalk" when ctx.AvatarTalkClient is not null:
            {
                await SendJsonAsync(ws, new { @event = "generating", provider = "avatartalk" }, ct);

                var avatarName = AvatarTalk.AvatarName.EuropeanMan;
                if (msg.TryGetProperty("avatar", out var av))
                {
                    var parsed = AvatarTalk.AvatarNameExtensions.ToEnum(av.GetString() ?? "");
                    if (parsed.HasValue) avatarName = parsed.Value;
                }

                var emotion = AvatarTalk.Emotion.Neutral;
                if (msg.TryGetProperty("emotion", out var em))
                {
                    var parsed = AvatarTalk.EmotionExtensions.ToEnum(em.GetString() ?? "");
                    if (parsed.HasValue) emotion = parsed.Value;
                }

                var response = await ctx.AvatarTalkClient.GenerateVideoAsync(
                    text: text,
                    avatar: avatarName,
                    emotion: emotion,
                    cancellationToken: ct);

                await SendJsonAsync(ws, new
                {
                    @event = "video_ready",
                    provider = "avatartalk",
                    mp4Url = response.Mp4Url,
                    htmlUrl = response.HtmlUrl,
                    status = response.Status,
                    videoDurationSeconds = response.VideoDurationSeconds,
                }, ct);
                break;
            }
        }
    }
    catch (NotSupportedException)
    {
        await SendJsonAsync(ws, new
        {
            @event = "error",
            message = $"{ctx.Provider} does not support text input. Use audio."
        }, ct);
    }
    catch (Exception ex)
    {
        await SendJsonAsync(ws, new { @event = "error", message = ex.Message }, ct);
    }
}

static async Task HandleSendAudio(
    string sessionId, ReadOnlyMemory<byte> audio, CancellationToken ct)
{
    if (!AvatarSessions.Sessions.TryGetValue(sessionId, out var ctx)) return;

    try
    {
        if (ctx.SimliClient is not null)
        {
            await ctx.SimliClient.SendAudioAsync(audio, ct);
        }
    }
    catch
    {
        // Ignore audio send failures silently
    }
}

static async Task HandleDisconnect(string sessionId, WebSocket ws)
{
    if (AvatarSessions.Sessions.TryRemove(sessionId, out var ctx))
    {
        await ctx.DisposeAsync();
    }
    await SendJsonAsync(ws, new { @event = "disconnected" }, CancellationToken.None);
}

// --- D-ID frame forwarding ---

static async Task ForwardDIdVideoFrames(
    WebSocket ws, DIdRealtimeAvatarClient avatar, CancellationToken ct)
{
    try
    {
        await foreach (var frame in avatar.ReceiveVideoFramesAsync(ct))
        {
            if (ws.State != WebSocketState.Open) break;

            // Binary protocol: [1:type][4:timestamp_be][N:data]
            // type: 0x01=H264, 0x02=VP8
            byte typeByte = frame.Codec switch
            {
                "VP8" => 0x02,
                _ => 0x01
            };
            var message = new byte[5 + frame.Data.Length];
            message[0] = typeByte;
            message[1] = (byte)(frame.Timestamp >> 24);
            message[2] = (byte)(frame.Timestamp >> 16);
            message[3] = (byte)(frame.Timestamp >> 8);
            message[4] = (byte)(frame.Timestamp);
            frame.Data.CopyTo(message, 5);

            await ws.SendAsync(message, WebSocketMessageType.Binary, true, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
}

static async Task ForwardDIdAudioFrames(
    WebSocket ws, DIdRealtimeAvatarClient avatar, CancellationToken ct)
{
    try
    {
        await foreach (var frame in avatar.ReceiveAudioFramesAsync(ct))
        {
            if (ws.State != WebSocketState.Open) break;

            // Binary protocol: [1:type][4:duration_ms_be][N:data]
            // type: 0x10=OPUS
            var message = new byte[5 + frame.Data.Length];
            message[0] = 0x10;
            message[1] = (byte)(frame.DurationMs >> 24);
            message[2] = (byte)(frame.DurationMs >> 16);
            message[3] = (byte)(frame.DurationMs >> 8);
            message[4] = (byte)(frame.DurationMs);
            frame.Data.CopyTo(message, 5);

            await ws.SendAsync(message, WebSocketMessageType.Binary, true, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
}

// --- Simli frame forwarding ---

static async Task ForwardSimliVideoFrames(
    WebSocket ws, SimliRealtimeAvatarClient avatar, CancellationToken ct)
{
    try
    {
        await foreach (var frame in avatar.ReceiveVideoFramesAsync(ct))
        {
            if (ws.State != WebSocketState.Open) break;

            byte typeByte = frame.Codec switch
            {
                "VP8" => 0x02,
                _ => 0x01
            };
            var message = new byte[5 + frame.Data.Length];
            message[0] = typeByte;
            message[1] = (byte)(frame.Timestamp >> 24);
            message[2] = (byte)(frame.Timestamp >> 16);
            message[3] = (byte)(frame.Timestamp >> 8);
            message[4] = (byte)(frame.Timestamp);
            frame.Data.CopyTo(message, 5);

            await ws.SendAsync(message, WebSocketMessageType.Binary, true, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
}

static async Task ForwardSimliAudioFrames(
    WebSocket ws, SimliRealtimeAvatarClient avatar, CancellationToken ct)
{
    try
    {
        await foreach (var frame in avatar.ReceiveAudioFramesAsync(ct))
        {
            if (ws.State != WebSocketState.Open) break;

            var message = new byte[5 + frame.Data.Length];
            message[0] = 0x10;
            message[1] = (byte)(frame.DurationMs >> 24);
            message[2] = (byte)(frame.DurationMs >> 16);
            message[3] = (byte)(frame.DurationMs >> 8);
            message[4] = (byte)(frame.DurationMs);
            frame.Data.CopyTo(message, 5);

            await ws.SendAsync(message, WebSocketMessageType.Binary, true, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
}

static async Task SendJsonAsync(WebSocket ws, object data, CancellationToken ct)
{
    if (ws.State != WebSocketState.Open) return;
    var json = JsonSerializer.Serialize(data);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
}

/// <summary>
/// Static holder for active avatar sessions, keyed by session ID.
/// </summary>
static class AvatarSessions
{
    public static readonly ConcurrentDictionary<string, AvatarSession> Sessions = new();
}

/// <summary>
/// Holds provider-specific client references for an active avatar session.
/// </summary>
sealed class AvatarSession : IAsyncDisposable
{
    public string Provider { get; }
    public DIdRealtimeAvatarClient? DIdClient { get; }
    public SimliRealtimeAvatarClient? SimliClient { get; }
    public AvatarTalk.AvatarTalkClient? AvatarTalkClient { get; }

    public AvatarSession(
        string provider,
        DIdRealtimeAvatarClient? didClient = null,
        SimliRealtimeAvatarClient? simliClient = null,
        AvatarTalk.AvatarTalkClient? avatarTalkClient = null)
    {
        Provider = provider;
        DIdClient = didClient;
        SimliClient = simliClient;
        AvatarTalkClient = avatarTalkClient;
    }

    public async ValueTask DisposeAsync()
    {
        if (DIdClient is not null) await DIdClient.DisposeAsync();
        if (SimliClient is not null) await SimliClient.DisposeAsync();
        AvatarTalkClient?.Dispose();
    }
}
