#!/usr/bin/env python3
"""Type a sentence, hear what the sample would play for it.

Phase 0's hands-on half (docs/VIBE-PLAYLIST-PLAN.md): a page on
http://127.0.0.1:8765 that ranks the songs prototype.py embed has embedded
so far against whatever you type, with each match playing from its
best-matching 10 seconds. It rereads the embeddings when the file changes,
so it can be used while embed is still running.

Listens on 127.0.0.1 only, and serves no file that is not in the sample.
"""

import argparse
import json
import mimetypes
import os
import re
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import prototype as P  # noqa: E402


class Index:
    def __init__(self, work: Path, store_name: str):
        self.store_path = work / store_name
        self.tags = {s["path"]: s for s in json.loads((work / "sample.json").read_text())}
        self.lock = threading.Lock()
        self.mtime = None
        self.reload()

    def reload(self):
        mtime = self.store_path.stat().st_mtime
        if mtime == self.mtime:
            return
        try:
            store = np.load(self.store_path, allow_pickle=True)["store"].item()
        except Exception:
            return  # caught mid-write by embed; the next search tries again
        paths = [p for p, e in store.items() if "windows" in e]
        settings = {name: P.Setting(store, paths, n) for name, n in P.CLIP_SETTINGS.items()}
        with self.lock:
            self.paths, self.settings, self.mtime = paths, settings, mtime
        print(f"{len(paths)} songs embedded", flush=True)


def page() -> str:
    return PAGE


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", type=Path, default=Path("~/.cache/flower-semantic-audio").expanduser())
    ap.add_argument("--store", default="embeddings.npz")
    ap.add_argument("--port", type=int, default=8765)
    args = ap.parse_args()

    torch, module = P.load_model("cpu")
    index = Index(args.work, args.store)
    cache = {}

    def text_vector(prompt, template):
        key = (prompt, template)
        if key not in cache:
            e = P.embed_text(torch, module, [P.TEMPLATES[template].format(prompt)])[0]
            cache[key] = e / np.linalg.norm(e)
        return cache[key]

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def send(self, code, body: bytes, kind: str, extra=None):
            self.send_response(code)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(len(body)))
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            url = urlparse(self.path)
            q = {k: v[0] for k, v in parse_qs(url.query).items()}
            if url.path == "/":
                return self.send(200, page().encode(), "text/html; charset=utf-8")
            if url.path == "/report":
                # prototype.py report's page, served here rather than opened as a
                # file: a browser blocks file:// audio from some pages, and a
                # browser extension can't read a file:// page at all.
                html = (args.work / "report.html").read_text()
                html = html.replace("player.src='file://'+s.path.split('/').map(encodeURIComponent).join('/');",
                                    "player.src='/song?p='+encodeURIComponent(s.path);")
                return self.send(200, html.encode(), "text/html; charset=utf-8")
            if url.path == "/label":
                # vocals.py pick's songs, to be marked by ear as sung or not.
                songs = (args.work / "vocal-label-set.json").read_text()
                return self.send(200, LABEL_PAGE.replace("__SONGS__", songs.replace("</", "<\\/")).encode(),
                                 "text/html; charset=utf-8")
            if url.path == "/song":
                path = q.get("p", "")
                if path not in index.tags:
                    return self.send(404, b"", "text/plain")
                return self.file(path)
            if url.path == "/search":
                index.reload()
                prompt = q.get("q", "").strip()
                if not prompt:
                    return self.send(400, b"{}", "application/json")
                template = q.get("template", "this-audio")
                setting_name = q.get("clips", "6")
                alpha = float(q.get("alpha", "0.5"))
                with index.lock:
                    paths, setting = index.paths, index.settings[setting_name]
                scores, windows = setting.score(text_vector(prompt, template)[None], alpha)
                top = np.argsort(-scores[:, 0])[:int(q.get("n", "30"))]
                results = []
                for i in top:
                    tags = index.tags[paths[i]]
                    results.append({"id": int(i), "title": tags["title"], "artist": tags["artist"],
                                    "album": tags["album"], "genre": tags["genre"],
                                    "score": round(float(scores[i, 0]), 3), "start": int(windows[i, 0]) * 10})
                body = json.dumps({"songs": len(paths), "results": results}, ensure_ascii=False).encode()
                return self.send(200, body, "application/json")
            m = re.fullmatch(r"/audio/(\d+)", url.path)
            if m:
                with index.lock:
                    paths = index.paths
                i = int(m.group(1))
                if i >= len(paths):
                    return self.send(404, b"", "text/plain")
                return self.file(paths[i])
            self.send(404, b"", "text/plain")

        def file(self, path):
            size = os.path.getsize(path)
            kind = mimetypes.guess_type(path)[0] or "application/octet-stream"
            if path.lower().endswith(".m4a"):
                kind = "audio/mp4"
            start, end = 0, size - 1
            r = re.fullmatch(r"bytes=(\d*)-(\d*)", self.headers.get("Range", ""))
            if r:
                if r.group(1):
                    start = int(r.group(1))
                    end = int(r.group(2)) if r.group(2) else size - 1
                else:
                    start = max(size - int(r.group(2)), 0)
                end = min(end, size - 1)
            with open(path, "rb") as f:
                f.seek(start)
                body = f.read(end - start + 1)
            headers = {"Accept-Ranges": "bytes"}
            if r:
                headers["Content-Range"] = f"bytes {start}-{end}/{size}"
            self.send(206 if r else 200, body, kind, headers)

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"open http://127.0.0.1:{args.port}", flush=True)
    server.serve_forever()


