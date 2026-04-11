// =============================================================================
// Realtime Avatar Demo -- Frontend
//
// Architecture:
//   Browser <--WebSocket--> ASP.NET Core Backend <--WebRTC/REST--> Providers
//
// Binary protocol (backend -> browser):
//   Video: [1:type][4:timestamp_be][N:data]  type: FRAME_TYPE.VIDEO_H264, VIDEO_VP8
//   Audio: [1:type][4:value_be][N:data]      type: FRAME_TYPE.AUDIO_OPUS
//
// The browser uses WebCodecs API (VideoDecoder + AudioDecoder) to decode
// the encoded frames received over the WebSocket.
// =============================================================================

"use strict";

// ---------------------------------------------------------------------------
// Binary protocol frame types (must match backend FrameType class)
// ---------------------------------------------------------------------------

const FRAME_TYPE = Object.freeze({
    VIDEO_H264: 0x01,
    VIDEO_VP8:  0x02,
    AUDIO_OPUS: 0x10,
});

// ---------------------------------------------------------------------------
// State per provider
// ---------------------------------------------------------------------------

const state = {
    did:        { ws: null, videoDecoder: null, audioDecoder: null, audioCtx: null, configured: false, micStream: null },
    simli:      { ws: null, videoDecoder: null, audioDecoder: null, audioCtx: null, configured: false, micStream: null },
    avatartalk: { ws: null, videoDecoder: null, audioDecoder: null, audioCtx: null, configured: false, micStream: null },
};

// Audio playback buffer queue (simple ring buffer approach)
const audioQueues = { did: [], simli: [], avatartalk: [] };

// Video frame timestamp counter (for WebCodecs)
const videoTimestamps = { did: 0, simli: 0, avatartalk: 0 };

// ---------------------------------------------------------------------------
// WebCodecs availability check
// ---------------------------------------------------------------------------

const hasWebCodecs = typeof VideoDecoder !== "undefined" && typeof AudioDecoder !== "undefined";

if (!hasWebCodecs) {
    document.querySelectorAll(".media-placeholder span:last-child").forEach(el => {
        el.textContent = "WebCodecs API not available in this browser. Use Chrome/Edge 94+.";
    });
}

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------

function log(provider, message, cls) {
    const el = document.getElementById(`${provider}-log`);
    if (!el) return;
    const time = new Date().toLocaleTimeString("en-US", { hour12: false });
    const span = document.createElement("span");
    span.innerHTML = `<span class="log-time">[${time}]</span> <span class="${cls || ""}">${escapeHtml(message)}</span>\n`;
    el.appendChild(span);
    el.scrollTop = el.scrollHeight;
}

function escapeHtml(str) {
    const div = document.createElement("div");
    div.textContent = str;
    return div.innerHTML;
}

// ---------------------------------------------------------------------------
// Status UI
// ---------------------------------------------------------------------------

function setStatus(provider, status, text) {
    const dot = document.getElementById(`${provider}-status-dot`);
    const label = document.getElementById(`${provider}-status-text`);
    if (dot) { dot.className = "status-dot " + status; }
    if (label) { label.textContent = text; }
}

function setButtons(provider, connected) {
    const conn = document.getElementById(`${provider}-connect`);
    const disc = document.getElementById(`${provider}-disconnect`);
    if (conn) conn.disabled = connected;
    if (disc) disc.disabled = !connected;
}

// ---------------------------------------------------------------------------
// WebSocket Connection
// ---------------------------------------------------------------------------

function getWsUrl() {
    const proto = location.protocol === "https:" ? "wss" : "ws";
    return `${proto}://${location.host}/ws`;
}

