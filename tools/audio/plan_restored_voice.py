"""Works out what has to be spoken, and what to speak it in.

    python tools/audio/plan_restored_voice.py D:/Dev/GK3Reborn/ContentWorkspace

Writes ``manifests/restored-voice.json``: one entry per line the cut crow's-nest
restoration calls for and the archives do not have, with the words it says, where those
words came from, who says it, and the reference recordings a clone should be conditioned
on.

Nothing here makes audio. This is the part that has to be right before anything does.

Where the words come from
-------------------------

Eighteen of the puzzle's nineteen recordings were deleted. Their ``.YAK`` wrappers were
not: each still carries a ``CAPTION`` and simply names no sound. So most of these lines
have their exact wording, written by whoever wrote the rest of the game.

Five do not, and they are marked ``written: true``. Those are ours, and a line that is ours
should never be filed as one of theirs -- see docs/cut-content.md.

The reference
-------------

A clone needs a sample of the voice, and of the right voice: one of these lines is Grace's.
So there is a reference set per character who speaks here, taken from the archives' own
lines whose YAK names them -- 1,050 of them are Gabriel's -- and nothing else. They are
picked long, because a longer reference clones better, and from the same day and the same
part of town where that is possible: a reference from a scene where he is shouting gives
eighteen lines of a man shouting about a bird's nest.
"""

import json
import os
import re
import struct
import sys

# The nineteen the puzzle calls for, and the six the room says about the same objects at
# any hour of day one. The one that survived is here too, marked, because it is the
# control: whatever is generated can be compared against a real recording of a line from
# the same scene, in the same voice, saying words from the same page.
#
# The last field is who says it. Almost every YAK in the game names its speaker as UNKNOWN,
# so the speaker is taken from the rule instead: a plate played under GABE_ALL is his and
# one played under GRACE_ALL is hers. Across the corpus that is decisive where it can be
# measured -- 583 of the plates ending QS1 are played under GRACE_ALL and none under
# GABE_ALL, and 174 of the PF1s are his -- and the six new lines here are named by their
# own rules in RC2_1ALL.NVC. The three the bird's nest says carry cases about the black
# fibres rather than a character, but they belong to a puzzle only Gabriel can do.
LINES = [
    ("13952441O1", "BIRDS_NEST", "LOOK", "GABRIEL"),
    ("1395232CW1", "BIRDS_NEST", "PICKUP", "GABRIEL"),
    ("13952321O1", "BIRDS_NEST", "PICKUP", "GABRIEL"),
    ("139520LCW1", "BIRDS_NEST", "THINK", "GABRIEL"),
    ("1395257OD1", "BIRDS_NEST", "HOSE", "GABRIEL"),
    ("1395257L91", "BIRDS_NEST", "HOSE", "GABRIEL"),
    ("1395257SU1", "BIRDS_NEST", "HOSE", "GABRIEL"),
    ("139D644PF1", "BIRDS_NEST_ON_GRND", "LOOK", "GABRIEL"),
    ("139D632291", "BIRDS_NEST_ON_GRND", "PICKUP", "GABRIEL"),
    ("139CP445O1", "CROW_AT_NEST", "LOOK", "GABRIEL"),
    ("1395D3B411", "GARDEN_HOSE", "SPRAY_GUN", "GABRIEL"),
    ("1395D44OD1", "GARDEN_HOSE", "LOOK", "GABRIEL"),
    ("1395D44SU1", "GARDEN_HOSE", "LOOK", "GABRIEL"),
    ("1395D0L1X1", "GARDEN_HOSE", "THINK", "GABRIEL"),
    ("1395D0LCW1", "GARDEN_HOSE", "THINK", "GABRIEL"),
    ("1394D1TPF1", "WATER_INTERFACE", "AIM", "GABRIEL"),
    ("1394D1TFS1", "WATER_INTERFACE", "AIM", "GABRIEL"),
    ("1394D22PF1", "WATER_INTERFACE", "EXIT", "GABRIEL"),
    ("1396L3W4B1", "SCENE", "ENTER", "GABRIEL"),

    # RC2_1ALL.NVC: what the two objects say whenever anybody is in the square on day one,
    # as against RC2102P.NVC's puzzle. These were missed the first time round -- the plan
    # was written from the puzzle's own action file and stopped there -- so the nest and
    # the rug were the two things in the restoration that had a caption and no voice.
    ("1355244CS1", "BIRDS_NEST", "LOOK", "GABRIEL"),
    ("1355244031", "BIRDS_NEST", "LOOK", "GABRIEL"),
    ("1355232031", "BIRDS_NEST", "PICKUP", "GABRIEL"),
    ("1351L44PF1", "BLACK_RUG_IN_WINDOW", "LOOK", "GABRIEL"),
    ("1351L44QS1", "BLACK_RUG_IN_WINDOW", "LOOK", "GRACE"),
    ("1351L32PF1", "BLACK_RUG_IN_WINDOW", "PICKUP", "GABRIEL"),
]

