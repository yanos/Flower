#!/usr/bin/env python3
"""How long one 10 s clip takes on this machine's CPU - run it on the server.

Needs only numpy and onnxruntime, and the exported audio encoder(s) in the
folder given (export-clap-onnx.py). The content of the clip does not change
the time, so the input is random. Prints seconds per clip on 1 thread (how
Flower's analyzer runs by default) and on every core, and what that makes a
library of the given size cost at each clip setting.

  python3 bench.py /path/to/export --songs 16131 --minutes 4.3
"""

import argparse
import os
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort


def model_file(encoder: str, precision: str) -> str:
    """export-clap-onnx.py's name for one encoder at one precision: the int8
    files are the reduce_range export's."""
    name = "music_audioset_epoch_15_esc_90.14" + ("_reduce_range" if precision == "int8" else "")
    return f"{name}-{encoder}-{precision}.onnx"


def per_clip(path: Path, threads: int, runs: int) -> float:
    o = ort.SessionOptions()
    o.intra_op_num_threads = threads
    s = ort.InferenceSession(str(path), o, providers=["CPUExecutionProvider"])
    # 10 s of 48 kHz mono, which is what the graph takes: the spectrogram is
    # computed inside it.
    x = (np.random.default_rng(0).standard_normal((1, 480000)) * 0.1).astype(np.float32)
    s.run(None, {"waveform": x})
    t = time.time()
    for _ in range(runs):
        s.run(None, {"waveform": x})
    return (time.time() - t) / runs


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("export", type=Path)
    ap.add_argument("--songs", type=int, default=16131)
    ap.add_argument("--minutes", type=float, default=4.3, help="average song length")
    ap.add_argument("--runs", type=int, default=10)
    args = ap.parse_args()

    windows = max(1, round(args.minutes * 6))
    print(f"{os.cpu_count()} cores, onnxruntime {ort.__version__}")
    for precision in ("int8", "fp16", "fp32"):
        path = args.export / model_file("audio", precision)
        if not path.exists():
            continue
        one = per_clip(path, 1, args.runs)
        every = per_clip(path, 0, args.runs)
        print(f"\n{precision}: {one:.3f} s/clip on 1 thread, {every:.3f} s/clip on all cores")
        for label, clips in (("3", 3), ("6", 6), ("12", 12), ("whole song", windows)):
            hours = args.songs * clips * one / 3600
            print(f"  {label:>10} clips: {hours:6.1f} h on 1 thread, {args.songs * clips * every / 3600:6.1f} h on all cores")


if __name__ == "__main__":
    main()
