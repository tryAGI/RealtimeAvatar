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

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var logger = loggerFactory.CreateLogger("AvatarDemo");

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
        await HandleWebSocketAsync(ws, sessionId, builder.Configuration, cts, logger);
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

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    activeSessions = AvatarSessions.Sessions.Count,
}));

app.Run();

static async Task HandleWebSocketAsync(
    WebSocket ws,
    string sessionId,
    IConfiguration config,
    CancellationTokenSource cts,
    ILogger logger)
{
    logger.LogInformation("Session {SessionId} connected", sessionId);
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
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "WebSocket connection lost for session {SessionId}", sessionId);
            break;
        }

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
                    await HandleConnect(ws, sessionId, config, msg, cts, logger);
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

    logger.LogInformation("Session {SessionId} disconnected", sessionId);
}

static async Task HandleConnect(
    WebSocket ws, string sessionId,
    IConfiguration config, JsonElement msg, CancellationTokenSource cts,
    ILogger logger)
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
                _ = ForwardFramesAsync(ws, avatar.ReceiveVideoFramesAsync(ct),
                    frame => (frame.Codec == "VP8" ? FrameType.VideoVP8 : FrameType.VideoH264, frame.Timestamp, frame.Data), sessionId, "did:video", ct, logger);
                _ = ForwardFramesAsync(ws, avatar.ReceiveAudioFramesAsync(ct),
                    frame => (FrameType.AudioOpus, frame.DurationMs, frame.Data), sessionId, "did:audio", ct, logger);
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

                _ = ForwardFramesAsync(ws, avatar.ReceiveVideoFramesAsync(ct),
                    frame => (frame.Codec == "VP8" ? FrameType.VideoVP8 : FrameType.VideoH264, frame.Timestamp, frame.Data), sessionId, "simli:video", ct, logger);
                _ = ForwardFramesAsync(ws, avatar.ReceiveAudioFramesAsync(ct),
                    frame => (FrameType.AudioOpus, frame.DurationMs, frame.Data), sessionId, "simli:audio", ct, logger);
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
        logger.LogWarning(ex, "Failed to connect provider {Provider} for session {SessionId}", provider, sessionId);
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

// --- Frame forwarding ---

/// <summary>
/// Generic helper that forwards frames from an async enumerable to a WebSocket
/// using the binary protocol: [1:type][4:value_be][N:data].
/// The selector lambda maps each provider-specific frame type to the common tuple.
/// </summary>
static async Task ForwardFramesAsync<T>(
    WebSocket ws,
    IAsyncEnumerable<T> frames,
    Func<T, (byte typeByte, uint headerValue, byte[] data)> selector,
    string sessionId,
    string provider,
    CancellationToken ct,
    ILogger logger)
{
    var frameCount = 0;
    try
    {
        await foreach (var frame in frames.WithCancellation(ct))
        {
            if (ws.State != WebSocketState.Open) break;

            var (typeByte, headerValue, data) = selector(frame);

            var message = new byte[5 + data.Length];
            message[0] = typeByte;
            message[1] = (byte)(headerValue >> 24);
            message[2] = (byte)(headerValue >> 16);
            message[3] = (byte)(headerValue >> 8);
            message[4] = (byte)headerValue;
            data.CopyTo(message, 5);

            await ws.SendAsync(message, WebSocketMessageType.Binary, true, ct);
            frameCount++;
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException ex)
    {
        logger.LogWarning(ex, "WebSocket disconnected during {Provider} frame forwarding for session {SessionId}", provider, sessionId);
    }
    finally
    {
        logger.LogInformation("Forwarded {FrameCount} {Provider} frames for session {SessionId}", frameCount, provider, sessionId);
    }
}

static async Task SendJsonAsync(WebSocket ws, object data, CancellationToken ct)
{
    if (ws.State != WebSocketState.Open) return;
    var json = JsonSerializer.Serialize(data);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
}

/// <summary>
/// Named constants for the binary protocol type byte.
/// Wire format: [1:type][4:value_be][N:data].
/// </summary>
static class FrameType
{
    public const byte VideoH264 = 0x01;
    public const byte VideoVP8  = 0x02;
    public const byte AudioOpus = 0x10;
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
