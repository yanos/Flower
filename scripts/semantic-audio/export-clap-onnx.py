#!/usr/bin/env python3
"""Export LAION's CLAP music checkpoint to the ONNX files Flower downloads.

docs/VIBE-PLAYLIST-PLAN.md, "The model files". Run by hand, never by the
build. Writes to --out (default ~/.cache/flower-semantic-audio/export):

  music_audioset_epoch_15_esc_90.14-audio-{fp32,fp16}.onnx               waveform [1,480000] 48 kHz mono -> [1,512]
  music_audioset_epoch_15_esc_90.14_reduce_range-audio-int8.onnx
  music_audioset_epoch_15_esc_90.14-text-{fp32,fp16}.onnx                input_ids, attention_mask [B,77] -> [B,512]
  music_audioset_epoch_15_esc_90.14_reduce_range-text-int8.onnx

The int8 files carry the name of the export that made them with reduce_range
(to_int8), music_audioset_epoch_15_esc_90.14_reduce_range, which is also the
release they go up as: a device knows its model by file name and size, and
the plain int8 export before it was the same size to the byte.
  vocab.json, merges.txt                          RoBERTa's tokenizer
  manifest.json                                   name, size and SHA-256 of every file

From LAION's own checkpoint (music_audioset_epoch_15_esc_90.14.pt) through
their laion_clap package, NOT from Hugging Face's laion/larger_clap_music:
that conversion is broken - its text embeddings all come out alike (cosine
0.999 between "angry heavy metal" and "christmas music") and it ranks every
prompt the same, found in phase 0 (November 2026). The original works.

The audio graph takes raw samples, because in laion_clap the spectrogram,
log-mel and batch norm are layers of the model (torchlibrosa), and so they
are exported with it: Flower reproduces no mel arithmetic at all. Embeddings
come out projected but NOT normalised; Flower normalises.
"""

import argparse
import hashlib
import json
import shutil
import urllib.request
from pathlib import Path

import numpy as np
import torch

NAME = "music_audioset_epoch_15_esc_90.14"
INT8_NAME = f"{NAME}_reduce_range"
CHECKPOINT = "music_audioset_epoch_15_esc_90.14.pt"
CHECKPOINT_URL = f"https://huggingface.co/lukewys/laion_clap/resolve/main/{CHECKPOINT}"
SAMPLES = 480_000
TOKENS = 77


class AudioEncoder(torch.nn.Module):
    def __init__(self, clap):
        super().__init__()
        self.clap = clap

    def forward(self, waveform):
        out = self.clap.audio_branch({"waveform": waveform}, mixup_lambda=None, device=waveform.device)
        return self.clap.audio_projection(out["embedding"])


class TextEncoder(torch.nn.Module):
    def __init__(self, clap):
        super().__init__()
        self.clap = clap

    def forward(self, input_ids, attention_mask):
        pooled = self.clap.text_branch(input_ids=input_ids, attention_mask=attention_mask)["pooler_output"]
        return self.clap.text_projection(pooled)


def load(checkpoint: Path):
    import laion_clap

    module = laion_clap.CLAP_Module(enable_fusion=False, amodel="HTSAT-base")
    module.load_ckpt(str(checkpoint), verbose=False)
    module.eval()
    return module


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def to_fp16(src: Path, dst: Path):
    import onnx
    from onnxruntime.transformers.float16 import convert_float_to_float16

    # onnxruntime's converter rather than onnxconverter-common's: the latter
    # retypes the outputs of Cast nodes already in the graph, and the result
    # fails to load.
    model = onnx.load(str(src))
    onnx.save(convert_float_to_float16(model, keep_io_types=True), str(dst))