function connectProvider(provider) {
    if (state[provider].ws) return;

    setStatus(provider, "connecting", "Connecting...");
    log(provider, "Opening WebSocket connection...", "log-event");

    const ws = new WebSocket(getWsUrl());
    ws.binaryType = "arraybuffer";
    state[provider].ws = ws;

    ws.onopen = () => {
        log(provider, "WebSocket open. Sending connect request.", "log-event");

        let msg;
        switch (provider) {
            case "did":
                msg = {
                    action: "connect",
                    provider: "did",
                    agentId: document.getElementById("did-agent-id").value.trim(),
                };
                break;
            case "simli":
                msg = {
                    action: "connect",
                    provider: "simli",
                    faceId: document.getElementById("simli-face-id").value.trim(),
                };
                break;
            case "avatartalk":
                msg = {
                    action: "connect",
                    provider: "avatartalk",
                };
                break;
        }
        ws.send(JSON.stringify(msg));
    };

    ws.onmessage = (evt) => {
        if (typeof evt.data === "string") {
            handleTextMessage(provider, JSON.parse(evt.data));
        } else {
            handleBinaryMessage(provider, evt.data);
        }
    };

    ws.onerror = () => {
        log(provider, "WebSocket error.", "log-error");
        setStatus(provider, "error", "Error");
    };

    ws.onclose = () => {
        log(provider, "WebSocket closed.", "log-event");
        cleanupProvider(provider);
        setStatus(provider, "", "Disconnected");
        setButtons(provider, false);
    };
}

function disconnectProvider(provider) {
    const ws = state[provider].ws;
    if (!ws) return;
    try {
        ws.send(JSON.stringify({ action: "disconnect" }));
        ws.close();
    } catch { /* ignore */ }
    cleanupProvider(provider);
    setStatus(provider, "", "Disconnected");
    setButtons(provider, false);
    log(provider, "Disconnected.", "log-event");
}

function cleanupProvider(provider) {
    const s = state[provider];
    s.ws = null;
    s.configured = false;
    if (s.videoDecoder) {
        try { s.videoDecoder.close(); } catch { /* ignore */ }
        s.videoDecoder = null;
    }
    if (s.audioDecoder) {
        try { s.audioDecoder.close(); } catch { /* ignore */ }
        s.audioDecoder = null;
    }
    if (s.audioCtx) {
        try { s.audioCtx.close(); } catch { /* ignore */ }
        s.audioCtx = null;
    }
    if (s.micStream) {
        s.micStream.getTracks().forEach(t => t.stop());
        s.micStream = null;
        const micBtn = document.getElementById(`${provider}-mic`);
        if (micBtn) micBtn.classList.remove("recording");
    }
    audioQueues[provider] = [];
    videoTimestamps[provider] = 0;
}

// ---------------------------------------------------------------------------
// Handle text (JSON) messages from backend
// ---------------------------------------------------------------------------

function handleTextMessage(provider, msg) {
    switch (msg.event) {
        case "connected":
            setStatus(provider, "connected", "Connected");
            setButtons(provider, true);
            log(provider, `Connected to ${msg.provider} provider.`, "log-success");
            // Hide placeholder
            const ph = document.getElementById(`${provider}-placeholder`);
            if (ph) ph.style.display = "none";
            // Initialize WebCodecs decoders for WebRTC providers
            if (provider === "did" || provider === "simli") {
                initVideoDecoder(provider);
                initAudioDecoder(provider);
            }
            break;

        case "text_sent":
            log(provider, `Text sent: "${msg.text}"`, "log-success");
            break;

        case "generating":
            log(provider, "Generating avatar video...", "log-event");
            setStatus(provider, "connecting", "Generating...");
            break;

        case "video_ready":
            log(provider, `Video ready: ${msg.mp4Url || "(no URL)"}`, "log-success");
            setStatus(provider, "connected", "Connected");
            if (msg.mp4Url && provider === "avatartalk") {
                showAvatarTalkVideo(msg.mp4Url);
            }
            break;

        case "status":
            log(provider, msg.message, "log-event");
            break;

        case "error":
            log(provider, `Error: ${msg.message}`, "log-error");
            setStatus(provider, "error", "Error");
            break;

        case "disconnected":
            log(provider, "Session disconnected by server.", "log-event");
            break;

        default:
            log(provider, `Unknown event: ${JSON.stringify(msg)}`, "log-event");
    }
}

