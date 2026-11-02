#!/usr/bin/env python3
"""Write the reference files the C# tests compare against.

docs/VIBE-PLAYLIST-PLAN.md, "Testing". Run by hand after an export, with the
phase 0 environment (requirements.txt). Writes:

  Tests/Flower.Tests/SemanticAudio/References/
    tokens.json            HF RobertaTokenizer ids for prompts that are hard
                           to tokenize (what laion_clap feeds the text encoder)
    vocab.subset.json,     the part of RoBERTa's vocabulary and merges those
    merges.subset.txt      prompts use: the merges applied while tokenizing
                           them, in rank order, and the tokens they end in.
                           Byte-pair merging always applies the lowest-ranked
                           pair present, so with exactly those merges it takes
                           the same steps it takes with all 50k, and the test
                           needs 1.4 MB less of the real files.
  Tests/Flower.Tests.Decoding/SemanticAudio/References/
    chord-44k.wav.gz       14 s of two chords at 44.1 kHz stereo: one full grid
                           window, and a 4 s tail that is repeat-padded. A
                           gzipped WAV because the tones repeat exactly, which
                           gzip finds and FLAC does not (22 KB against 360)
    *.prepared.i16.gz      each fixture window as the model takes it, after
                           the int16 round trip - so exactly int16 values,
                           stored as such - before repeat-padding
    embeddings.json        what Python's onnxruntime makes of those windows
                           (audio int8) and of a few prompts (text fp16)

The waveforms come from the ffmpeg command line, as phase 0 decoded every song;
Flower decodes with FFAudio, FFmpeg's own resampler again.
"""

import argparse
import gzip
import json
import subprocess
import tempfile
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[2]
TOKENS_OUT = REPO / "Tests/Flower.Tests/SemanticAudio/References"
DECODING_OUT = REPO / "Tests/Flower.Tests.Decoding/SemanticAudio/References"
FIXTURES = REPO / "Tests/Flower.Tests.Decoding.Scenarios/Fixtures"

SR = 48_000
WINDOW = 10 * SR
MIN_TAIL = 3 * SR
TEMPLATE = "A song that sounds like {}."

TOKENIZER_PROMPTS = [
    "music for a rainy day",
    "Music For A Rainy Day",
    "music to go jogging",
    "calm music for focusing at work",
    "sad piano without singing",
    "lo-fi hip hop beats to study/relax to",
    "80s synth-pop with a driving bassline!!!",
    "AC/DC, Mötley Crüe & Blue Öyster Cult",
    "rock'n'roll that doesn't stop, I'm sure you'll love it",
    "musique pour un jour de pluie",
    "café jazz, naïve and soft",
    "música para correr, ¿por qué no?",
    "日本のシティポップ",
    "Музыка для бега",
    "happy songs 😀🎶",
    "  leading and trailing spaces  ",
    "two  spaces\tand a tab\nand a newline",
    "a",
    "",
    "123 456.78 1,000",
    "\"quoted\" (parenthesised) [bracketed] {braced}",
    "dark ambient — drones, hiss… and rain",
    "Beyoncé, Sigur Rós, Björk",
    "it's, it’s, ' alone '",
    "C# minor nocturne @ 3am #mood",
    " ".join(["very"] * 120) + " long",
    TEMPLATE.format("music for a rainy day"),
    TEMPLATE.format("songs from the 1960s"),
    "This audio is instrumental music.",
    "This audio is a comedy sketch.",
]

EMBEDDING_PROMPTS = [
    "music for a rainy day",
    "music to go jogging",
    "calm music for focusing at work",
    "angry heavy metal",
    "christmas music",
]


def decode(path: Path) -> np.ndarray:
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-nostdin", "-i", str(path), "-vn", "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"],
        check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype=np.float32)


def windows(audio: np.ndarray) -> list:
    full, tail = divmod(audio.size, WINDOW)
    count = max(full + (1 if tail >= MIN_TAIL else 0), 1)
    return [audio[k * WINDOW:(k + 1) * WINDOW] for k in range(count)]


def round_trip(window: np.ndarray) -> np.ndarray:
    return (np.clip(window, -1, 1) * 32767).astype(np.int16)