def to_int8(src: Path, dst: Path):
    import onnx
    from onnxruntime.quantization import QuantType, quantize_dynamic

    # Weights only: the transformer layers (MatMul/Gemm), which are nearly all
    # of HTSAT, and the embedding tables (Gather) - RoBERTa's 50k x 768 word
    # table alone is 154 MB at fp32, so leaving it out leaves the text encoder
    # at twice the size it should be. Convolutions stay fp32.
    #
    # The audio front end stays fp32 too, and has to: the log-mel filterbank
    # is a MatMul, and quantizing it quantizes the power spectrum going in -
    # a range of many orders of magnitude, which the log right after it then
    # blows up. With it, int8 audio embeddings came out at cosine 0.19
    # against fp32; without it, 0.9995.
    #
    # reduce_range: weights in 7 bits rather than 8. On an x64 CPU without
    # VNNI ONNX Runtime multiplies uint8 activations by int8 weights with
    # VPMADDUBSW, which adds each pair of products into 16 bits and saturates
    # there - 2 x 255 x 127 overflows, 2 x 255 x 63 does not. Measured
    # (November 2026) on 128 clips of the library on an i5-8259U: plain int8
    # came out at cosine 0.939 mean and 0.807 worst against fp32, reduce_range
    # at 0.99927 and 0.99781. On a Mac, where nothing saturates, it costs
    # 0.99955 -> 0.99929 and no measurable ranking (top 10/25/50 of 5921
    # clips against 28 prompts within a song or two of plain int8's), and the
    # same speed; on the i5 4% more time per clip. uint8 weights fixed it
    # too, at 0.99956, but ran no faster than fp32 there.
    front_end = [n.name for n in onnx.load(str(src), load_external_data=False).graph.node
                 if "spectrogram_extractor" in n.name or "logmel_extractor" in n.name]
    quantize_dynamic(str(src), str(dst), weight_type=QuantType.QInt8,
                     op_types_to_quantize=["MatMul", "Gemm", "Gather"], per_channel=True,
                     reduce_range=True, nodes_to_exclude=front_end)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("~/.cache/flower-semantic-audio/export").expanduser())
    ap.add_argument("--checkpoint", type=Path, default=Path(f"~/.cache/flower-semantic-audio/ckpt/{CHECKPOINT}").expanduser())
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    if not args.checkpoint.exists():
        args.checkpoint.parent.mkdir(parents=True, exist_ok=True)
        print(f"downloading {CHECKPOINT_URL}")
        urllib.request.urlretrieve(CHECKPOINT_URL, args.checkpoint)

    module = load(args.checkpoint)
    clap = module.model

    audio = args.out / f"{NAME}-audio-fp32.onnx"
    text = args.out / f"{NAME}-text-fp32.onnx"
    with torch.no_grad():
        # One clip at a time, with no batch axis: HTSAT's window reshapes
        # trace the batch size into the graph as a constant, so a graph
        # traced at 2 and declared dynamic gave cosine 0.18 against PyTorch
        # for a single clip - silently. Flower's analyzer encodes one clip at
        # a time anyway.
        waveform = torch.from_numpy(np.random.default_rng(0).uniform(-0.5, 0.5, (1, SAMPLES)).astype(np.float32))
        torch.onnx.export(AudioEncoder(clap), (waveform,), str(audio),
                          input_names=["waveform"], output_names=["audio_embeds"],
                          opset_version=17, dynamo=False)
        tokens = module.tokenizer(["music for a rainy day", "jazz"])
        torch.onnx.export(TextEncoder(clap), (tokens["input_ids"], tokens["attention_mask"]), str(text),
                          input_names=["input_ids", "attention_mask"], output_names=["text_embeds"],
                          dynamic_axes={"input_ids": {0: "batch"}, "attention_mask": {0: "batch"},
                                        "text_embeds": {0: "batch"}},
                          opset_version=17, dynamo=False)

    for encoder in (audio, text):
        to_fp16(encoder, encoder.with_name(encoder.name.replace("fp32", "fp16")))
        to_int8(encoder, encoder.with_name(encoder.name.replace(NAME, INT8_NAME).replace("fp32", "int8")))

    # The tokenizer laion_clap uses is roberta-base's.
    tokenizer = module.tokenize
    saved = Path(tokenizer.save_pretrained(str(args.out / "tokenizer"))[0]).parent
    for name in ("vocab.json", "merges.txt"):
        shutil.copyfile(saved / name, args.out / name)
    shutil.rmtree(saved)

    files = sorted(p for p in args.out.iterdir() if p.is_file() and p.name != "manifest.json")
    manifest = {
        "model": NAME,
        "checkpoint": CHECKPOINT,
        "checkpoint_sha256": sha256(args.checkpoint),
        "audio": {"input": "waveform", "sample_rate": 48000, "samples": SAMPLES, "channels": 1},
        "text": {"tokenizer": "roberta-base", "tokens": TOKENS, "pad_id": 1},
        "files": [{"name": p.name, "size": p.stat().st_size, "sha256": sha256(p)} for p in files],
    }
    (args.out / "manifest.json").write_text(json.dumps(manifest, indent=2))
    for f in manifest["files"]:
        print(f"{f['size'] / 1e6:9.1f} MB  {f['name']}")


if __name__ == "__main__":
    main()
