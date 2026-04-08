# tryAGI.RealtimeAvatar.Abstractions

[![NuGet](https://img.shields.io/nuget/v/tryAGI.RealtimeAvatar.Abstractions.svg)](https://www.nuget.org/packages/tryAGI.RealtimeAvatar.Abstractions/)
[![CI](https://github.com/tryAGI/RealtimeAvatar/actions/workflows/dotnet.yml/badge.svg)](https://github.com/tryAGI/RealtimeAvatar/actions/workflows/dotnet.yml)

Shared abstractions for realtime avatar streaming providers in .NET.

## Installation

```bash
dotnet add package tryAGI.RealtimeAvatar.Abstractions
```

## Interface

```csharp
using tryAGI.RealtimeAvatar;

IRealtimeAvatarClient avatar = /* provider-specific adapter */;

// Send text or audio
await avatar.SendTextAsync("Hello!");
await avatar.SendAudioAsync(pcm16Bytes);

// Receive video/audio frames
await foreach (var frame in avatar.ReceiveVideoFramesAsync())
{
    // frame.Data, frame.Codec, frame.Timestamp
}
```

## Implementations

| SDK | Adapter | Transport |
|-----|---------|-----------|
| [D-ID](https://github.com/tryAGI/DId) | `DIdRealtimeAvatarClient` | WebRTC (SIPSorcery) |
| [Simli](https://github.com/tryAGI/Simli) | `SimliRealtimeAvatarClient` | WebSocket + WebRTC |

## License

MIT
