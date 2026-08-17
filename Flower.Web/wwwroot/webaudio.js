// Backs WebAudioManager (Flower/Manager/WebAudioManager.cs) via [JSImport].
// A single HTMLAudioElement is reused across tracks - the browser's own
// decoder handles whatever format the <audio> element supports (mp3/aac/wav/
// flac/ogg in Chrome and Firefox; format support is narrower in Safari, not
// yet verified there), so unlike the LibVLC-backed desktop/mobile pipeline
// this needs no PCM ring buffer or native decode at all. C# polls the getters
// below on a timer rather than this module calling back into C# - mirrors
// GaplessAudioManager's own 250ms PositionChanged polling timer, and avoids
// needing [JSExport]/module-loading-order complexity for event callbacks.

let audio = null;

function ensureAudio() {
    if (!audio) {
        audio = new Audio();
        audio.preload = "auto";
    }
    return audio;
}

export function setSrc(url) {
    const a = ensureAudio();
    a.src = url;
    a.load();
}

export function play() {
    // play() returns a Promise that rejects if the browser blocks autoplay
    // (no prior user gesture) - swallow it here rather than let an unhandled
    // rejection reach the console; WebAudioManager's poll loop already
    // reflects the resulting paused state back via getPaused().
    ensureAudio().play().catch(() => {});
}

export function pause() {
    ensureAudio().pause();
}

export function stop() {
    const a = ensureAudio();
    a.pause();
    a.currentTime = 0;
}

export function seek(seconds) {
    ensureAudio().currentTime = seconds;
}

export function setVolume(volume) {
    ensureAudio().volume = volume;
}

export function getCurrentTime() {
    return audio ? audio.currentTime : 0;
}

export function getDuration() {
    return audio && isFinite(audio.duration) ? audio.duration : 0;
}

export function getPaused() {
    return audio ? audio.paused : true;
}

export function getEnded() {
    return audio ? audio.ended : false;
}