// ---------------------------------------------------------------------------
// Handle binary messages (encoded video/audio frames)
// ---------------------------------------------------------------------------

function handleBinaryMessage(provider, data) {
    if (data.byteLength < 5) return;

    const view = new DataView(data);
    const typeByte = view.getUint8(0);
    const value = view.getUint32(1, false); // big-endian
    const payload = new Uint8Array(data, 5);

    if (typeByte === FRAME_TYPE.VIDEO_H264 || typeByte === FRAME_TYPE.VIDEO_VP8) {
        // Video frame
        handleVideoFrame(provider, typeByte, value, payload);
    } else if (typeByte === FRAME_TYPE.AUDIO_OPUS) {
        // Audio frame
        handleAudioFrame(provider, value, payload);
    } else {
        const hex = '0x' + typeByte.toString(16).padStart(2, '0');
        console.warn(`Unknown frame type: ${hex}`);
        log(provider, `Unknown frame type: ${hex}`, 'log-error');
    }
}

// ---------------------------------------------------------------------------
// WebCodecs: Video Decoder
// ---------------------------------------------------------------------------

function initVideoDecoder(provider) {
    if (!hasWebCodecs) return;

    const canvas = document.getElementById(`${provider}-canvas`);
    if (!canvas) return;
    const ctx2d = canvas.getContext("2d");

    const decoder = new VideoDecoder({
        output: (frame) => {
            // Draw decoded VideoFrame onto canvas
            canvas.width = frame.displayWidth;
            canvas.height = frame.displayHeight;
            ctx2d.drawImage(frame, 0, 0);
            frame.close();
        },
        error: (err) => {
            log(provider, `Video decoder error: ${err.message}`, "log-error");
        },
    });

    state[provider].videoDecoder = decoder;
    state[provider].configured = false;
}

function handleVideoFrame(provider, typeByte, timestamp, payload) {
    const s = state[provider];
    if (!s.videoDecoder || s.videoDecoder.state === "closed") return;

    const isH264 = typeByte === FRAME_TYPE.VIDEO_H264;
    const isVP8 = typeByte === FRAME_TYPE.VIDEO_VP8;

    if (isH264) {
        handleH264Frame(provider, timestamp, payload);
    } else if (isVP8) {
        handleVP8Frame(provider, timestamp, payload);
    }
}

function handleH264Frame(provider, timestamp, payload) {
    const s = state[provider];

    // Detect keyframe by looking for IDR NAL units (type 5) or SPS (type 7)
    const isKeyframe = detectH264Keyframe(payload);

    if (!s.configured) {
        if (!isKeyframe) {
            // Wait for a keyframe to configure the decoder
            return;
        }
        // Extract SPS/PPS from keyframe to create AVCC description
        const description = extractAvccDescription(payload);
        if (!description) {
            log(provider, "Could not extract SPS/PPS from H.264 keyframe.", "log-error");
            return;
        }
        try {
            s.videoDecoder.configure({
                codec: "avc1.42E01E",
                description: description,
                optimizeForLatency: true,
            });
            s.configured = true;
            log(provider, "H.264 video decoder configured.", "log-success");
        } catch (err) {
            log(provider, `Failed to configure H.264 decoder: ${err.message}`, "log-error");
            return;
        }
    }

    try {
        // Strip SPS/PPS NALs from the data, keep only VCL NALs for the chunk
        const vclData = isKeyframe ? stripParameterSets(payload) : payload;
        if (vclData.length === 0) return;

        const ts = videoTimestamps[provider];
        videoTimestamps[provider] += 33333; // ~30fps in microseconds

        const chunk = new EncodedVideoChunk({
            type: isKeyframe ? "key" : "delta",
            timestamp: ts,
            data: vclData,
        });
        s.videoDecoder.decode(chunk);
    } catch (err) {
        // Decoding errors are common during initial sync; silently skip
    }
}

