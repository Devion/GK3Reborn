"""Speaks the lines the crow's-nest puzzle calls for, through a running ComfyUI.

    D:/AI/ComfyUI/python_embeded/python.exe tools/audio/make_restored_voice.py \
        --workspace D:/Dev/GK3Reborn/ContentWorkspace [--only PLATE ...] [--seed N]

Reads ``manifests/restored-voice.json`` — written by ``plan_restored_voice.py`` — and posts
the pinned graph in ``comfy/workflows/voice_chatterbox.api.json`` once per line. What comes
back is written into ``enhanced/audio/dialogue`` under the exact 1999 asset name, which is
where ``pack-content`` picks audio up from and what puts it in the ReBarn volume. Nothing
goes to ``overrides/``: that is a player's own directory and is not packed.

Through ComfyUI rather than around it, like every other generative pass in this project:
the graph is pinned and reviewable, it is the same server and the same weights the rest of
the content came through, and a person can open it, change the exaggeration and run it again
without touching this file. See ``PbrLab/make_basecolour.py``, which this is shaped after.

The voice is Gabriel's own recordings. The reference is chosen by the planner from the 1,050
lines whose YAK names him as the speaker, and staged into ComfyUI's input directory.

**A synthesised line is not a restored one.** Thirteen of these say words the game's own
captions preserved; five say words we wrote. Both are new audio in a voice that was
performed by somebody, and the manifest records which is which so that nothing here can
quietly be filed as a recovered recording.
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
WORKFLOW = os.path.join(HERE, "comfy", "workflows", "voice_chatterbox.api.json")
DEFAULT_URL = "http://127.0.0.1:8188"


def post(url, path, graph):
    body = json.dumps({"prompt": graph}).encode("utf-8")
    request = urllib.request.Request(
        f"{url}/prompt", data=body, headers={"Content-Type": "application/json"})

    try:
        with urllib.request.urlopen(request, timeout=60) as answer:
            return json.load(answer)["prompt_id"]
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", "replace")[:900]
        raise SystemExit(f"ComfyUI refused the graph for {path}:\n{detail}") from error
    except urllib.error.URLError as error:
        raise SystemExit(
            f"no ComfyUI at {url} ({error}). Start it with run_nvidia_gpu.bat.") from error


def wait(url, prompt_id, patience):
    """Polls the history until the prompt is done, and hands back what it wrote."""
    deadline = time.time() + patience

    while time.time() < deadline:
        with urllib.request.urlopen(f"{url}/history/{prompt_id}", timeout=30) as answer:
            history = json.load(answer)

        if prompt_id in history:
            done = history[prompt_id]
            status = done.get("status", {})

            if status.get("status_str") == "error":
                raise SystemExit(f"ComfyUI failed the graph: {json.dumps(status)[:900]}")

            return done.get("outputs", {})

        time.sleep(2)

    raise SystemExit(f"ComfyUI did not finish within {patience:.0f}s.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--comfy-url", default=DEFAULT_URL)
    parser.add_argument("--comfy-output", default="D:/AI/ComfyUI/ComfyUI/output")
    parser.add_argument("--only", nargs="*", default=None)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--patience", type=float, default=600)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    manifest = os.path.join(args.workspace, "manifests", "restored-voice.json")

    with open(manifest, encoding="utf-8") as handle:
        plan = json.load(handle)

    out = os.path.join(args.workspace, "enhanced", "audio", "dialogue")
    os.makedirs(out, exist_ok=True)

    with open(WORKFLOW, encoding="utf-8") as handle:
        pinned = json.load(handle)

    wanted = [
        line for line in plan["lines"]
        if not line["recorded"]
        and line["text"]
        and (args.only is None or line["plate"] in set(args.only))
    ]

    if not wanted:
        print("nothing to say")
        return

    print(f"{len(wanted)} line(s), reference {plan['reference'][0]['plate']}")

    for line in wanted:
        target = os.path.join(out, line["asset"] + ".wav")

        print(f"  {line['asset']:16s} [{line['source']:7s}] {line['text'][:58]}")

        if args.dry_run:
            continue

        graph = json.loads(json.dumps(pinned))
        graph["3"]["inputs"]["text"] = line["text"]

        # One seed per line rather than one for the run, so re-running a single line that
        # came out badly does not change the others. CRC rather than hash(): Python
        # randomises string hashing per process, which would have made a pinned graph
        # produce a different reading of the same line every run.
        graph["3"]["inputs"]["seed"] = args.seed + (
            zlib.crc32(line["plate"].encode("ascii")) % 100000)
        # Written straight where it belongs. SaveAudioAdvanced offers flac, mp3 and opus
        # and no WAV at all, and a lossy intermediate for a line that is already synthetic
        # is a second generation for nothing.
        graph["4"]["inputs"]["folder_path"] = os.path.abspath(
            os.path.join(args.workspace, "enhanced", "audio"))
        graph["4"]["inputs"]["filename"] = line["asset"]

        wait(args.comfy_url, post(args.comfy_url, line["plate"], graph), args.patience)

        if not os.path.exists(target):
            raise SystemExit(f"ComfyUI finished and {target} is not there.")

        print(f"    -> {target} ({os.path.getsize(target) / 1024:.0f} KB)")

    print(f"\nwrote into {out}")
    print("these are packed by: GK3Reborn.Tools pack-content --workspace <ws>")


main()
