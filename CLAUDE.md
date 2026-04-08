# CLAUDE.md — RealtimeAvatar Abstractions

## Overview

Shared abstractions (`tryAGI.RealtimeAvatar.Abstractions`) for provider-agnostic realtime avatar streaming. Defines `IRealtimeAvatarClient` interface and frame records used by D-ID, Simli, and other avatar SDK adapters.

## Build

```bash
dotnet build RealtimeAvatar.slnx
```

## Key Files

- `src/libs/RealtimeAvatar/IRealtimeAvatarClient.cs` — Interface + frame records

## Interface

| Member | Description |
|--------|-------------|
| `IsConnected` | Whether the session is currently connected |
| `SendTextAsync(text)` | Send text for the avatar to speak |
| `SendAudioAsync(pcm16Audio)` | Send raw PCM16 audio for lip-sync |
| `ReceiveVideoFramesAsync()` | Stream of encoded video frames |
| `ReceiveAudioFramesAsync()` | Stream of encoded audio frames |

## Implementations

| SDK | Adapter Class | Transport |
|-----|---------------|-----------|
| D-ID | `DIdRealtimeAvatarClient` | WebRTC (SIPSorcery) |
| Simli | `SimliRealtimeAvatarClient` | WebSocket + WebRTC |

## Demo Application

Full-stack demo at `examples/RealtimeAvatarDemo/` showcasing all three providers:

```
Browser <--WebSocket--> ASP.NET Core Backend <--WebRTC/REST--> Avatar Providers
```

### Architecture

- **Backend** (ASP.NET Core): Connects to D-ID and Simli via server-side WebRTC (SIPSorcery), receives encoded H.264/VP8 video + OPUS audio frames, and forwards them to the browser over a WebSocket. AvatarTalk is handled via REST API.
- **Frontend**: Uses WebCodecs API (`VideoDecoder` + `AudioDecoder`) to decode frames received from the WebSocket. Microphone capture sends PCM16 audio for Simli.

### Providers

| Provider | Input | Transport | Output |
|----------|-------|-----------|--------|
| D-ID | Text | WebRTC (server-side SIPSorcery) | H.264/VP8 video + OPUS audio |
| Simli | PCM16 audio (microphone) | WebSocket + WebRTC (server-side SIPSorcery) | H.264 video + OPUS audio |
| AvatarTalk | Text | REST API | MP4 video URL |

### Run

```bash
cd examples/RealtimeAvatarDemo
# Set API keys via environment variables or appsettings.json
export DID_API_KEY="your-key"
export SIMLI_API_KEY="your-key"
export AVATARTALK_API_KEY="your-key"
dotnet run
# Open https://localhost:5001
```

### Build

```bash
cd examples/RealtimeAvatarDemo
dotnet build
```

### Key Files

- `examples/RealtimeAvatarDemo/Program.cs` — ASP.NET Core backend with WebSocket handler
- `examples/RealtimeAvatarDemo/wwwroot/index.html` — Dark-themed UI with three provider cards
- `examples/RealtimeAvatarDemo/wwwroot/js/app.js` — WebCodecs decoders, mic capture, WebSocket client
- `examples/RealtimeAvatarDemo/wwwroot/css/style.css` — Dark theme styling

### Binary Protocol (Backend to Browser)

Video frames: `[1:type][4:timestamp_be][N:encoded_data]` where type `0x01`=H264, `0x02`=VP8

Audio frames: `[1:type][4:duration_ms_be][N:encoded_data]` where type `0x10`=OPUS

### Notes

- Uses `extern alias` for DId and Simli project references to avoid duplicate `tryAGI.RealtimeAvatar` type conflicts
- WebCodecs requires Chrome/Edge 94+ on the browser side
- This is a unique open-source example of server-side WebRTC avatar streaming in C#
