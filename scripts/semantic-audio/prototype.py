#!/usr/bin/env python3
"""Phase 0 of docs/VIBE-PLAYLIST-PLAN.md: is LAION's CLAP music checkpoint good enough?

Three steps, each resumable, everything written under --work
(default ~/.cache/flower-semantic-audio):

  sample  pick N songs from a music folder (seeded), with their tags
  embed   decode each song and embed EVERY 10 s window of its grid, so that
          every clip setting (3, 6, 12, whole song) can be judged from one pass
  report  rank the sample against prompts.txt under every combination of
          prompt template x clip setting x alpha, and write report.html, which
          plays each match from its best-matching window

The grid and the clip selection here are the plan's ("Clips"), and are what
the C# engine must reproduce.

The model is LAION's own checkpoint through their laion_clap package, not
Hugging Face's laion/larger_clap_music, whose conversion is broken (every
prompt ranks the library the same - see export-clap-onnx.py).
"""

import argparse
import html
import json
import os
import random
import subprocess
import sys
import time
from pathlib import Path

import numpy as np

SR = 48_000
WINDOW = 10 * SR
MIN_TAIL = 3 * SR
CHECKPOINT = Path("~/.cache/flower-semantic-audio/ckpt/music_audioset_epoch_15_esc_90.14.pt").expanduser()
EXTENSIONS = {".mp3", ".m4a", ".flac", ".wav", ".ogg", ".opus", ".aiff", ".aif", ".ape", ".alac"}

CLIP_SETTINGS = {"3": 3, "6": 6, "12": 12, "whole": None}
TEMPLATES = {
    "bare": "{}",
    "this-audio": "This audio is {}.",
    "sounds-like": "A song that sounds like {}.",
}
ALPHAS = [0.0, 0.5, 1.0]


# ---- the grid ---------------------------------------------------------------

def grid_window_count(n_samples: int) -> int:
    """Windows on a song's 10 s grid: full windows, plus a tail of 3-10 s."""
    full, tail = divmod(n_samples, WINDOW)
    count = full + (1 if tail >= MIN_TAIL else 0)
    return max(count, 1)  # a song shorter than 3 s still gets window 0


