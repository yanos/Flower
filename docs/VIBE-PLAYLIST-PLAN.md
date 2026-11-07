# Vibe Playlist Plan

A playlist made from a sentence: "music for a rainy day", "music to go
jogging", "sad piano without singing". It never runs out. Pressing play starts
an endless queue of the library's songs that *sound* like the sentence, and
the queue keeps topping itself up.

**Status (November 2026): the server runs the engine (phases 1 and 2), and
phase 3, the playlist on a paired desktop and in the browser, is built and
waiting to be tried on the real library.** `Flower.Core/SemanticAudio/` has the tokenizer,
the clip grid, the waveform preparation, the model download, the analyzer,
the index, the search and its filters, and `Flower.SemanticAudio.Onnx` runs
the two encoders. The C# pipeline matches Python's: waveforms to the sample,
embeddings at cosine 0.9999998 or better. The model is published as the
`music_audioset_epoch_15_esc_90.14` release. `Flower.Server` hosts the engine
behind its settings, with an AI Features tab on its settings page (the
browser's, and an admin desktop's server pane) and `vibePlaylists` in `/info`.
A paired desktop and a browser tab make Vibe Playlists from a sentence, with a
preview, and play them as an endless queue (phase 3). This plan replaces
`PROMPT-PLAYLIST-PLAN.md`, which had a language model read the sentence
against the names of the library's albums. The playlist's shape carries over
from it: a pool, an endless queue, skips remembered. What changes is how songs
are picked. A CLAP model listens to every song once and runs on hardware the
owner already has. There is no subscription, no API key and no per-playlist
cost, and nothing leaves the house.

## Decisions already made (by the owner, November 2026)