PAGE = r"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Vibe Search</title>
<style>
:root{--bg:#fff;--fg:#1d1d1f;--muted:#6e6e73;--line:#e5e5ea;--accent:#c2185b;--row:#f5f5f7}
@media (prefers-color-scheme:dark){:root{--bg:#1c1c1e;--fg:#f2f2f7;--muted:#98989f;--line:#38383a;--accent:#ff6fa3;--row:#2c2c2e}}
*{box-sizing:border-box}
body{background:var(--bg);color:var(--fg);font:14px/1.45 -apple-system,system-ui,sans-serif;margin:0 auto;padding:16px;max-width:900px}
h1{font-size:20px;margin:0 0 2px} .muted{color:var(--muted)}
form{display:flex;flex-wrap:wrap;gap:8px;margin:14px 0}
input[type=text]{flex:1 1 320px;font:inherit;font-size:16px;padding:9px 12px;border-radius:10px;border:1px solid var(--line);background:var(--row);color:var(--fg)}
select,button{font:inherit;padding:6px 10px;border-radius:8px;border:1px solid var(--line);background:var(--row);color:var(--fg);cursor:pointer}
button.go{background:var(--accent);border-color:var(--accent);color:#fff}
.chips{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:10px}
.chips button{border-radius:14px;padding:3px 10px;font-size:13px}
ol{list-style:none;padding:0;margin:0}
li{display:grid;grid-template-columns:28px 36px 1fr auto;gap:8px;align-items:center;padding:7px 4px;border-bottom:1px solid var(--line)}
li.playing{background:var(--row)}
li .n{color:var(--muted);text-align:right;font-variant-numeric:tabular-nums}
li .t{font-weight:500;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
li .a{color:var(--muted);font-size:13px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
li .s{color:var(--muted);font-size:12px;text-align:right;font-variant-numeric:tabular-nums}
.play{width:32px;height:32px;padding:0;border-radius:50%}
</style></head><body>
<h1>Vibe search</h1>
<div class="muted" id="status">Type how the music should feel. Each match plays from its best-matching 10 seconds.</div>
<form id="f">
<input type="text" id="q" placeholder="music for a rainy day" autofocus>
<select id="clips"><option value="3">3 clips</option><option value="6" selected>6 clips</option><option value="12">12 clips</option><option value="whole">whole song</option></select>
<select id="alpha"><option value="0">best clips</option><option value="0.5" selected>balanced</option><option value="1">average</option></select>
<select id="template"><option value="this-audio" selected>"This audio is …"</option><option value="bare">as typed</option><option value="sounds-like">"A song that sounds like …"</option></select>
<button class="go">Search</button>
</form>
<div class="chips" id="chips"></div>
<ol id="results"></ol>
<audio id="player"></audio>
<script>
const $=id=>document.getElementById(id);
const examples=["music for a rainy day","music to go jogging","sad piano music without singing","energetic rock with female vocals","relaxing SNES-style music for studying","jazz for a late night","ambient music for falling asleep","angry heavy metal","funky groovy bass","epic movie soundtrack"];
for(const e of examples){const b=document.createElement('button');b.type='button';b.textContent=e;b.onclick=()=>{$('q').value=e;search()};$('chips').appendChild(b)}
const player=$('player');let playing=null;
async function search(){
 const q=$('q').value.trim();if(!q)return;
 $('status').textContent='Searching…';
 const p=new URLSearchParams({q,clips:$('clips').value,alpha:$('alpha').value,template:$('template').value});
 const r=await fetch('/search?'+p);const d=await r.json();
 $('status').textContent=`Top ${d.results.length} of ${d.songs} songs embedded so far.`;
 const ol=$('results');ol.innerHTML='';
 d.results.forEach((s,i)=>{
  const li=document.createElement('li');
  li.innerHTML=`<span class="n">${i+1}</span><button class="play" title="play from ${s.start}s">▶</button><div><div class="t"></div><div class="a"></div></div><span class="s"></span>`;
  li.querySelector('.t').textContent=s.title;
  li.querySelector('.a').textContent=[s.artist,s.album,s.genre].filter(Boolean).join(' · ');
  li.querySelector('.s').textContent=s.score.toFixed(3);
  const b=li.querySelector('.play');
  b.onclick=()=>{
   if(playing===li&&!player.paused){player.pause();b.textContent='▶';return}
   if(playing){playing.classList.remove('playing');playing.querySelector('.play').textContent='▶'}
   if(playing!==li){player.src='/audio/'+s.id;player.addEventListener('loadedmetadata',()=>{player.currentTime=s.start},{once:true})}
   player.play();playing=li;li.classList.add('playing');b.textContent='❚❚';
  };
  ol.appendChild(li);
 });
}
$('f').onsubmit=e=>{e.preventDefault();search()};
for(const id of ['clips','alpha','template'])$(id).onchange=()=>{if($('q').value.trim())search()};
</script></body></html>
"""


LABEL_PAGE = r"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Vocals or Not</title>
<style>
:root{--bg:#fff;--fg:#1d1d1f;--muted:#6e6e73;--line:#e5e5ea;--accent:#c2185b;--row:#f5f5f7}
@media (prefers-color-scheme:dark){:root{--bg:#1c1c1e;--fg:#f2f2f7;--muted:#98989f;--line:#38383a;--accent:#ff6fa3;--row:#2c2c2e}}
*{box-sizing:border-box}
body{background:var(--bg);color:var(--fg);font:14px/1.45 -apple-system,system-ui,sans-serif;margin:0 auto;padding:16px;max-width:820px}
h1{font-size:20px;margin:0 0 4px} .muted{color:var(--muted)}
ol{list-style:none;padding:0;margin:12px 0 0}
li{display:grid;grid-template-columns:28px 36px 1fr auto;gap:8px;align-items:center;padding:7px 4px;border-bottom:1px solid var(--line)}
li.playing{background:var(--row)}
.n{color:var(--muted);text-align:right;font-variant-numeric:tabular-nums}
.t{font-weight:500;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.a{color:var(--muted);font-size:13px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
button{font:inherit;padding:5px 9px;border-radius:8px;border:1px solid var(--line);background:var(--row);color:var(--fg);cursor:pointer}
button.on{background:var(--accent);border-color:var(--accent);color:#fff}
.play{width:32px;height:32px;padding:0;border-radius:50%}
.choice{display:flex;gap:4px;flex-wrap:wrap;justify-content:flex-end}
</style></head><body>
<h1>Vocals or not</h1>
<div class="muted">Does the song have someone singing or rapping words, anywhere in it? ▶ starts at the 10 seconds most likely to have a voice; skip around if you're unsure. Wordless humming or "ooh"s count as no vocals. Speech samples count as vocals.</div>
<div id="progress" class="muted" style="margin-top:8px"></div>
<ol id="list"></ol>
<audio id="player"></audio>
<script>
const SONGS=__SONGS__;
const $=id=>document.getElementById(id);
let labels={};try{labels=JSON.parse(localStorage.getItem('vibe-vocals')||'{}')}catch(e){}
const save=()=>{try{localStorage.setItem('vibe-vocals',JSON.stringify(labels))}catch(e){}};
const player=$('player');let playing=null;
function progress(){const n=SONGS.filter(s=>labels[s.path]).length;$('progress').textContent=`${n} of ${SONGS.length} labelled`}
SONGS.forEach((s,i)=>{
 const li=document.createElement('li');
 li.innerHTML=`<span class="n">${i+1}</span><button class="play">▶</button><div><div class="t"></div><div class="a"></div></div><div class="choice"></div>`;
 li.querySelector('.t').textContent=s.title;
 li.querySelector('.a').textContent=[s.artist,s.album].filter(Boolean).join(' · ');
 const b=li.querySelector('.play');
 b.onclick=()=>{
  if(playing===li&&!player.paused){player.pause();b.textContent='▶';return}
  if(playing){playing.classList.remove('playing');playing.querySelector('.play').textContent='▶'}
  if(playing!==li){player.src='/song?p='+encodeURIComponent(s.path);player.addEventListener('loadedmetadata',()=>{player.currentTime=s.start},{once:true})}
  player.play();playing=li;li.classList.add('playing');b.textContent='❚❚';
 };
 const c=li.querySelector('.choice');
 const draw=()=>{for(const x of c.children)x.classList.toggle('on',labels[s.path]===x.dataset.v)};
 for(const [label,v] of [['vocals','vocal'],['no vocals','instrumental'],['not sure','unsure']]){
  const x=document.createElement('button');x.textContent=label;x.dataset.v=v;
  x.onclick=()=>{labels[s.path]=labels[s.path]===v?undefined:v;save();draw();progress()};c.appendChild(x)}
 draw();$('list').appendChild(li);
});
progress();
</script></body></html>
"""


if __name__ == "__main__":
    main()