def repeat_pad(window: np.ndarray) -> np.ndarray:
    if window.size < WINDOW:
        window = np.tile(window, WINDOW // window.size)
        window = np.pad(window, (0, WINDOW - window.size))
    return window


def make_chord(path: Path):
    # Two chords, one per grid window, so the windows sound different and
    # neither is silence: A major-ish on 240/300/360 Hz for the first 10 s,
    # then 200/250/300 Hz. Both repeat every 1/60 s or 1/50 s, which keeps
    # the reference files small. 44.1 kHz stereo, the rate a library is made
    # of, with the channels out of phase a little so the mixdown matters.
    rate, seconds, change = 44_100, 14, 10
    t = np.arange(rate * seconds) / rate
    first = sum(np.sin(2 * np.pi * hz * t) for hz in (240.0, 300.0, 360.0)) / 3
    second = sum(np.sin(2 * np.pi * hz * t + 0.3) for hz in (200.0, 250.0, 300.0)) / 3
    tone = np.where(t < change, first, second)
    left, right = 0.6 * tone, 0.4 * tone
    pcm = (np.stack([left, right], axis=1) * 32767).astype("<i2").tobytes()
    wav = subprocess.run(["ffmpeg", "-v", "error", "-f", "s16le", "-ar", str(rate), "-ac", "2", "-i", "-",
                          "-c:a", "pcm_s16le", "-f", "wav", "-bitexact", "-"],
                         input=pcm, check=True, capture_output=True).stdout
    path.write_bytes(gzip.compress(wav, compresslevel=9, mtime=0))


def write_tokenizer_subset(tokenizer):
    import regex

    ranks = tokenizer.bpe_ranks
    used_merges, used_tokens = set(), {"<s>", "</s>", "<pad>", "<unk>"}
    for text in TOKENIZER_PROMPTS:
        for piece in regex.findall(tokenizer.pat, text):
            symbols = [tokenizer.byte_encoder[b] for b in piece.encode("utf-8")]
            while len(symbols) > 1:
                pairs = {(a, b) for a, b in zip(symbols, symbols[1:]) if (a, b) in ranks}
                if not pairs:
                    break
                best = min(pairs, key=ranks.get)
                used_merges.add(best)
                merged, i = [], 0
                while i < len(symbols):
                    if i < len(symbols) - 1 and (symbols[i], symbols[i + 1]) == best:
                        merged.append(symbols[i] + symbols[i + 1])
                        i += 2
                    else:
                        merged.append(symbols[i])
                        i += 1
                symbols = merged
            used_tokens.update(symbols)
    merges = sorted(used_merges, key=ranks.get)
    (TOKENS_OUT / "merges.subset.txt").write_text("#version: 0.2\n" + "".join(f"{a} {b}\n" for a, b in merges))
    vocab = {t: tokenizer.encoder[t] for t in sorted(used_tokens, key=tokenizer.encoder.get)}
    (TOKENS_OUT / "vocab.subset.json").write_text(json.dumps(vocab, ensure_ascii=False))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--export", type=Path, default=Path("~/.cache/flower-semantic-audio/export").expanduser())
    args = ap.parse_args()

    from transformers import RobertaTokenizer
    import onnxruntime as ort

    TOKENS_OUT.mkdir(parents=True, exist_ok=True)
    DECODING_OUT.mkdir(parents=True, exist_ok=True)

    tokenizer = RobertaTokenizer(str(args.export / "vocab.json"), str(args.export / "merges.txt"))

    def tokenize(texts):
        return tokenizer(texts, padding="max_length", truncation=True, max_length=77, return_tensors="np")

    tokens = [{"text": t, "ids": tokenize([t])["input_ids"][0].tolist()} for t in TOKENIZER_PROMPTS]
    (TOKENS_OUT / "tokens.json").write_text(json.dumps(tokens, ensure_ascii=False, indent=1))
    write_tokenizer_subset(tokenizer)

    chord_gz = DECODING_OUT / "chord-44k.wav.gz"
    make_chord(chord_gz)
    chord = Path(tempfile.mkdtemp()) / "chord-44k.wav"
    chord.write_bytes(gzip.decompress(chord_gz.read_bytes()))

    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    audio_session = ort.InferenceSession(str(args.export / "music_audioset_epoch_15_esc_90.14-audio-int8.onnx"), options)
    text_session = ort.InferenceSession(str(args.export / "music_audioset_epoch_15_esc_90.14-text-fp16.onnx"), options)

    clips = []
    for fixture in (FIXTURES / "sine-44k.flac", chord):
        for k, window in enumerate(windows(decode(fixture))):
            ints = round_trip(window)
            name = f"{fixture.stem}.window{k}.prepared.i16.gz"
            (DECODING_OUT / name).write_bytes(gzip.compress(ints.astype("<i2").tobytes(), compresslevel=9, mtime=0))
            waveform = repeat_pad(ints.astype(np.float32) / 32767)[None, :].astype(np.float32)
            embedding = audio_session.run(None, {"waveform": waveform})[0][0]
            clips.append({"fixture": fixture.name, "window": k, "prepared": name,
                          "embedding": (embedding / np.linalg.norm(embedding)).tolist()})

    encoded = tokenize([TEMPLATE.format(p) for p in EMBEDDING_PROMPTS])
    text = text_session.run(None, {"input_ids": encoded["input_ids"].astype(np.int64),
                                   "attention_mask": encoded["attention_mask"].astype(np.int64)})[0]
    text /= np.linalg.norm(text, axis=1, keepdims=True)
    prompts = [{"prompt": p, "embedding": e.tolist()} for p, e in zip(EMBEDDING_PROMPTS, text)]

    (DECODING_OUT / "embeddings.json").write_text(json.dumps(
        {"template": TEMPLATE, "audio": "music_audioset_epoch_15_esc_90.14-audio-int8.onnx",
         "text": "music_audioset_epoch_15_esc_90.14-text-fp16.onnx", "clips": clips, "prompts": prompts}))
    for p in sorted(TOKENS_OUT.iterdir()) + sorted(DECODING_OUT.iterdir()):
        print(f"{p.stat().st_size / 1e3:8.1f} KB  {p.relative_to(REPO)}")


if __name__ == "__main__":
    main()