1. **Model: LAION's CLAP music checkpoint, `music_audioset_epoch_15_esc_90.14`**
   (Apache-2.0, trained on music only). Nobody publishes it as ONNX, so a
   script in this repo converts and quantizes it once. The files are hosted
   on a GitHub release of this repo and pinned by SHA-256 (see "The model
   files").
2. **Precision: fixed, not a setting.** The audio encoder runs at int8 and the
   text encoder at fp16. Phase 0 measured each against fp32 over the sample:
   int8 audio keeps 98% of every prompt's top 10 at twice fp32's speed and
   under a third of its size, while int8 text keeps only 74% (40% on the worst
   prompt), and text runs once per search, so its speed doesn't matter.
   fp16 audio was worse than int8 and slower than fp32 on a CPU, so it isn't
   shipped. This was a setting in the first draft; with one choice clearly
   best on each side, it would only have offered worse results.
3. **How many clips per song: a setting.** It ranges from 2-3 evenly spaced
   10-second clips up to the whole song in consecutive 10-second parts.
   **Every clip analysed is kept.** Lowering the setting ignores clips rather
   than deleting them, and raising it analyses only the clips that were never
   analysed (see "Clips").
4. **Where it runs: wherever the library lives, with no placement setting.**
   A paired device has the server do *everything*, the prompt's text included,
   and holds no model and no embeddings itself. A device on its own does
   everything in-process. A split with text on the phone and audio on the
   server was considered and dropped (see "Where it runs").
5. **It plays as an endless queue**, as the prompt-playlist plan designed it.
6. **Two switches: AI Features, off by default, and Vibe Playlist under it.**
   AI Features is the master switch over every feature that runs a model on
   the host's own songs, and each feature has its own switch under it, on by
   default, so turning AI Features on turns every feature on. A feature runs
   only while both are on, and either one turned off deletes its model files
   and every embedding. (First a single switch; the owner asked for the
   master and per-feature split in November 2026.)
7. **Negative prompts: not now.** See "Later".
8. **The name is Vibe Playlist.**

## Why CLAP

Nothing in the library's tags can answer "music for when it rains". Measured on
the first server's library (October 2026): 16,131 songs on 1,394 albums by 971
artists. **65% of songs have no genre**, and **none has a BPM**. A language
model can fill that gap only for records it has heard *of*. It guesses from
artist and album names, so it is weakest on most of this library.

CLAP (Contrastive Language-Audio Pretraining) is two encoders trained so that
a clip of audio and a sentence describing it land close together in one
512-dimensional space:

- **The audio encoder** (HTSAT) turns 10 seconds of audio into a vector. It is
  expensive, but runs **once per song**, ahead of time.
- **The text encoder** (RoBERTa-base, about 125M parameters) turns the
  sentence into a vector of the same kind. It is cheap, and runs **once per
  prompt**.
- **Ranking** is a cosine similarity of that one vector against every song's.
  For 16k songs that is a few milliseconds.

It judges the sound itself, so an unknown artist is as findable as Miles
Davis. That is the one thing a language model cannot do here.

What it is bad at, and what this plan does not pretend to solve:
- **Facts about songs rather than sound**: "christmas music", "songs from
  Tarantino films", "90s hip hop".
- **Instructions**: "30 songs, no repeated artists, starting calm". Ordinary
  playlist logic runs on top of the ranking instead (see "The endless queue").
- **Languages other than English.** The text encoder is English RoBERTa, so a
  French prompt will match poorly.

## Where it runs

The same rule as `docs/ONLINE-METADATA-PLAN.md`:

- **The engine is one piece of code in `Flower.Core`** (`Flower.Core/SemanticAudio/`),
  running on whichever host owns the songs. It is named for what it does,
  matching audio against the meaning of a sentence: its folders, types and
  tables say `SemanticAudio`. "Vibe" is the feature's name, and stays on what
  a listener or a client sees: the playlist and its fields, the settings, the
  routes and their refusals.
- **ONNX Runtime is a project of its own, `Flower.SemanticAudio.Onnx`**, with
  the two encoders behind `IClapAudioEncoder` and `IClapTextEncoder`, which
  stay in `Flower.Core`. Every head references `Flower.Core`, and ONNX
  Runtime's iOS build of its managed library binds `__Internal` symbols that
  only its native package provides. Through `Flower.Core` it would reach the
  phones' AOT links long before phase 6 decides whether a phone runs the
  engine. So a head that hosts the engine references the project and hands
  the engine its encoders.
- **A paired device asks its server for everything.** It sends the sentence
  and gets songs back. It downloads no model and keeps no embeddings, and its
  settings show the server's switch rather than one of its own. Paired is how
  a listener's phone is meant to be used, and a phone that ships nothing
  ~200 MB large is what `CLAUDE.md`'s "listeners are not technical" asks for.
  The server already has every song's file, so it never has to stream audio
  to analyse it.
- **The browser tab** always asks the server.
- **A device on its own** downloads both encoders and analyses its own files,
  which are all local.

The split the owner first proposed (text encoder on the phone, audio on the
server) would let a paired phone make a new Vibe Playlist offline. It was
dropped. It costs every paired phone a 127 MB download and a synced copy of
the library's embeddings, for the case of making a *new* playlist with no
connection, and with no connection that phone cannot stream most of the songs
it would pick anyway. An existing Vibe Playlist's pool syncs like any
playlist's songs, so the songs it has downloaded still play offline.

## The model files

`scripts/semantic-audio/export-clap-onnx.py` (Python, run once by hand, never by the
build) does the following:

1. Loads LAION's own checkpoint, `music_audioset_epoch_15_esc_90.14.pt`
   (2.35 GB, from the Hugging Face repo `lukewys/laion_clap`, pinned by
   SHA-256), with LAION's own `laion_clap` package. **Not** Hugging Face's
   conversion, `laion/larger_clap_music`: phase 0 found its text encoder
   broken. Every prompt came out at cosine 0.999 with every other, so every
   prompt ranked the library the same way, under both transformers 4 and 5.
   The conversions of the sibling checkpoints (`clap-htsat-unfused`,
   `larger_clap_general`) don't have the fault, so it is that one upload.
2. Exports two graphs with `torch.onnx.export` (opset 17, the TorchScript
   exporter):
   - **Audio**: `waveform` `[1, 480000]`, 10 s of 48 kHz mono float → `[1, 512]`.
     The spectrogram, log-mel and batch norm are layers of LAION's model
     (torchlibrosa), so they are in the graph and Flower computes no
     features. The batch is fixed at 1. HTSAT reshapes by the batch size it
     was traced with, so a dynamic axis exported cleanly and then gave
     cosine 0.17 with PyTorch at any other batch.
   - **Text**: `input_ids` and `attention_mask` `[B, 77]` → `[B, 512]`, with
     a dynamic batch axis.

   Both output the projected embedding, before normalisation.
3. Writes the two graphs Flower ships, plus fp32 of both as the reference the
   other two are measured against:
   - **Audio, int8**: `onnxruntime.quantization.quantize_dynamic`, weights
     only, `QInt8`, per channel, `reduce_range`, MatMul and Gemm. The
     spectrogram and log-mel nodes at the front of the graph are excluded:
     quantizing them drops the cosine with fp32 to 0.19. `reduce_range`
     because without it an x64 CPU without VNNI saturates (see "The second
     export" below).
   - **Text, fp16**: `onnxruntime.transformers.float16`, with inputs and
     outputs kept as fp32. (`onnxconverter-common` writes Cast nodes that ONNX
     Runtime refuses to load.)
4. Copies the tokenizer files (`vocab.json`, `merges.txt`).
5. Writes reference outputs for the C# tests (see "Testing").
6. Writes `manifest.json` with every file's name, size and SHA-256.

Sizes and speeds, measured in phase 0 on an M1 Max, one thread. **Bold** is
what ships, about 330 MB in all:

| | fp32 | fp16 | int8 |
|---|---|---|---|
| Audio encoder | 285 MB, 0.41 s/clip | 143 MB, 0.46 s/clip | **81 MB, 0.20 s/clip** |
| Text encoder | 501 MB, 0.16 s/prompt | **251 MB, 0.16 s/prompt** | 127 MB, 0.05 s/prompt |

How much of fp32's top 10 each keeps, over the 28 prompts in
`scripts/semantic-audio/prompts.txt` (worst prompt in brackets): audio int8 with text
fp16, 98% (90%); audio fp32 with text int8, 74% (40%); audio fp16 with text
fp32, 80% (50%).

The files go up as assets of a release of this repo tagged with the name of
the export: the checkpoint's own name, and for this second export what
changed, `music_audioset_epoch_15_esc_90.14_reduce_range`. The audio encoder
carries that name too (`music_audioset_epoch_15_esc_90.14_reduce_range-audio-int8.onnx`);
the text encoder and tokenizer are the first export's files under their own
names (`music_audioset_epoch_15_esc_90.14-text-fp16.onnx`, and so on). Not `larger_clap_music`, though that is the name the model usually
goes by: that is Hugging Face's conversion, the broken one, and naming the
release after it would point at the wrong file. The release is a pre-release,
because the dmg's update check asks GitHub for `releases/latest`
(`GitHubReleaseUpdater`), and a full release here could be the one it gets
back. The tag doesn't start with `v`, so the suite runs on it as on any push
but nothing is built or published. `SemanticAudioModelManifest` in
`Flower.Core` names the release URL and each file's size and SHA-256, so the
binary pins exactly what it will load. A new export is a new release and a
code change, never a silent swap. LAION's Apache-2.0 licence and attribution
go into `NOTICE` and into the release's notes.

**The second export** (November 2026). The first,
`music_audioset_epoch_15_esc_90.14`, quantized the audio encoder without
`reduce_range`. On an x64 CPU without VNNI (Intel before Cascade Lake and
Ice Lake, AMD before Zen 4) ONNX Runtime multiplies uint8 activations by
int8 weights with `VPMADDUBSW`, which adds pairs of products into 16 bits
and saturates there; 7-bit weights cannot overflow it. Measured against
PyTorch's fp32 embeddings:

| | Mac (5921 clips): cosine mean / worst | top 10 / 25 / 50 kept | i5-8259U (128 clips): cosine mean / worst | s/clip on the i5, 1 thread |
|---|---|---|---|---|
| int8, the first export | 0.99955 / 0.99761 | 98.2 / 98.0 / 98.4% | **0.939 / 0.807** | 0.340 |
| **int8, `reduce_range`** | 0.99929 / 0.99679 | 96.8 / 97.9 / 98.1% | 0.99927 / 0.99781 | 0.355 |
| uint8 weights | 0.99958 / 0.99793 | 96.4 / 98.0 / 98.8% | 0.99956 / 0.99896 | 0.421 |
| fp32 | | | 1.0000 | 0.418 |

On the Mac all three run at 0.20 s/clip, and their rankings differ by a
song or two across the 28 prompts, which those prompts cannot tell from
noise. `reduce_range` fixes the i5 for 4% more time per clip; uint8 fixed it
too but ran no faster than fp32 there. The new file has its own name
because a device knows the model by name and size and the two are the same
size to the byte, and its own `model` string, so every server hears its
library again. A download deletes whatever files the manifest no longer
names, so the first export's 80 MB do not stay behind.

**Downloading** (`SemanticAudioModelStore`): when the switch goes on, the store fetches
the two encoders, plus the tokenizer files, into `<data dir>/semantic-audio/models/`. It resumes with ranged GETs, checks
each SHA-256 before the file counts as present, and reports bytes downloaded
for the status line. A file that fails its hash is deleted and fetched again,
and two failures leave the status at "Couldn't download the model" with
Retry. A connection that drops, or sends nothing for a minute, is retried
five times, resuming each time, before the status shows it. On a server in a container, `<data dir>` is the volume, so the model
survives an image update.

## Analysing songs

`SemanticAudioAnalyzer` is a background job, one per host:

- **What it works on.** Songs missing a clip the current setting selects
  (see "Clips"), and songs whose clips are stale. Stale means the clips were
  analysed by a different export of the model (`semantic_audio_songs.model`),
  or from audio that has changed since (`semantic_audio_songs.file_stamp`; see
  "Storage"): a re-encode, or a file the server replaced
  (`Track.FileReplacedAt`). A stale song loses all its clips, because they
  describe audio or a model that is gone. A tag edit doesn't make a song
  stale, because the audio is the same, even though the file is rewritten.
- **When it runs.** When the switch is on and the model is present, from the
  host's start, and again on `Library.LibraryChanged` and on `TrackChanged`
  for a replaced file. On the server it runs after the startup rescan and never
  alongside one: `LibraryRescanCoordinator.Started` pauses the engine, which
  stops the pass (the song it was on is heard again later), and `Completed`
  resumes it. Only the listening waits: the model downloads and the index
  loads meanwhile, so searches don't wait out a long startup scan.
- **Order.** Recently added songs first, so a new album becomes findable
  first, then the rest of the library from the most played down.
- **Resumable.** Each song is committed when it is done, so a restart loses at
  most the song in progress.
- **Polite.** One song at a time and one inference thread
  (`IntraOpNumThreads = 1`) by default, at below-normal thread priority. On a
  desktop on its own, playback must never stutter for it. On a NAS, it must
  not starve the streams it serves.
- **Decoding.** `FFAudio.Decoder.OpenPath(path, F32, 48000, 1)`: CLAP's
  48 kHz, mono, float. Straight through from the start, discarding what lies
  between the clips, **never by seeking**. The first draft seeked to each
  clip. Measured on 60 songs of the library in phase 1, a seek landed up to
  48 ms early in AAC and MP3 alike: the encoder's delay (2,112 samples for
  AAC), which FFAudio's reported landing doesn't account for. That took
  embeddings down to cosine 0.925 against Python's, where decoding straight
  through gave 0.9987 or better on every clip. A whole song decodes in
  0.30 s on an M1 Max (900× real time), against 1.2 s of inference for six
  clips, so being exact costs about a quarter more time per song. The early
  landing is FFAudio.NET's to fix, and it would also move a playback seek by
  as much (inaudibly).
- **Failures** (cannot decode, too short to hold any audio) are recorded with
  a reason, so a broken file is not retried on every pass. They are counted in
  the status and retried only when the file changes.

### Clips

The setting is **Clips per song**, with these choices: *3* · *6* · *12* ·
*Whole song*. The default comes from phase 0; the conversation that led here
suggested 4-8. The clip is always 10 s (480,000 samples), which is what the
model was trained on.

**Clips sit on a fixed grid, so every setting is a choice among the same
clips.** A song is divided once into 10 s windows from 0: window *k* covers
[10k s, 10k + 10 s). A final window shorter than 3 s is not part of the grid,
and one between 3 and 10 s is repeat-padded (the model's own `repeatpad`
rule). A clip is identified by its window index, and every setting only picks
indices:

- **Whole song:** every window.
- **N clips:** for *i* in 0…N−1, the window containing the point
  (0.05 + 0.9 · (i + 0.5) / N) of the song's duration. Centres skip the first
  and last 5%, because intros and fade-outs are the least representative 10
  seconds of most songs. Evenly spread clips are the point for game
  soundtracks and anything else that changes within one track. Two centres
  that fall in one window count once, so a short song gets fewer clips.
- **A song shorter than 10 s:** window 0, repeat-padded.

Because the grid never moves, changing the setting reuses everything already
analysed. One more rule makes that true of every lowering, not just one from
the whole song. Evenly spread points don't nest: the 3 clips aren't among the
6, since 3's points fall halfway between 6's. So **a point whose own window
isn't stored takes the nearest stored window instead**, when that window is
within 0.3 of the spacing between the setting's points
(`ClipGrid.StandInShare`). A lower setting's points always sit within a
quarter of its spacing of a higher one's, so lowering always finds them. A
higher setting's points sit half its spacing from a lower one's, so raising
mostly gets new windows. With nothing stored, the choice is phase 0's
exactly.

- **Lowering it** (whole song → 3, 12 → 6, 6 → 3) analyses nothing and
  deletes nothing. The extra clips stay in `semantic_audio_clips` and are
  ignored, and each song's mean is recomputed from the clips now selected.
  That is arithmetic on stored vectors, done in one pass in seconds.
- **Raising it** (3 → whole song) analyses only the windows that have no clip
  yet. Going 3 → 12 → whole song → 6 never analyses a window twice.
- **Clips are deleted only** when their song goes stale (new audio or new
  model), when the song leaves the library, or by the master switch.

The cost of the grid: a selected clip may sit up to 10 s from the ideal
evenly spaced point, and a stand-in clip somewhat further. Across a whole
song, that makes no difference to what the clips sample.

| Setting | Clips (16k songs, ~4 min average) | Storage (fp16, 1 KB a clip) | Analysis on lambda, 1 thread | On all 8 cores |
|---|---|---|---|---|
| 3 | ~48k | ~50 MB | 4 h | 1.5 h |
| 6 | ~97k | ~100 MB | 8 h | 3 h |
| 12 | ~194k | ~200 MB | 16.5 h | 6 h |
| Whole song | ~390k | ~400 MB | 36 h | 13 h |

Measured in phase 0 with `scripts/semantic-audio/bench.py`: the int8 audio encoder takes
0.31 s a clip on one thread of lambda's 8-core CPU (0.11 s on all of them),
against 0.20 s on an M1 Max. The times are inference only. Decoding the whole
song adds about a quarter (see "Decoding" above), and the analyzer measures
its own rate anyway (see "The settings page").

The storage column is what the clips stored for each setting take. Since
clips are kept, storage is set by the highest setting ever used, until the
master switch clears it. Raising the setting fills in missing windows in the
background, and until a song's are done, search uses the mean of the clips it
already has, so a playlist never stops working mid-change.

### Preprocessing: the part that must be exact

The audio graph computes its own spectrogram (see "The model files"), so what
`Flower.Core` does to a clip before it is short, and it is what LAION's own
`get_audio_embedding_from_data` does:

- **Decode** to 48 kHz mono float, through FFAudio (FFmpeg's `swresample`,
  the same resampler phase 0's `ffmpeg` command line used).
- **Cut** the 10 s window from the grid (see "Clips").
- **Round-trip through int16**: clamp to [−1, 1], scale by 32767, truncate to
  an integer, and divide by 32767 again. LAION's training data went through
  this, and so does every clip it embeds.
- **Pad a short clip by repeating it**: whole copies of the clip end to end,
  then zeros up to 480,000 samples (`repeatpad`). Only a song's last window
  can be short, and only a tail of 3 s or more is kept (see "Clips").

The rule for this part: if the C# waveform differs from the Python one, every
embedding is subtly wrong, and nothing fails. The playlists are just worse.
So the reference test in "Testing" decides whether this code is correct.
Reading the code does not. There was a mel spectrogram and an FFT to write
here in the first draft. Exporting LAION's model with its front end inside
removed both, and with them the test this plan most depended on.

### Tokenizing

The text side is RoBERTa's byte-level BPE (`vocab.json` + `merges.txt`). It
adds `<s>` and `</s>`, truncates to 77 tokens and pads to 77 with
`<pad>` = 1, as LAION's tokenizer call does. It is a small BPE of our own
(`RobertaTokenizer`, about 200 lines), not `Microsoft.ML.Tokenizers`: it has
no reflection for NativeAOT to trip on, and one job. It gives Hugging Face's
ids exactly for 30 awkward prompts (see "Testing"). The one thing left out:
Hugging Face splits the special tokens' own spellings (`<mask>`) out of the
text, and here they are plain text.

**Prompt template: `A song that sounds like {0}.`**, a constant, not a
setting. CLAP's captions were sentences, so phase 0 tried the prompt as typed
against two templates, and the template mattered more than anything else
measured: switching it changed 60-66% of a prompt's top 25, where the clip
count changed 15-23%. The owner marked every song in each template's top 25
(6 clips, α 0.5) as fitting or not:

| Prompt | As typed | `This audio is {0}.` | **`A song that sounds like {0}.`** |
|---|---|---|---|
| calm music for focusing at work | 25 / 0 | 24 / 1 | **25 / 0** |
| music for a rainy day | 15 / 10 | 15 / 10 | **17 / 8** |
| music to go jogging | 7 / 18 | 9 / 16 | **15 / 10** |

It ties or wins on each, and nearly doubles the fits for an activity, which
is the weakest kind of prompt (see "Open questions").

## Storage

Two new tables in `flower.db` (`SqliteMigrations` step 14), on whichever host
analyses, and a third with the playlist in phase 3:

```
semantic_audio_songs(track_id PK, model TEXT, file_stamp TEXT, duration_ticks INT,
           window_count INT, analysed_at INT, failure TEXT NULL)
semantic_audio_clips(track_id, window_index INT, embedding BLOB,
           PRIMARY KEY (track_id, window_index))     -- cascades from the song
vibe_playlist_skips(playlist_id, track_id, skipped_at INT)  -- phase 3, step 15
```

The skips are kept by the device that played the songs (`VibeSkipRepository`),
not by the host that ranks them: a skip is about who was listening, and the
queue that weighs it runs on the device. A browser tab, which has no database,
keeps them for the session. The playlist's two fields are columns of
`playlists` (`vibe_prompt`, `vibe_instrumental`).

- **Embeddings** are 512 × fp16 = 1 KB each, L2-normalised before storing.
- **`window_index`** is the clip's index on the song's 10 s grid (see
  "Clips"), so its start is `window_index × 10 s` and no position is stored.
  (`WINDOW` is an SQL keyword.)
- **No mean is stored.** It depends on the setting, and the index computes it
  from the clips when it loads, along with the vocal and speech scores, which
  need the clips anyway.
- **`model`** names the export that analysed the song
  (`music_audioset_epoch_15_esc_90.14_reduce_range/int8`), so a row from another export never mixes into
  a search.
- **`file_stamp`** is the song's audio identity: duration, codec, sample rate,
  channels, bit depth and bitrate as the last scan read them, plus
  `Track.FileReplacedAt` (`SemanticAudioAnalyzer.AudioStamp`). It is not the
  file's own size and modification time, because tags and artwork live in the
  file, so a tag edit would have the song heard again, and an artwork job
  across the library would have all of it heard again.
- **`track_id` is not a foreign key into `tracks`**, for the reason
  `playlist_tracks`' isn't. The analyzer deletes what has left the library
  itself, at the start of each pass.
- **None of it touches `Track`.** `Track` is an immutable record that syncs,
  and these are host-local derived data.

**`SemanticAudioIndex`** holds every song's mean embedding in memory as one contiguous
float array: 16k × 512 × 4 bytes = 32 MB on a server. Clip embeddings stay in
SQLite and are read only for reranking candidates.

## Search

`SemanticAudioSearch.Rank(prompt)`:

1. Template the prompt, tokenize, run the text encoder, normalise. About
   160 ms on one thread of an M1 Max (phase 0). Results are cached per
   prompt for the process's lifetime, which matters for a preview that runs on
   every pause in typing.
2. Take the dot product with every row of `SemanticAudioIndex`, and keep the top 500.
3. **Rerank** those 500 with their clips: score = α · mean similarity
   + (1 − α) · the mean of the song's best two clip similarities. This finds
   a song that is half quiet piano intro and half orchestra for "piano
   introduction that becomes epic", which an average blurs. α is a constant
   from phase 0: 0.5. Phase 0 found little between 0, 0.5 and 1: they share
   87-90% of a prompt's top 25, and the owner's marks moved by a song or two
   either way.
4. Return the songs in score order, with scores.

What a Vibe Playlist holds is a **pool**: the head of that ranking. It stops at
200 songs, or at the point where the score drops below the best score minus a
margin, whichever comes first. Phase 0 didn't settle the margin: it is set in phase 3, by listening to previews of real prompts. A library where little fits
should make a short pool, not 200 poor matches.

**Extending the pool is free now.** It takes the next songs down the same
ranking that are not already in the pool. The costly second loop in the
prompt-playlist plan, with its budgets and its money, is gone.

### Filters: what a prompt can't tell CLAP

Phase 0's listening found three things a ranking alone gets wrong. Each is a
filter applied to the ranking before the pool is cut, never a change to the
prompt:

- **Only music, always.** A comedy sketch can sound like whatever the prompt
  asks for, and the library has plenty: the jogging list had two in its top
  25. A song's *speech score* is its mean embedding's similarity to four "people
  talking / a comedy sketch" phrasings minus its similarity to four "music"
  phrasings. In the sample, the 17 songs scoring highest were all sketches,
  but audio alone caught only about half of the 22 songs tagged Comedy or
  Humour: a sketch with music under it scores as music. So a song is left out
  when its speech score is above 0 **or** its genre tag says Comedy, Humour or
  Spoken Word. Scoring each clip rather than the mean should catch more of
  the rest, and is still to be measured (phase 1 didn't).
- **No vocals, when asked.** A Vibe Playlist has a switch, *No vocals*
  (`Playlist.VibeInstrumental`, beside `VibePrompt`), that keeps only songs
  CLAP hears no voice in. It also turns on by itself when the prompt says so
  ("instrumental", "without lyrics", "no singing", "sans paroles", and a
  short list like them), and that phrase is cut from the prompt before it is
  encoded. Written into the prompt, the words did keep sung songs out, but
  they also moved the list towards rain for "jogging": they dilute what was
  asked.

  A song's *vocal score* is its most vocal selected clip's similarity to three
  "a song with a singer" phrasings minus its similarity to three
  "instrumental" phrasings. It takes the most vocal clip because one sung verse
  is enough for a song to have lyrics. The cut-off is **−0.15**, set in phase 0
  from 60 songs the owner labelled by ear, two thirds of them chosen from the
  range where the score is unsure. Every song below −0.14 was instrumental,
  and none was sung. The errors start just above that, with Stereolab,
  Broadcast and Elfin Saddle, all low-mixed, half-spoken voices at −0.14 to
  −0.10. Weighted back to the whole sample, the cut-off keeps about 60% of the
  library's instrumentals and lets almost no sung song through. That is the
  right mistake for "no lyrics", since a list that is shorter costs less than
  a voice where none was asked for. The score is arithmetic on stored clips,
  so it costs no analysis, and it is recomputed with the means when the clip
  setting changes.
- **Christmas songs, only when asked.** CLAP doesn't know what Christmas
  music is: the sample's seven Christmas songs ("White Christmas", "Silent
  Night", Brubeck's "Santa Claus Is Coming to Town") ranked between 113th and
  485th of 1,000 for "christmas music". Christmas is a fact about a song, not
  a sound, so the rule reads tags. A song whose title, album or genre names
  Christmas (whole words, so that "Santa Fe" isn't one: christmas, xmas, noël,
  navidad, weihnacht, "santa claus", "jingle bell", "silent night", and a short
  list like them) is left out of every Vibe Playlist, **unless the prompt
  names Christmas too**. Then the pool is those songs only, in CLAP's order,
  which also serves the "christmas music" prompt CLAP can't serve alone.

## The endless queue

Unchanged from the prompt-playlist plan. While a Vibe Playlist is what is
playing, a **queue source** keeps about ten songs upcoming, drawn from the
pool:

- shuffled, weighted by score;
- skipping any song played in this source's last N plays (N is most of the
  pool);
- never two in a row from one album, and an artist at most once in any four
  songs, unless the pool is too small to allow it.

When fewer than a quarter of the pool are eligible, the source extends the
pool. On a paired device that is one request, and if it fails the source
relaxes the repeat window rather than stopping. Shuffle and repeat do not apply
to it. Playing anything else ends the source and leaves the songs it queued
where they are.

**As built (phase 3)**, in `VibeQueueSource` and `PlaylistControlViewModel`:
the repeat window is every song queued in the session, so nothing comes back
until the whole pool has played; once fewer than a quarter are left the source
asks for more, and when the pool is spent it forgets all but the latest
quarter. The weight is by place in the pool, the best song four times as
likely as the last. Two songs are one album when the title and the album
artist match, so a compilation's songs, each with an artist of its own, count
as one album. The queue is topped up as each song starts, before the next is
armed, and Play Next, a drag or a removal don't end it.

**Skips.** A song skipped in its first 30 seconds is recorded against the
playlist (`vibe_playlist_skips`). It is not removed, but its weight is quartered
for that playlist. Using a skipped song's embedding to push *similar* songs
down as well is the negative-prompt idea, and waits with it.

**The song picked to start from** counts as queued (`VibeQueueSource.Queued`),
so it isn't drawn again in the next ten. It used to be, about one start in
three, which a flaky test was the first to notice.

## Endless Play

Asked for by the owner in November 2026, after phase 3: when a queue runs out,
keep playing songs that more or less match the last few. A second AI feature
beside Vibe Playlist, made from the same listening, with no sentence: the
seeds are songs.

- **Two switches, both off by default.** The server's, under AI Features
  beside Vibe Playlist, decides whether it is offered
  (`MusicLibrarySettings.EndlessPlayEnabled`). Each device has its own, so a
  listener chooses for themselves (`AppSettings.EndlessPlay`): Settings >
  General on the desktop and in the browser, the server section of the
  phone's Settings. A device's switch is greyed, with a line saying why,
  while its server doesn't offer it; it learns that from `/info`'s
  `endlessPlay` (a browser tab from its handshake). Off by default, unlike
  Vibe Playlist, because it changes what plays without anyone asking.
- **Either feature keeps the listening going.** The engine runs while the
  master switch and at least one feature are on
  (`MusicLibrarySettings.RunsSemanticAudio`), and only the last one turned
  off deletes the model and the analysis. Each feature's routes refuse on its
  own switch (`vibe-off`, `endless-play-off`).
- **When.** As the last song of the queue starts, with nothing after it, no
  repeat, no shuffle (which never runs out) and no Vibe Playlist (which keeps
  its own queue going), the device asks while that song plays, so the answer
  is armed behind it and follows gaplessly. If the queue plays out before the
  answer comes, the answer starts playing when it does. Anything lined up or
  played meanwhile wins, and the answer is dropped.
- **What.** The seeds are the last five songs of the queue; the songs ranked
  are those nearest the normalised average of their means
  (`SemanticAudioSearch.Similar`), so a run pulls toward what the songs
  share. Song against song, so no rerank by clips. The filters follow the
  seeds: Christmas songs only after a Christmas song, instrumentals only
  after nothing but instrumentals, spoken word never. The server answers
  with 50; the device leaves out the last 500 songs of its queue and draws
  ten the way a Vibe Playlist's queue draws (`VibeQueueSource`: weighted to
  the best, albums and artists apart), and adds them to the end. They are
  the queue's own from then on, so the next run-out asks again from them and
  a long session drifts the way listening does.
- **Where it runs.** `PlaylistControlViewModel` on every head that has a
  server to ask, so a paired phone has it too, with no phone UI beyond its
  switch. A device on its own waits for phases 5 and 6 like Vibe Playlists.

## Model of a playlist

- **`Playlist.VibePrompt`** is a string, and null for the other kinds. It is a
  `Touch()`ing property like `Rules`, and editing it rebuilds the pool.
- **`Playlist.VibeInstrumental`** is the *No vocals* switch (see "Filters").
  It is stored apart from the prompt, so a prompt that turned it on by saying
  "instrumental" can be edited without losing it, and a playlist can turn it
  off again.
- **The pool is the playlist's `Tracks`**, in score order and materialized the
  way a smart playlist's are, so download all, Add to Queue and the track list
  view work with no special case.
- **It is still not a smart playlist.** It reuses the playlist *row* and
  nothing of `SmartPlaylistRules` (see `docs/CITED-DECISIONS.md` on keeping
  radio out of smart playlists).
- **Sync.** `VibePrompt` and `VibeInstrumental` travel in `PlaylistSyncPlaylistDto` the way `Rules`
  does. Unlike rules, **the server's pool is the answer.** A paired device never
  ranks anything. It takes the server's tracks with the manifest, as for any
  playlist.

## The wire

Signed like every route (`PeerSignatureAuth`):

- **`POST /api/flower/v1/vibe/preview`** takes `{prompt}` and returns the first
  50 songs of the pool. The editor shows this while typing pauses. It saves
  nothing.
- **`POST /api/flower/v1/vibe-playlists/{id}/extend`** takes
  `{exclude: [...]}`, the songs the device already added beyond the pool, and
  returns the next ones down the same ranking. It stores nothing: the plan
  first had the extension join the server's pool, but the next rebuild cuts
  the pool where it always does, so the device keeps them for its session
  instead, and skips stay on the device (see "Storage").
- **The pool** reaches every device with its playlists. The server ranks it
  (`VibePlaylistPools`) and installs it as the playlist's songs without moving
  `UpdatedAt`, as a smart playlist's recomputation does. Sync decides a Vibe
  Playlist on its sentence alone (`PlaylistSyncPlanner`): the same sentence
  is no change, and the device then takes the server's songs as they stand; a
  newer sentence on either side wins outright. `/info`'s `playlistsToken`
  counts a Vibe Playlist's songs, so a device comes back for a rebuilt pool.
- **`POST /api/flower/v1/vibe/endless`** takes `{seeds: [...], exclude:
  [...]}`, by the server's ids, and returns up to 50 songs like the seeds,
  best first (see "Endless Play"). Not a listener's playlist, so any paired
  device may ask, on the same Vibe budget.
- **`/info`** carries `vibePlaylists`: off, analysing (with percent), or ready.
  A paired device shows New Vibe Playlist… only when the server says ready or
  analysing. And `endlessPlay`, whether the server offers Endless Play.
- **`GET /api/admin/vibe/status`** returns the status in "The settings page",
  for an admin's settings screen to poll; `POST /api/admin/vibe/pause` and
  `/resume` pause and resume the listening and answer with it.

The settings themselves travel in the existing admin settings DTO, as
`LookUpOnline` does.

Refusals use `FlowerProblem`:
- `vibe-off` (409): the switch is off;
- `endless-play-off` (409): Endless Play's switch is off;
- `vibe-not-ready` (409): no song is analysed yet, or the model is not
  downloaded;
- over the rate limit below, the gate's ordinary `429` `rate-limited`, on a
  plane of its own (`RequestGate.Plane.Vibe`, 60 a minute). The plan first
  named a `vibe-budget-spent` code; a client handles a 429 the same whatever
  the budget, so it wasn't added.

**Trust boundary.** With no money involved, the only thing a listener can
spend is server CPU: one text encode per uncached preview. So every paired
device may make Vibe Playlists, admin or not. Previews and extends get a
budget of their own per device (the same per-device rule as the other planes in
`docs/SECURITY.md`), about one preview a second with a small burst. The prompt
never leaves the server and is not logged above Debug: what someone wants to
listen to is their own business, even on a shared server.

## The settings page

An **AI Features** tab in `SettingsPanel` (desktop window, and the browser
for a server), and an **AI FEATURES** section in the phone's `SettingsView`.
Where each appears follows `LookUpOnline`:

| Screen | Shows |
|---|---|
| Server settings (browser, or an admin device's server screen) | The full page, for the server |
| A device on its own (desktop or phone) | The full page, for itself |
| A paired device's own settings | One line: "Vibe Playlists are made by *Server*, which has them on / off." No switch for that; Endless Play's own switch (see "Endless Play"). |
| A server older than these settings | "This server's version has no AI features. Update it to turn them on.", with every switch disabled: its status route answers 404. |

The full page:

- **AI Features**, the master switch, off by default, and indented under it
  **Vibe Playlist**, on by default, and **Endless Play**, off by default,
  both disabled while the master is off. The master and either feature on
  starts the model download, then analysis. Turning off the last one that
  needs it asks first, inline: the switches are drafted with the rest of
  the page, and then say "Saving stops listening to songs and deletes the
  model (*n* MB) and the analysis of *n* songs. Turning a feature back on
  means analysing every song again (about *t*)." Save is the yes;
  Cancel, or the switch turned back on, the no. A dialog has nowhere
  to go in a panel shown in a window and a browser tab alike. Then it stops the job
  and deletes `<data dir>/semantic-audio/` and every `semantic_audio_*` row. Vibe Playlists
  themselves stay, since they are the user's, but they cannot play new songs
  and say so until the switch is back on. The new playlists' rows are the
  only thing kept.
- **Clips per song**: *3* · *6* · *12* · *Whole song*, with what each costs in
  storage and roughly in time on this host, measured from its own analysis so
  far. Lowering it is instant. Raising it analyses only the clips never
  analysed before, in the background.
- **Status**: one line and a progress bar:
  - "Downloading the model: 34% (41 of 207 MB)"
  - "Analysing: 26% (4,210 of 16,131 songs), about 3 h left · now: *Song* by
    *Artist*"
  - "Up to date: 16,131 songs analysed" with, when there are any, "12 songs
    couldn't be analysed" (expandable to the list and reasons)
  - "Waiting for the library scan to finish", "Paused", or an error with
    Retry.
  - "Using *n* MB (model *n* MB, analysis *n* MB)".

  The percent is songs done over songs eligible. "Time left" is the job's own
  measured rate over its last few hundred songs. The settings view model polls
  status every 2 s while the tab is showing: an event in-process, and
  `GET /api/admin/vibe/status` for a server.
- **Pause** and **Resume**, under the status: applied at once rather than on
  Save, like Retry, and kept in the server's settings file
  (`MusicLibrarySettings.SemanticAudioPaused`) so a paused server stays
  paused across a restart. A pause stops the listening and a download still
  under way, and leaves searches working on what was heard. It is not the
  wait for a library scan (`SemanticAudioState.WaitingForScan`), which ends
  on its own; Pause is offered during that wait too, and Resume only after a
  pause.
- **Storage**: "Using *n* MB (model *n* MB, analysis *n* MB)".

Under the two features, a **Listening** section holds what both are made
from: the status, Pause, and clips per song.

The settings live in `MusicLibrarySettings` (`AiFeaturesEnabled`,
`VibeEnabled`, `EndlessPlayEnabled`, `VibeClipsPerSong`, `SemanticAudioPaused`;
`RunsVibePlaylists` and `RunsEndlessPlay` are a feature's switch with the
master, `RunsSemanticAudio` either of them), so the server's `FlowerServerOptions` and a device's
`AppSettings` both inherit them. They are drafted and saved like
`LookUpOnline`, and can be locked from the server's config file
(`SettingsOverrides`) like every other server setting.

## Testing

Golden files come from `scripts/semantic-audio/make-references.py` and are
committed as embedded resources: the tokenizer's under
`Tests/Flower.Tests/SemanticAudio/References/` (17 KB), the decoding ones
under `Tests/Flower.Tests.Decoding/SemanticAudio/References/` (130 KB). They
are small: prepared waveforms and embeddings for a handful of clips, not
models.

- **Tokenizer** (`RobertaTokenizerReferenceTests`). 30 prompts (punctuation,
  emoji, accents, Cyrillic and Japanese, odd whitespace, contractions, long
  input truncated) give exactly the HF `RobertaTokenizer` ids. The test runs
  against only the part of the vocabulary and merges those prompts use. BPE
  always applies the lowest-ranked pair present, so with exactly the merges
  applied it takes the same steps it takes with all 50k, and the 1.4 MB of
  real files stay out of the repo.
- **Waveforms** (`ClipWaveformReferenceTests`, `RequiresFfmpeg`). The 440 Hz
  fixture at 44.1 kHz and 14 s of two chords (a gzipped WAV, since its tones
  repeat exactly), decoded, resampled, cut and int16 round-tripped in C#,
  must equal Python's within 3 LSB per sample. The chord's second window is a
  4 s tail. **Measured: 0 LSB, every sample of every window.** The
  references store the int16 values themselves, since that is exactly what
  the round trip leaves.
- **Embeddings** (`ClapEmbeddingReferenceTests`, `RequiresClapModel`; the
  model is never committed). The C# pipeline's embedding of each reference
  clip and prompt must have cosine ≥ 0.999 with Python ORT's at the same
  precision, and Python's own waveforms must embed at 0.9995. **Measured:
  0.9999998 or better for all of them on a Mac, and 0.99987 or better for
  Python's own waveforms on an i5-8259U without VNNI.** The first export,
  without `reduce_range`, gave 0.962 on CI's AMD EPYC 7763 ("The second
  export" above); every message names the CPU, since CI's Linux runners are
  a mix. It reads the model from
  `FLOWER_CLAP_MODEL_DIR`, or `~/.cache/flower-semantic-audio/export`, and
  skips when neither has it. It is meant to run in one CI leg, Linux, the
  server's platform, with the cache keyed on the manifest hash, once the
  release exists. The iOS suite leaves it out, since no phone references the
  ONNX project.
- **Logic, with fake embeddings:**
  - grid selection for short, long and odd durations;
  - each setting's windows on that grid;
  - lowering the setting analyses nothing, and raising it analyses only
    missing windows (3 → whole → 6 never encodes a window twice);
  - stale detection (model and audio stamp, but not tags or the clip
    setting);
  ranking and rerank order; the three filters; pool cut-off; extension
  excluding the pool; queue-source rules (repeat window, album and artist
  spacing, skips down-weighted); the model download (resume, a server
  ignoring the range, a hash failing twice); purge leaving no
  `semantic_audio_*` row and no file. All but the pool and the queue source
  are written (phase 1); those two come with the playlist in phase 3.
- **Settings view model:** what each screen shows, the confirmation before
  off, and status text from a status object. Behaviour only, per `CLAUDE.md`.

## Phases

0. **Prototype, in Python, before any C#.** Run LAION's music checkpoint over
   about 1,000 of the real library's songs on lambda, with 20-30 real prompts.
   It answers, by listening:
   - Is it good enough? **Yes**, for prompts about sound and mood, with the
     filters (see "Filters").
   - Which prompt template wins? `A song that sounds like {0}.` (see
     "Tokenizing").
   - Which default clip count? 6 (see "Open questions").
   - Which α and pool margin? α is 0.5 (see "Search"). The pool margin is
     still open.
   - Does int8 keep the top-50 of fp32? For the audio encoder yes, for the
     text encoder no, so audio ships int8 and text fp16 (see decision 2).
     On lambda's CPU a clip takes 0.31 s on one thread (see "Clips").

   **Done, November 2026.** It ran on the owner's Mac rather than lambda
   (1,000 songs, 72 hours of music, 26,254 clips), with lambda timed
   separately by `scripts/semantic-audio/bench.py`. The scripts are in `scripts/semantic-audio/`:
   `export-clap-onnx.py`, `prototype.py` (sample, embed, report),
   `precision.py`, `bench.py`, `vocals.py`, and `try.py`, a local page that
   searches the sample, plays each match from its best clip, and serves the
   report and the vocals labelling.
1. **Export and engine.** Publish the `music_audioset_epoch_15_esc_90.14` release. In `Flower.Core/SemanticAudio/`:
   tokenizer, clip grid, waveform preparation, clip reader, model store,
   analyzer, index, search, filters, and `SemanticAudioEngine`, which runs them
   on a thread of its own behind the two settings. The ONNX sessions are in
   `Flower.SemanticAudio.Onnx` (see "Where it runs"). All the tests above but
   the playlist's.

   **Built, November 2026.** The release is published, and CI's Linux leg
   downloads it (cached on the manifest) and runs the reference tests with
   `FLOWER_REQUIRE_CLAP_MODEL`, so a missing model fails rather than skips.
   Besides what is noted in place above, it changed four things in this plan:
   decoding never seeks; a lower clip setting reuses nearby stored windows,
   so every lowering is free; the stale stamp is the audio's identity, not
   the file's; and no mean is stored. Left over: measuring the speech score
   per clip rather than on the mean (see "Filters").
2. **Server.** The engine hosted in `Flower.Server`: a reference to
   `Flower.SemanticAudio.Onnx` and FFAudio's native payload for the host (the
   server has no decoder today), the encoders handed to the engine, and a gate
   so a pass never runs alongside a rescan (`LibraryRescanCoordinator`). Then
   the settings in the admin DTO, `/info`, the status route, purge, and the
   settings tab in the browser and on an admin device's server screen. Check
   the Docker image on both architectures (ONNX Runtime's native library and
   ffaudio's in `mcr.microsoft.com/dotnet/aspnet:10.0`). FFAudio.NET.Linux
   ships x64 only, so an arm64 image has no decoder until it gets one.

   **Built, November 2026.** `SemanticAudioService` hosts the engine: it
   hands it ONNX Runtime's encoders (one thread each) and FFAudio's decoder,
   applies the two settings from `FlowerServerOptions` (the PUT applies them
   at once rather than waiting for the configuration watcher), and pauses it
   for every scan (`LibraryRescanCoordinator.Started`/`Completed`). The model
   comes through `SemanticAudioModelSource`, so the server tests turn the
   switch on against a handler that refuses. `SemanticAudioReport`, in
   `Flower.Core` so a device on its own can say the same things, turns the
   engine's state into `VibeStatusDto` and `/info`'s `vibePlaylists`, with
   each clip setting's cost for this library (`SemanticAudioCosts`: storage
   from the songs' lengths, time from the clip rate the analyzer measures).
   The tab is `AiFeaturesSettingsViewModel` (first a Vibe Playlists tab with
   one switch, then AI Features with a master and per-feature switch, at the
   owner's request), held by `SettingsViewModel`; a paired
   device's one-line note waits for phase 3, which is the first thing to
   read `vibePlaylists`. The server's output drops ONNX Runtime's iOS and
   Android builds (110 MB). Measured on a throwaway server with three real
   songs: the release downloaded and checked in 25 s, and every song was
   heard at 6 clips.

   The image, on Colima: arm64 loads ONNX Runtime, finds no decoder and
   says so once ("This host has no audio decoder for its platform"). amd64,
   run under Rosetta, analyses the same three songs as the Mac, but only with
   workstation GC: with Server GC, ASP.NET's default, the engine thread hung
   on its first song, waiting on a futex. On lambda, a real x64 Linux host,
   the same image with Server GC analysed all three at about 0.35 s a clip
   on one thread, so it was Rosetta: .NET suspends threads for a collection
   with signals, which Rosetta does not handle as Linux does. Colima's amd64
   is no test of this image.
3. **The playlist, on a paired desktop and in the browser.** The new kind, New
   Vibe Playlist… with a preview, the queue source, skips, and sync of
   `VibePrompt`.

   **Built, November 2026, not yet tried on the real library.**
   `Playlist.VibePrompt` and `VibeInstrumental` (`IsVibe`; a Vibe Playlist is
   not `IsOrdinary`, so nothing can be dropped on it), stored in `playlists`
   and carried by `PlaylistSyncPlaylistDto`. The server keeps every pool with
   `VibePlaylistPools`, started by `SemanticAudioService`, and answers
   `VibeEndpoints`' two routes. A device asks through `VibePlaylistClient`
   (`IVibePlaylists`), on the desktop and in the browser alike, and offers
   New Vibe Playlist… (the sidebar's + and its empty space, and the macOS
   menu bar) while its server says `analysing` or `ready`; the desktop learns
   that from `/info` (`AppSettings.PairedServerVibePlaylists`), the browser
   from its handshake. The editor (`VibePlaylistEditorViewModel`,
   `VibePlaylistEditorView`) is a dialog on the desktop and a panel over the
   page in the browser: a sentence, a name that defaults to the sentence, No
   vocals, and the preview after each pause in typing. Saving keeps the
   preview's songs as the pool until the server's arrives. Play, a song
   double-clicked in it, or the menu's Play starts the endless queue; the
   menu has Edit Vibe Playlist… and no Shuffle, and the Queue screen says
   where its songs come from. A paired device's own settings say in one line
   whether its server makes them.

   Still to do in this phase:
   - **Set the pool margin by listening.** It is 0.15 below the best song for
     now (`VibePlaylistPools.PoolMargin`), from phase 0's 1,000-song sample,
     where a prompt's 25th song sat 0.07 to 0.24 below its first.
   - **A browser tab doesn't come back for a rebuilt pool** until it is
     reloaded: it reads playlists once, at load (`OriginPlaylistImporter`),
     and nothing there watches `playlistsToken` yet.
4. **A paired phone.** The same editor and queue source on
   `MobileMainViewModel`. Nothing runs on the phone, so no ONNX either.

   **Built, November 2026, not yet tried on a phone.** The phone's half is
   `MobileMainViewModel.Vibe.cs`. The Playlists tab has a New Vibe Playlist
   row under New Smart Playlist while the server says `analysing` or
   `ready`. The row follows the server as it changes:
   `PeerSyncCoordinator` raises `PairingChanged` when `/info`'s
   `vibePlaylists` moves, and `MainViewModel` re-raises
   `CanMakeVibePlaylists`. The editor is `VibePlaylistEditorSheetView`, a
   pushed screen like the smart playlist editor's, over the same
   `VibePlaylistEditorViewModel`; the check saves it, and every other way
   off it cancels. A new one joins the library only when it is saved, and
   a save syncs at once (`PlaylistManagementViewModel.EditEnded`, on every
   head and for smart playlists too): the first phone-made one sat on the
   phone after its save, while the empty draft the editor had opened on
   had already synced to the Mac as an ordinary playlist. A song tapped on its screen,
   the header's Play (and Shuffle, as on the desktop), and the row menu's
   Play all start the endless queue. The row menu and the screen menu have
   no Shuffle, and their Edit opens the sentence.
5. **A desktop on its own.** The engine in-process, and the full settings tab
   on its own settings.
6. **A phone on its own.** This is the phase with the real risk, and it is
   decided once 1-5 are measured:
   - ONNX Runtime under NativeAOT on iOS (a static xcframework, `DirectPInvoke`),
     and on Android, including 16 KB pages;
   - what it adds to the app's size, so maybe a reduced ORT build with only
     the operators these two graphs use;
   - iOS's limits on background work: analysis only in the foreground, or
     with `BGProcessingTask` while charging.

## Later

- **Negative prompts.** Deferred by the owner. The idea: CLAP's text encoder
  embeds a fixed vocabulary of mood, genre and tempo phrases once, and the
  phrases furthest from a prompt act as negatives that push matching songs
  down. Typed negatives ("no vocals", "not metal") and skipped songs would
  feed the same mechanism.
- **Metadata in the score.** Matching title, album, genre and year words for
  the cultural prompts CLAP cannot do ("christmas").
- **Tempo and loudness by DSP.** BPM by onset autocorrelation, computed in the
  same pass from the same decoded audio, would fill `Track.BeatsPerMinute`
  (shown as a column today and empty everywhere). "Jogging" would then mean
  150-180 BPM as well as sounding energetic.
- **"More like this song"** comes nearly free: Endless Play's
  `SemanticAudioSearch.Similar` with one seed.
- **A smaller text encoder**, if the 251 MB matters on a phone on its own:
  an int8 export that holds the rankings (dynamic quantization, per tensor or
  per channel, with or without the embedding table, all lost about a quarter
  of the top 10), or int4 audio.

## Open questions

- **The default clip count: 6, settled by phase 0.** The first pass over
  16k songs takes about 8 hours on one thread of lambda, so overnight. The
  owner marked each clip setting's top 25 under the winning template (α 0.5;
  fits / misses, with unmarked songs in brackets):

  | Prompt | 3 clips | **6 clips** | 12 clips | Whole song |
  |---|---|---|---|---|
  | calm music for focusing at work | 22 / 0 (3) | **25 / 0** | 24 / 0 (1) | 25 / 0 |
  | music for a rainy day | 17 / 5 (3) | **17 / 8** | 17 / 8 | 17 / 8 |
  | music to go jogging | 9 / 11 (5) | **15 / 10** | 13 / 12 | 13 / 12 |

  More clips bought nothing: 12 and the whole song tie 6 or do slightly worse.
  3 clips lost a third of jogging's fits.
- **Activities.** CLAP knows sounds, not what people do to them: "music to go
  jogging" got 9 fits and 14 misses in phase 0, its top 13 including
  Nirvana's "Something In The Way". "Fast, upbeat, energetic music with a
  driving beat" put 8 songs the owner had marked as fitting and only one
  rejected song into its top 25. Two ways to bridge that without a language
  model: a short built-in table that turns common activities into how they
  sound (jogging, studying, a dinner, sleep, a party), applied to the prompt
  before encoding, or tempo from "Later" for the activities that are about
  pace. Neither is decided.
- **Whether "whole song" is worth offering at all.** It is 36 hours of
  analysis on lambda's one thread and 400 MB, and on the three prompts the
  owner marked it did no better than 6 clips (see the table above). It stays for now, since it is
  the setting for songs whose best part is in the middle, and the settings
  page says what it costs.
- **What a Vibe Playlist shows while the server is still analysing.** Its pool
  is built only from songs analysed so far. It grows as analysis continues,
  but only if the pool is rebuilt. The proposal is to rebuild the pool of every
  Vibe Playlist at most once an hour while analysis is under way, and once
  when it finishes.