function handleVP8Frame(provider, timestamp, payload) {
    const s = state[provider];

    // VP8 keyframe detection: bit 0 of first byte is 0 for keyframe
    const isKeyframe = (payload[0] & 0x01) === 0;

    if (!s.configured) {
        if (!isKeyframe) return;
        try {
            s.videoDecoder.configure({
                codec: "vp8",
                optimizeForLatency: true,
            });
            s.configured = true;
            log(provider, "VP8 video decoder configured.", "log-success");
        } catch (err) {
            log(provider, `Failed to configure VP8 decoder: ${err.message}`, "log-error");
            return;
        }
    }

    try {
        const ts = videoTimestamps[provider];
        videoTimestamps[provider] += 33333;

        const chunk = new EncodedVideoChunk({
            type: isKeyframe ? "key" : "delta",
            timestamp: ts,
            data: payload,
        });
        s.videoDecoder.decode(chunk);
    } catch {
        // skip
    }
}

// ---------------------------------------------------------------------------
// H.264 NAL unit parsing utilities
// ---------------------------------------------------------------------------

/**
 * Find Annex B NAL unit boundaries (00 00 00 01 or 00 00 01 start codes).
 * Returns array of { offset, length, nalType }.
 */
function parseNalUnits(data) {
    const units = [];
    let i = 0;
    while (i < data.length - 3) {
        let startCodeLen = 0;
        if (data[i] === 0 && data[i+1] === 0 && data[i+2] === 0 && data[i+3] === 1) {
            startCodeLen = 4;
        } else if (data[i] === 0 && data[i+1] === 0 && data[i+2] === 1) {
            startCodeLen = 3;
        }
        if (startCodeLen > 0) {
            const nalStart = i + startCodeLen;
            const nalType = data[nalStart] & 0x1f;
            // Find next start code
            let end = data.length;
            for (let j = nalStart + 1; j < data.length - 2; j++) {
                if (data[j] === 0 && data[j+1] === 0 &&
                    (data[j+2] === 1 || (data[j+2] === 0 && j + 3 < data.length && data[j+3] === 1))) {
                    end = j;
                    break;
                }
            }
            units.push({
                offset: nalStart,
                length: end - nalStart,
                nalType: nalType,
                startCodeOffset: i,
                startCodeLen: startCodeLen,
            });
            i = end;
        } else {
            i++;
        }
    }
    return units;
}

function detectH264Keyframe(data) {
    const nals = parseNalUnits(data);
    return nals.some(n => n.nalType === 5 || n.nalType === 7); // IDR or SPS
}

/**
 * Extract AVCC-style description from H.264 Annex B bitstream containing SPS+PPS.
 * Returns an ArrayBuffer for VideoDecoderConfig.description.
 */
function extractAvccDescription(data) {
    const nals = parseNalUnits(data);
    let sps = null;
    let pps = null;

    for (const nal of nals) {
        if (nal.nalType === 7 && !sps) { // SPS
            sps = data.slice(nal.offset, nal.offset + nal.length);
        } else if (nal.nalType === 8 && !pps) { // PPS
            pps = data.slice(nal.offset, nal.offset + nal.length);
        }
    }

    if (!sps || !pps) return null;

    // Build AVCC box
    // AVCDecoderConfigurationRecord structure
    const configLen = 11 + sps.length + pps.length;
    const buf = new ArrayBuffer(configLen);
    const view = new DataView(buf);
    const arr = new Uint8Array(buf);

    view.setUint8(0, 1);               // configurationVersion
    view.setUint8(1, sps[1]);          // AVCProfileIndication
    view.setUint8(2, sps[2]);          // profile_compatibility
    view.setUint8(3, sps[3]);          // AVCLevelIndication
    view.setUint8(4, 0xff);            // lengthSizeMinusOne = 3 (4-byte NALU lengths) | reserved 0xFC
    view.setUint8(5, 0xe1);            // numOfSequenceParameterSets = 1 | reserved 0xE0
    view.setUint16(6, sps.length);     // sequenceParameterSetLength
    arr.set(sps, 8);                   // SPS data
    view.setUint8(8 + sps.length, 1);  // numOfPictureParameterSets
    view.setUint16(9 + sps.length, pps.length); // pictureParameterSetLength
    arr.set(pps, 11 + sps.length);     // PPS data

    return buf;
}

