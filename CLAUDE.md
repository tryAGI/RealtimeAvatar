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