# Words for the five whose captions did not survive either. Written here rather than in the
# generator so that what was invented is visible in one place and reviewable as prose.
WRITTEN = {
    "1394D1TPF1": "Steady . . . come on, hold it right there.",
    "1394D1TFS1": "That's got it!",
    "1394D22PF1": "Okay, enough of that.",
    "139D632291": "Black fibers. Just what the doctor ordered.",
    "1396L3W4B1": "Somethin' up in that tree's been busy.",
}


def asset(code):
    """The file a ten-character voice-over plate is stored under."""
    return f"A{code[:7]}.{code[7:]}"


def yaks(raw):
    found = {}

    for root, _, files in os.walk(raw):
        for name in files:
            upper = name.upper()

            if upper.startswith("E") and upper.endswith(".YAK"):
                found[upper[1:-4]] = os.path.join(root, name)

    return found


def caption(path):
    text = open(path, "rb").read().decode("latin-1")
    found = re.search(r"CAPTION,(.*)", text)

    return found.group(1).strip() if found else None


def seconds(path):
    """A normalised clip's length, from its RIFF header. 16-bit PCM, per the pipeline."""
    with open(path, "rb") as handle:
        head = handle.read(44)

    if len(head) < 44 or head[:4] != b"RIFF":
        return 0.0

    rate = struct.unpack_from("<I", head, 24)[0]
    bytes_a_second = struct.unpack_from("<I", head, 28)[0]
    size = os.path.getsize(path) - 44

    return size / bytes_a_second if bytes_a_second else (size / (rate * 2))


def voice(speaker, raw, normalized, want, prefer):
    """The longest of one character's own recordings, preferring a location code."""
    spoken = []

    for code, path in yaks(raw).items():
        text = open(path, "rb").read().decode("latin-1")

        if f"SPEAKER,{speaker}" not in text.upper():
            continue

        wav = os.path.join(normalized, asset(code) + ".wav")

        if not os.path.exists(wav):
            continue

        length = seconds(wav)

        # Filtered here rather than after the sort below: taking the longest eight and
        # then rejecting anything over twelve seconds leaves nothing at all, which is
        # what it did.
        if 2.0 <= length <= 12.0:
            spoken.append((code, wav, length))

    # Long first, and from the same part of town first of all: a reference wants him
    # talking normally, and the longest lines in a location are the ones where he is.
    spoken.sort(key=lambda s: (s[0][:3] != prefer, -s[2]))

    return [
        {"plate": code, "file": os.path.relpath(wav, normalized).replace("\\", "/"),
         "seconds": round(length, 2)}
        for code, wav, length in spoken[:want]
    ]


def main():
    workspace = sys.argv[1]
    raw = os.path.join(workspace, "raw")
    normalized = os.path.join(workspace, "normalized", "audio", "dialogue")

    have = yaks(raw)
    entries = []

    for code, noun, verb, speaker in LINES:
        wrapper = have.get(code)
        spoken = caption(wrapper) if wrapper else None
        recorded = os.path.exists(os.path.join(normalized, asset(code) + ".wav"))

        entries.append({
            "plate": code,
            "asset": asset(code),
            "noun": noun,
            "verb": verb,
            "speaker": speaker,
            "text": spoken or WRITTEN.get(code),
            "source": "caption" if spoken else "written",
            "written": spoken is None,
            "recorded": recorded,
            # Whether the game still has the .YAK that names the line. Four of these have
            # neither recording nor wrapper -- they were cut before either was made -- and
            # a line with no wrapper cannot be spoken at all, because the wrapper is what
            # the engine reads and what names the sound. The generator writes one.
            "wrapper": wrapper is not None,
        })

    missing = [e for e in entries if not e["recorded"]]
    invented = [e for e in missing if e["written"]]
    unknown = [e for e in missing if e["text"] is None]

    # One reference set per character who has a line here, because a clone conditioned on
    # Gabriel saying Grace's line gives Gabriel saying Grace's line. The file name is what
    # the generator stages into ComfyUI's input directory and what the pinned graph loads.
    voices = {
        speaker: {
            "input": f"GK3_{speaker}_REF.wav",
            "reference": voice(speaker, raw, normalized, want=8, prefer="139"),
        }
        for speaker in dict.fromkeys(line[3] for line in LINES)
    }

    manifest = {
        "schemaVersion": 2,
        "about": "Lines the cut crow's-nest puzzle calls for and the archives do not have.",
        "voices": voices,
        "lines": entries,
    }

    out = os.path.join(workspace, "manifests", "restored-voice.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)

    with open(out, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=1)
        handle.write("\n")

    print(f"{len(entries)} lines the restoration calls for")
    print(f"  {len(entries) - len(missing)} still recorded")
    print(f"  {len(missing) - len(invented)} to synthesise from their own captions")
    print(f"  {len(invented)} to synthesise from words we wrote")

    if unknown:
        print(f"  {len(unknown)} with no words at all: "
              + ", ".join(e['plate'] for e in unknown))

    for speaker, chosen in voices.items():
        total = sum(r["seconds"] for r in chosen["reference"])
        print(f"reference {speaker}: {len(chosen['reference'])} of their own lines, "
              f"{total:.1f}s, staged as {chosen['input']}")

    print(f"wrote {out}")


main()
