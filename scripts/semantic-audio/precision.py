#!/usr/bin/env python3
"""Phase 0: does int8 (or fp16) keep the playlists fp32 makes?

Runs the exported ONNX encoders (export-clap-onnx.py) over the sample's clips
under one clip setting, ranks the sample against prompts.txt, and compares
each precision's top results with fp32's - the PyTorch embeddings from
prototype.py embed, which the fp32 graph reproduces exactly. Also times one
clip and one prompt on one thread, the way Flower's analyzer runs.

Text and audio precision are judged separately as well as together, since
they need not be the same setting.
"""

import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).parent))
import prototype as P  # noqa: E402

PRECISIONS = ["fp32", "fp16", "int8-reduce-range"]


def session(path: Path, threads: int):
    o = ort.SessionOptions()
    o.intra_op_num_threads = threads
    return ort.InferenceSession(str(path), o, providers=["CPUExecutionProvider"])


def normalise(x):
    return x / np.linalg.norm(x, axis=-1, keepdims=True)


def timed(run, runs=10):
    run()
    t = time.time()
    for _ in range(runs):
        run()
    return round((time.time() - t) / runs, 4)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--work", type=Path, default=Path("~/.cache/flower-semantic-audio").expanduser())
    ap.add_argument("--prompts", default=str(Path(__file__).with_name("prompts.txt")))
    ap.add_argument("--clips", default="6", choices=list(P.CLIP_SETTINGS))
    ap.add_argument("--template", default="this-audio", choices=list(P.TEMPLATES))
    ap.add_argument("--alpha", type=float, default=0.5)
    args = ap.parse_args()
    export = args.work / "export"

    torch, module = P.load_model("cpu")
    store = np.load(args.work / "embeddings.npz", allow_pickle=True)["store"].item()
    usable = [p for p, e in store.items() if "windows" in e]
    prompts = [l.strip() for l in Path(args.prompts).read_text().splitlines() if l.strip() and not l.startswith("#")]
    n = P.CLIP_SETTINGS[args.clips]

    tokens = module.tokenizer([P.TEMPLATES[args.template].format(p) for p in prompts])
    feeds = {"input_ids": tokens["input_ids"].numpy(), "attention_mask": tokens["attention_mask"].numpy()}
    one_prompt = {k: v[:1] for k, v in feeds.items()}
    report = {"setting": vars(args) | {"work": str(args.work), "export": str(export)},
              "audio_cosine": {}, "text_cosine": {}, "seconds_per_clip_1_thread": {},
              "seconds_per_prompt_1_thread": {}, "overlap": {}}
    report["setting"].pop("work"); report["setting"].pop("export")

    texts = {}
    for precision in PRECISIONS:
        s = session(export / f"music_audioset_epoch_15_esc_90.14-text-{precision}.onnx", 1)
        texts[precision] = normalise(s.run(None, feeds)[0])
        report["seconds_per_prompt_1_thread"][precision] = timed(lambda: s.run(None, one_prompt))

    # The clips the setting selects, decoded once.
    print("decoding the selected clips", flush=True)
    waveforms, picks = [], []
    for path in usable:
        entry = store[path]
        picked = P.selected_windows(len(entry["windows"]), entry["duration"], n)
        audio = P.decode(path)
        waveforms += [P.prepare(audio[k * P.WINDOW:(k + 1) * P.WINDOW]) for k in picked]
        picks.append(picked)
    print(f"{len(waveforms)} clips", flush=True)

    reference_audio = np.concatenate([store[p]["windows"][k].astype(np.float32) for p, k in zip(usable, picks)])
    audio = {"fp32": None}
    for precision in PRECISIONS:
        path = export / f"music_audioset_epoch_15_esc_90.14-audio-{precision}.onnx"
        s1 = session(path, 1)
        report["seconds_per_clip_1_thread"][precision] = timed(lambda: s1.run(None, {"waveform": waveforms[0][None]}))
        s = session(path, 0)
        out = normalise(np.concatenate([s.run(None, {"waveform": w[None]})[0] for w in waveforms]))
        audio[precision] = out
        cos = (out * reference_audio).sum(1)
        report["audio_cosine"][precision] = {"min": round(float(cos.min()), 5), "mean": round(float(cos.mean()), 5)}
        print(precision, "audio done", report["audio_cosine"][precision], flush=True)
    for precision in PRECISIONS:
        cos = (texts[precision] * texts["fp32"]).sum(1)
        report["text_cosine"][precision] = {"min": round(float(cos.min()), 5), "mean": round(float(cos.mean()), 5)}

    def setting_from(clips):
        """A prototype.Setting over these clip embeddings instead of the store's."""
        fake, offset = {}, 0
        for path, picked in zip(usable, picks):
            windows = np.zeros((len(store[path]["windows"]), clips.shape[1]), dtype=np.float32)
            windows[picked] = clips[offset:offset + len(picked)]
            offset += len(picked)
            fake[path] = {"windows": windows, "duration": store[path]["duration"]}
        return P.Setting(fake, usable, n)

    reference, _ = setting_from(reference_audio).score(texts["fp32"], args.alpha)
    reference_top = np.argsort(-reference, axis=0)
    for audio_precision in PRECISIONS:
        setting = setting_from(audio[audio_precision])
        for text_precision in PRECISIONS:
            scores, _ = setting.score(texts[text_precision], args.alpha)
            top = np.argsort(-scores, axis=0)
            result = {}
            for k in (10, 25, 50):
                per_prompt = [len(set(top[:k, j]) & set(reference_top[:k, j])) / k for j in range(len(prompts))]
                result[f"top{k}"] = round(float(np.mean(per_prompt)), 3)
                if k == 10:
                    result["top10_worst"] = round(float(np.min(per_prompt)), 3)
            report["overlap"][f"audio {audio_precision} / text {text_precision}"] = result

    report["sizes_mb"] = {p.name: round(p.stat().st_size / 1e6, 1) for p in sorted(export.glob("*.onnx"))}
    (args.work / "precision.json").write_text(json.dumps(report, indent=1))
    print(json.dumps(report, indent=1))


if __name__ == "__main__":
    main()
