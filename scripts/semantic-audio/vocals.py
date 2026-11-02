#!/usr/bin/env python3
"""Phase 0: can CLAP tell a song with lyrics from an instrumental?

A song's vocal score is its most vocal clip's similarity to "a song with a
singer" minus its similarity to "instrumental music" (three phrasings of
each, averaged), under the 6-clip setting. The most vocal clip decides
because one sung verse is enough for a song to have lyrics.

  pick       chooses songs for you to label, mostly from the uncertain middle,
             into vocal-label-set.json; try.py serves them at /label
  evaluate   takes the labels (exported from /label) and finds the cut-off
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import prototype as P  # noqa: E402

INSTRUMENTAL = ["This audio is instrumental music.", "This audio is music without vocals.",
                "This audio is an instrumental track with no singing."]
VOCAL = ["This audio is a song with a singer.", "This audio is music with vocals.",
         "This audio is someone singing lyrics."]
SPEECH = ["This audio is people talking.", "This audio is a comedy sketch.",
          "This audio is a man speaking.", "This audio is spoken word with laughter."]
MUSIC = ["This audio is music.", "This audio is a song.",
         "This audio is instrumental music.", "This audio is a song with singing."]


def margins(work: Path, clips: int = 6):
    """(paths, vocal margin, the window of the most vocal clip, speech margin)."""
    store = np.load(work / "embeddings.npz", allow_pickle=True)["store"].item()
    paths = [p for p, e in store.items() if "windows" in e]
    torch, module = P.load_model("cpu")

    def mean_vector(texts):
        e = P.embed_text(torch, module, texts)
        e /= np.linalg.norm(e, axis=1, keepdims=True)
        return e.mean(0)

    vocal, instrumental = mean_vector(VOCAL), mean_vector(INSTRUMENTAL)
    speech, music = mean_vector(SPEECH), mean_vector(MUSIC)
    out_vocal, out_window, out_speech = [], [], []
    for p in paths:
        e = store[p]
        picked = P.selected_windows(len(e["windows"]), e["duration"], clips)
        x = e["windows"][picked].astype(np.float32)
        x /= np.linalg.norm(x, axis=1, keepdims=True)
        m = x @ vocal - x @ instrumental
        out_vocal.append(float(m.max()))
        out_window.append(int(picked[int(m.argmax())]))
        mean = x.mean(0)
        mean /= np.linalg.norm(mean)
        out_speech.append(float(mean @ speech - mean @ music))
    return paths, np.array(out_vocal), np.array(out_window), np.array(out_speech)


def pick(args):
    tags = {s["path"]: s for s in json.loads((args.work / "sample.json").read_text())}
    paths, vocal, window, speech = margins(args.work)
    rng = np.random.default_rng(args.seed)
    # Sketches are another filter's problem (speech), so they are left out here.
    eligible = [i for i, p in enumerate(paths)
                if speech[i] <= 0 and (tags[p]["genre"] or "").lower() not in ("comedy", "humour", "humor")]
    bands = [(-1, -0.2, 8), (-0.2, -0.1, 14), (-0.1, 0.0, 22), (0.0, 0.05, 10), (0.05, 1, 6)]
    chosen = []
    for low, high, n in bands:
        inside = [i for i in eligible if low <= vocal[i] < high]
        chosen += list(rng.choice(inside, size=min(n, len(inside)), replace=False))
    rng.shuffle(chosen)  # so the order gives away nothing about the score
    songs = [{"path": paths[i], "title": tags[paths[i]]["title"], "artist": tags[paths[i]]["artist"],
              "album": tags[paths[i]]["album"], "start": int(window[i]) * 10} for i in chosen]
    (args.work / "vocal-label-set.json").write_text(json.dumps(songs, ensure_ascii=False))
    print(f"{len(songs)} songs to label in {args.work / 'vocal-label-set.json'}")


def evaluate(args):
    labels = json.loads(Path(args.labels).read_text())  # {path: "vocal" | "instrumental"}
    paths, vocal, _, _ = margins(args.work)
    index = {p: i for i, p in enumerate(paths)}
    rows = [(vocal[index[p]], v == "vocal") for p, v in labels.items() if p in index and v in ("vocal", "instrumental")]
    rows.sort()
    print(f"{len(rows)} labelled: {sum(v for _, v in rows)} with vocals, {sum(not v for _, v in rows)} instrumental")
    for m, v in rows:
        print(f"  {m:+.3f} {'vocals' if v else 'instrumental'}")
    print("\ncut-off: songs below it count as instrumental")
    print("  cut-off  instrumentals kept  songs with vocals let through")
    instrumentals = sum(not v for _, v in rows)
    for cut in np.arange(-0.25, 0.051, 0.025):
        kept = sum(1 for m, v in rows if m < cut and not v)
        leaked = sum(1 for m, v in rows if m < cut and v)
        print(f"  {cut:+.3f}   {kept:3}/{instrumentals}               {leaked}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", type=Path, default=Path("~/.cache/flower-semantic-audio").expanduser())
    sub = ap.add_subparsers(dest="command", required=True)
    p = sub.add_parser("pick")
    p.add_argument("--seed", type=int, default=1)
    e = sub.add_parser("evaluate")
    e.add_argument("labels")
    args = ap.parse_args()
    {"pick": pick, "evaluate": evaluate}[args.command](args)


if __name__ == "__main__":
    main()