/**
 * Strip SPS (7) and PPS (8) NAL units from Annex B bitstream, returning only VCL NALs.
 * Converts to 4-byte length-prefixed format for WebCodecs.
 */
function stripParameterSets(data) {
    const nals = parseNalUnits(data);
    const vclNals = nals.filter(n => n.nalType !== 7 && n.nalType !== 8);

    if (vclNals.length === 0) return new Uint8Array(0);

    // Calculate total size with 4-byte length prefixes
    let totalLen = 0;
    for (const nal of vclNals) {
        totalLen += 4 + nal.length;
    }

    const result = new Uint8Array(totalLen);
    const view = new DataView(result.buffer);
    let offset = 0;
    for (const nal of vclNals) {
        view.setUint32(offset, nal.length, false);
        result.set(data.slice(nal.offset, nal.offset + nal.length), offset + 4);
        offset += 4 + nal.length;
    }
    return result;
}

// ---------------------------------------------------------------------------
// WebCodecs: Audio Decoder
// ---------------------------------------------------------------------------

function initAudioDecoder(provider) {
    if (!hasWebCodecs) return;

    const s = state[provider];
    s.audioCtx = new AudioContext({ sampleRate: 48000 });

    const decoder = new AudioDecoder({
        output: (audioData) => {
            playAudioData(provider, audioData);
        },
        error: (err) => {
            log(provider, `Audio decoder error: ${err.message}`, "log-error");
        },
    });

    try {
        decoder.configure({
            codec: "opus",
            sampleRate: 48000,
            numberOfChannels: 2,
        });
    } catch (err) {
        log(provider, `Failed to configure OPUS audio decoder: ${err.message}`, "log-error");
    }

    s.audioDecoder = decoder;
}

function handleAudioFrame(provider, durationMs, payload) {
    const s = state[provider];
    if (!s.audioDecoder || s.audioDecoder.state === "closed") return;

    try {
        const chunk = new EncodedAudioChunk({
            type: "key",
            timestamp: Date.now() * 1000,
            data: payload,
        });
        s.audioDecoder.decode(chunk);
    } catch {
        // skip
    }
}

// Next playback time tracker
const nextPlayTime = { did: 0, simli: 0, avatartalk: 0 };

function playAudioData(provider, audioData) {
    const s = state[provider];
    if (!s.audioCtx) return;

    try {
        const numFrames = audioData.numberOfFrames;
        const numChannels = audioData.numberOfChannels;
        const sampleRate = audioData.sampleRate;

        const buffer = s.audioCtx.createBuffer(numChannels, numFrames, sampleRate);

        for (let ch = 0; ch < numChannels; ch++) {
            const channelData = new Float32Array(numFrames);
            audioData.copyTo(channelData, { planeIndex: ch });
            buffer.copyToChannel(channelData, ch);
        }
        audioData.close();

        const source = s.audioCtx.createBufferSource();
        source.buffer = buffer;
        source.connect(s.audioCtx.destination);

        const now = s.audioCtx.currentTime;
        if (nextPlayTime[provider] < now) {
            nextPlayTime[provider] = now;
        }
        source.start(nextPlayTime[provider]);
        nextPlayTime[provider] += buffer.duration;
    } catch {
        try { audioData.close(); } catch { /* ignore */ }
    }
}