def selected_windows(window_count: int, duration_s: float, n):
    """The windows a setting selects. n=None is the whole song."""
    if n is None or window_count <= 1:
        return list(range(window_count))
    picked = []
    for i in range(n):
        centre = (0.05 + 0.9 * (i + 0.5) / n) * duration_s
        k = min(max(int(centre // 10), 0), window_count - 1)
        if k not in picked:
            picked.append(k)
    return picked


# ---- sample -----------------------------------------------------------------

def read_tags(path: Path) -> dict:
    import mutagen

    tags = {"title": path.stem, "artist": "", "album": "", "genre": "", "duration": 0.0}
    try:
        f = mutagen.File(path, easy=True)
    except Exception:
        return tags
    if f is None:
        return tags
    if f.info is not None:
        tags["duration"] = float(getattr(f.info, "length", 0.0) or 0.0)
    for key in ("title", "artist", "album", "genre"):
        value = (f.tags or {}).get(key) if f.tags is not None else None
        if value:
            tags[key] = value[0]
    return tags


def cmd_sample(args):
    root = Path(args.root).expanduser()
    files = sorted(p for p in root.rglob("*") if p.suffix.lower() in EXTENSIONS and p.is_file())
    print(f"{len(files)} audio files under {root}")
    rng = random.Random(args.seed)
    chosen = rng.sample(files, min(args.n, len(files)))
    songs = []
    for p in chosen:
        t = read_tags(p)
        t["path"] = str(p)
        songs.append(t)
    out = args.work / "sample.json"
    out.write_text(json.dumps(songs, indent=1, ensure_ascii=False))
    print(f"wrote {len(songs)} songs to {out}")


# ---- embed ------------------------------------------------------------------

def decode(path: str) -> np.ndarray:
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-nostdin", "-i", path, "-vn", "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"],
        check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype=np.float32)


def load_model(device: str):
    import torch
    import laion_clap

    module = laion_clap.CLAP_Module(enable_fusion=False, amodel="HTSAT-base", device=device)
    module.load_ckpt(str(CHECKPOINT), verbose=False)
    module.eval()
    return torch, module


def prepare(window: np.ndarray) -> np.ndarray:
    """One grid window as the model takes it: laion_clap's int16 round trip,
    and repeatpad to 10 s (tile whole copies, then zeros) for a short one."""
    w = (np.clip(window, -1, 1) * 32767).astype(np.int16).astype(np.float32) / 32767
    if w.size < WINDOW:
        w = np.tile(w, WINDOW // w.size)
        w = np.pad(w, (0, WINDOW - w.size))
    return w


def embed_audio(torch, module, windows) -> np.ndarray:
    x = torch.from_numpy(np.stack([prepare(w) for w in windows]))
    with torch.no_grad():
        e = module.get_audio_embedding_from_data(x=x, use_tensor=True)
    return e.float().cpu().numpy()


def embed_text(torch, module, texts) -> np.ndarray:
    with torch.no_grad():
        e = module.get_text_embedding(list(texts), use_tensor=True)
    return e.float().cpu().numpy()


def cmd_embed(args):
    torch, module = load_model(args.device)
    songs = json.loads((args.work / "sample.json").read_text())
    store_path = args.work / args.store
    done = {}
    if store_path.exists():
        z = np.load(store_path, allow_pickle=True)
        done = z["store"].item()
    print(f"{len(done)} of {len(songs)} songs already embedded")

    started = time.time()
    clips = 0
    for index, song in enumerate(songs):
        path = song["path"]
        if path in done:
            continue
        try:
            audio = decode(path)
        except subprocess.CalledProcessError as e:
            done[path] = {"failure": e.stderr.decode(errors="replace")[-300:]}
            continue
        if audio.size < SR // 10:
            done[path] = {"failure": "too short"}
            continue
        count = grid_window_count(audio.size)
        windows = [audio[k * WINDOW:(k + 1) * WINDOW] for k in range(count)]
        embeddings = []
        for b in range(0, count, args.batch):
            embeddings.append(embed_audio(torch, module, windows[b:b + args.batch]))
        e = np.concatenate(embeddings)
        e /= np.linalg.norm(e, axis=1, keepdims=True)
        done[path] = {"windows": e.astype(np.float16), "duration": audio.size / SR}
        clips += count
        if (index + 1) % 25 == 0:
            np.savez(store_path, store=np.array(done, dtype=object))
            rate = clips / (time.time() - started)
            print(f"{index + 1}/{len(songs)} songs, {clips} clips, {rate:.1f} clips/s", flush=True)
    np.savez(store_path, store=np.array(done, dtype=object))
    print(f"done: {len(done)} songs in {store_path}")


# ---- report -----------------------------------------------------------------

class Setting:
    """Every song's clips under one clip setting, stacked so that one matrix
    product scores every song against every prompt at once."""

    def __init__(self, store, paths, n):
        clips, owner, means = [], [], []
        self.windows = []
        for i, p in enumerate(paths):
            entry = store[p]
            picked = selected_windows(len(entry["windows"]), entry["duration"], n)
            chosen = entry["windows"][picked].astype(np.float32)
            clips.append(chosen)
            owner += [i] * len(picked)
            self.windows += picked
            mean = chosen.mean(axis=0)
            means.append(mean / np.linalg.norm(mean))
        self.clips = np.concatenate(clips)
        self.owner = np.array(owner)
        self.means = np.stack(means)
        self.songs = len(paths)

    def score(self, texts: np.ndarray, alpha: float):
        """(songs x prompts) scores, and each song's best window per prompt."""
        per_clip = self.clips @ texts.T
        best = np.full((self.songs, len(texts), 2), -np.inf, dtype=np.float32)
        best_window = np.zeros((self.songs, len(texts)), dtype=np.int64)
        order = np.argsort(self.owner, kind="stable")
        for row in order:
            song = self.owner[row]
            values = per_clip[row]
            better = values > best[song, :, 0]
            best_window[song, better] = self.windows[row]
            best[song, :, 1] = np.where(better, best[song, :, 0], np.maximum(best[song, :, 1], values))
            best[song, :, 0] = np.where(better, values, best[song, :, 0])
        second = np.where(np.isinf(best[:, :, 1]), best[:, :, 0], best[:, :, 1])
        top_two = (best[:, :, 0] + second) / 2
        return alpha * (self.means @ texts.T) + (1 - alpha) * top_two, best_window


def cmd_report(args):
    torch, module = load_model("cpu")
    songs = {s["path"]: s for s in json.loads((args.work / "sample.json").read_text())}
    store = np.load(args.work / args.store, allow_pickle=True)["store"].item()
    usable = [p for p, e in store.items() if "windows" in e]
    failures = {p: e["failure"] for p, e in store.items() if "failure" in e}
    prompts = [l.strip() for l in Path(args.prompts).read_text().splitlines() if l.strip() and not l.startswith("#")]

    texts = {}
    for tname, template in TEMPLATES.items():
        e = embed_text(torch, module, [template.format(p) for p in prompts])
        texts[tname] = e / np.linalg.norm(e, axis=1, keepdims=True)

    rankings = {}
    for cname, n in CLIP_SETTINGS.items():
        setting = Setting(store, usable, n)
        for tname, matrix in texts.items():
            for alpha in ALPHAS:
                scores, windows = setting.score(matrix, alpha)
                for j, prompt in enumerate(prompts):
                    top = np.argsort(-scores[:, j])[:args.top]
                    rankings[f"{tname}|{prompt}|{cname}|{alpha}"] = [
                        [int(i), round(float(scores[i, j]), 4), int(windows[i, j])] for i in top]

    # How much each knob moves the top of the ranking: mean top-N overlap
    # against a reference configuration.
    def overlap(a, b):
        return len({x[0] for x in a} & {x[0] for x in b}) / max(len(a), 1)

    reference = ("this-audio", "6", 0.5)
    sensitivity = {}
    for label, change in [
        ("template: bare", ("bare", "6", 0.5)),
        ("template: sounds-like", ("sounds-like", "6", 0.5)),
        ("clips: 3", ("this-audio", "3", 0.5)),
        ("clips: 12", ("this-audio", "12", 0.5)),
        ("clips: whole song", ("this-audio", "whole", 0.5)),
        ("alpha: 0 (best clips only)", ("this-audio", "6", 0.0)),
        ("alpha: 1 (mean only)", ("this-audio", "6", 1.0)),
    ]:
        values = [overlap(rankings[f"{reference[0]}|{p}|{reference[1]}|{reference[2]}"],
                          rankings[f"{change[0]}|{p}|{change[1]}|{change[2]}"]) for p in prompts]
        sensitivity[label] = round(float(np.mean(values)), 3)

    clip_counts = [len(store[p]["windows"]) for p in usable]
    data = {
        "songs": [{"path": p, "title": songs[p]["title"], "artist": songs[p]["artist"],
                   "album": songs[p]["album"], "genre": songs[p]["genre"]} for p in usable],
        "prompts": prompts,
        "templates": list(TEMPLATES),
        "clips": list(CLIP_SETTINGS),
        "alphas": ALPHAS,
        "rankings": rankings,
        "sensitivity": sensitivity,
        "reference": list(reference),
        "stats": {"songs": len(usable), "failures": len(failures),
                  "windows": int(sum(clip_counts)), "mean_windows": round(float(np.mean(clip_counts)), 1)},
    }
    (args.work / "rankings.json").write_text(json.dumps(data, ensure_ascii=False))
    out = args.work / "report.html"
    out.write_text(REPORT.replace("__DATA__", json.dumps(data, ensure_ascii=False).replace("</", "<\\/")))
    print(f"wrote {out}")
    print("top-%d overlap with %s:" % (args.top, reference), json.dumps(sensitivity, indent=1))


REPORT = r"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Vibe Prototype</title>
<style>
:root{--bg:#fff;--fg:#1d1d1f;--muted:#6e6e73;--line:#e5e5ea;--accent:#c2185b;--row:#f7f7f9}
@media (prefers-color-scheme:dark){:root{--bg:#1c1c1e;--fg:#f2f2f7;--muted:#98989f;--line:#38383a;--accent:#ff6fa3;--row:#2c2c2e}}
body{background:var(--bg);color:var(--fg);font:14px/1.45 -apple-system,system-ui,sans-serif;margin:0;padding:16px;max-width:1100px;margin:auto}
h1{font-size:20px;margin:0 0 4px} .muted{color:var(--muted)}
.controls{display:flex;flex-wrap:wrap;gap:8px;margin:12px 0;position:sticky;top:0;background:var(--bg);padding:8px 0;border-bottom:1px solid var(--line)}
select{font:inherit;padding:4px 6px;background:var(--row);color:var(--fg);border:1px solid var(--line);border-radius:6px}
table{border-collapse:collapse;width:100%} td,th{padding:5px 8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:middle}
th{font-weight:600;color:var(--muted);font-size:12px} tr.playing td{background:var(--row)}
button{font:inherit;border:1px solid var(--line);background:var(--row);color:var(--fg);border-radius:6px;padding:2px 10px;cursor:pointer}
.verdict button.on{background:var(--accent);color:#fff;border-color:var(--accent)}
.sens td:last-child{font-variant-numeric:tabular-nums}
.title{font-weight:500} .score{font-variant-numeric:tabular-nums;color:var(--muted)}
</style></head><body>
<h1>Vibe prototype: music_audioset_epoch_15_esc_90.14 (LAION original)</h1>
<div class="muted" id="stats"></div>
<details><summary>How much each setting changes the top results</summary>
<p class="muted">Average share of the top results that stay the same when one setting changes from the reference (<span id="ref"></span>). 1.0 means the setting makes no difference.</p>
<table class="sens" id="sens"></table></details>
<div class="controls">
<select id="prompt"></select><select id="template"></select><select id="clips"></select><select id="alpha"></select>
<span class="muted" id="tally"></span>
</div>
<table><thead><tr><th>#</th><th></th><th>Song</th><th>Genre tag</th><th>Score</th><th>Fits?</th></tr></thead><tbody id="rows"></tbody></table>
<audio id="player"></audio>
<script>
const D=__DATA__;
const $=id=>document.getElementById(id);
const fill=(el,vals,label)=>{for(const v of vals){const o=document.createElement('option');o.value=v;o.textContent=label(v);el.appendChild(o)}};
fill($('prompt'),D.prompts,v=>v);fill($('template'),D.templates,v=>'template: '+v);
fill($('clips'),D.clips,v=>'clips: '+v);fill($('alpha'),D.alphas,v=>'alpha: '+v);
$('template').value=D.reference[0];$('clips').value=D.reference[1];$('alpha').value=String(D.reference[2]);
$('stats').textContent=`${D.stats.songs} songs, ${D.stats.windows} ten-second windows (${D.stats.mean_windows} a song), ${D.stats.failures} could not be decoded.`;
$('ref').textContent=`template ${D.reference[0]}, ${D.reference[1]} clips, alpha ${D.reference[2]}`;
for(const [k,v] of Object.entries(D.sensitivity)){const r=$('sens').insertRow();r.insertCell().textContent=k;r.insertCell().textContent=v.toFixed(2)}
let verdicts={};try{verdicts=JSON.parse(localStorage.getItem('vibe-verdicts')||'{}')}catch(e){}
const save=()=>{try{localStorage.setItem('vibe-verdicts',JSON.stringify(verdicts))}catch(e){}};
const player=$('player');let playingRow=null;
function render(){
 const key=[$('template').value,$('prompt').value,$('clips').value,$('alpha').value].join('|');
 const rows=$('rows');rows.innerHTML='';let yes=0,no=0;
 D.rankings[key].forEach(([i,score,win],n)=>{
  const s=D.songs[i];const r=rows.insertRow();
  r.insertCell().textContent=n+1;
  const b=document.createElement('button');b.textContent='▶';b.title=`play from ${win*10}s`;
  b.onclick=()=>{if(playingRow===r&&!player.paused){player.pause();return}
   player.src='file://'+s.path.split('/').map(encodeURIComponent).join('/');
   player.currentTime=win*10;player.play();if(playingRow)playingRow.classList.remove('playing');playingRow=r;r.classList.add('playing')};
  r.insertCell().appendChild(b);
  const c=r.insertCell();c.innerHTML=`<div class="title"></div><div class="muted"></div>`;
  c.firstChild.textContent=s.title;c.lastChild.textContent=[s.artist,s.album].filter(Boolean).join(' — ');
  r.insertCell().textContent=s.genre||'';
  const sc=r.insertCell();sc.className='score';sc.textContent=score.toFixed(3);
  const vk=$('prompt').value+'|'+s.path;const v=verdicts[vk];if(v==='y')yes++;if(v==='n')no++;
  const vc=r.insertCell();vc.className='verdict';
  for(const [lab,val] of [['yes','y'],['no','n']]){const vb=document.createElement('button');vb.textContent=lab;
   if(v===val)vb.classList.add('on');vb.onclick=()=>{verdicts[vk]=verdicts[vk]===val?undefined:val;save();render()};vc.appendChild(vb)}
 });
 $('tally').textContent=`your verdicts on this list: ${yes} fit, ${no} don't`;
}
for(const id of ['prompt','template','clips','alpha'])$(id).onchange=render;
render();
</script></body></html>
"""


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", type=Path, default=Path("~/.cache/flower-semantic-audio").expanduser())
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("sample")
    s.add_argument("root")
    s.add_argument("--n", type=int, default=1000)
    s.add_argument("--seed", type=int, default=1)
    e = sub.add_parser("embed")
    e.add_argument("--device", default="mps")
    e.add_argument("--batch", type=int, default=16)
    e.add_argument("--store", default="embeddings.npz")
    r = sub.add_parser("report")
    r.add_argument("--prompts", default=str(Path(__file__).with_name("prompts.txt")))
    r.add_argument("--top", type=int, default=25)
    r.add_argument("--store", default="embeddings.npz")
    args = ap.parse_args()
    args.work.mkdir(parents=True, exist_ok=True)
    {"sample": cmd_sample, "embed": cmd_embed, "report": cmd_report}[args.cmd](args)


if __name__ == "__main__":
    main()