// ---------------------------------------------------------------------------
// Send text to provider
// ---------------------------------------------------------------------------

function sendText(provider) {
    const s = state[provider];
    if (!s.ws || s.ws.readyState !== WebSocket.OPEN) {
        log(provider, "Not connected.", "log-error");
        return;
    }

    let text;
    if (provider === "avatartalk") {
        text = document.getElementById("avatartalk-text").value.trim();
        if (!text) return;
        const avatar = document.getElementById("avatartalk-avatar").value;
        const emotion = document.getElementById("avatartalk-emotion").value;
        s.ws.send(JSON.stringify({ action: "send_text", text, avatar, emotion }));
        document.getElementById("avatartalk-text").value = "";
    } else if (provider === "did") {
        text = document.getElementById("did-text").value.trim();
        if (!text) return;
        s.ws.send(JSON.stringify({ action: "send_text", text }));
        document.getElementById("did-text").value = "";
    }

    log(provider, `Sending: "${text}"`, "log-event");
}

// ---------------------------------------------------------------------------
// AvatarTalk: Show generated MP4 video
// ---------------------------------------------------------------------------

function showAvatarTalkVideo(mp4Url) {
    const video = document.getElementById("avatartalk-video");
    const placeholder = document.getElementById("avatartalk-placeholder");
    if (!video) return;

    video.src = mp4Url;
    video.style.display = "block";
    if (placeholder) placeholder.style.display = "none";
    video.play().catch(() => { /* autoplay may be blocked */ });
}

// ---------------------------------------------------------------------------
// Microphone capture for Simli (PCM16 @ 16kHz mono)
// ---------------------------------------------------------------------------

let micProcessor = null;

async function toggleMic(provider) {
    const s = state[provider];
    const micBtn = document.getElementById(`${provider}-mic`);

    if (s.micStream) {
        // Stop microphone
        s.micStream.getTracks().forEach(t => t.stop());
        s.micStream = null;
        if (micProcessor) {
            micProcessor.disconnect();
            micProcessor = null;
        }
        micBtn.classList.remove("recording");
        log(provider, "Microphone stopped.", "log-event");
        return;
    }

    if (!s.ws || s.ws.readyState !== WebSocket.OPEN) {
        log(provider, "Connect first before enabling microphone.", "log-error");
        return;
    }

    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: {
                sampleRate: 16000,
                channelCount: 1,
                echoCancellation: true,
                noiseSuppression: true,
            },
        });
        s.micStream = stream;
        micBtn.classList.add("recording");
        log(provider, "Microphone started. Capturing PCM16 audio.", "log-success");

        // Create AudioContext for processing
        const micCtx = new AudioContext({ sampleRate: 16000 });
        const source = micCtx.createMediaStreamSource(stream);

        // Use ScriptProcessorNode (simpler, widely supported)
        // bufferSize 4096 @ 16kHz = ~256ms chunks
        const processor = micCtx.createScriptProcessor(4096, 1, 1);
        micProcessor = processor;

        processor.onaudioprocess = (e) => {
            if (!s.ws || s.ws.readyState !== WebSocket.OPEN) return;
            const input = e.inputBuffer.getChannelData(0);
            // Convert Float32 [-1, 1] to Int16
            const pcm16 = new Int16Array(input.length);
            for (let i = 0; i < input.length; i++) {
                const sample = Math.max(-1, Math.min(1, input[i]));
                pcm16[i] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
            }
            // Send as binary WebSocket message
            s.ws.send(pcm16.buffer);
        };

        source.connect(processor);
        processor.connect(micCtx.destination); // Required for ScriptProcessor to fire

    } catch (err) {
        log(provider, `Microphone error: ${err.message}`, "log-error");
    }
}
