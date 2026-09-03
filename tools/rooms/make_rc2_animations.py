"""Writes the two clips the crow's-nest puzzle ends with.

    python tools/rooms/make_rc2_animations.py D:/Dev/GK3Reborn/ContentWorkspace

The nest comes down and the crow leaves. RC2's own clips for this did not survive -- the
puzzle was cut before release -- so these are made, and they are made in the game's own
format rather than in a new one.

Why this is not as ambitious as it sounds
-----------------------------------------

A ``.ACT`` stores, per frame and per mesh group, either where its vertices are or where the
group sits. Two thousand one hundred and eighty-eight of the game's clips -- 37.8% of them
-- are the second kind only: transforms, no vertex data at all. A nest falling out of a tree
and a bird flying off are rigid motion and nothing else, so they are that kind, and writing
one means writing a 4x3 matrix per frame.

The format is `GK3Reborn/docs/formats/vertex-animation.md`, and the engine's reader checks
five invariants while reading. That is the test: if `act-info` reads these back and reports
them as rigid, the bytes are right.

    GK3Reborn.Tools act-info --source <GK3>/Data

Fifteen frames a second, which is the rate the format plays at.
"""

import math
import os
import struct
import sys

FPS = 15


def act(model, meshes, frames):
    """One vertex animation, transforms only.

    ``frames`` is a list of frames; each frame is a list of one 4x3 basis-and-position per
    mesh, or None for a mesh that has not moved -- which is how the format says "hold the
    last pose" and is why a clip is small.
    """
    # Per frame, per mesh: uint16 index, uint32 budget, then blocks. A transform block is a
    # 1-byte kind, a 4-byte size and 48 bytes of payload.
    bodies = []

    for frame in frames:
        body = b""

        for mesh in range(meshes):
            pose = frame[mesh] if mesh < len(frame) else None

            if pose is None:
                body += struct.pack("<HI", mesh, 0)
                continue

            payload = struct.pack("<12f", *pose)
            body += struct.pack("<HI", mesh, 1 + 4 + len(payload))
            body += struct.pack("<BI", 2, len(payload)) + payload

        bodies.append(body)

    # The header is 52 bytes, then one absolute offset per frame, then the frames.
    start = 52 + (4 * len(frames))
    offsets = []
    at = start

    for body in bodies:
        offsets.append(at)
        at += len(body)

    payload = b"".join(bodies)
    name = model.encode("latin-1")[:31].ljust(32, b"\0")

    return (b"HTCA"
            + struct.pack("<IIII", 258, len(frames), meshes, len(payload))
            + name
            + b"".join(struct.pack("<I", o) for o in offsets)
            + payload)


def pose(x, y, z, spin=0.0):
    """A mesh transform: three basis vectors then a position.

    The basis is left-handed, as GK3's are -- see the format notes. A spin about Y is all
    either of these clips needs.
    """
    c = math.cos(math.radians(spin))
    s = math.sin(math.radians(spin))

    return (c, 0.0, -s,
            0.0, 1.0, 0.0,
            s, 0.0, c,
            x, y, z)


def nest_falls():
    """Twenty-two frames of a nest leaving a tree.

    It is knocked sideways before it drops, because water pushed it, and it turns as it
    goes. The last four frames are it settled: a clip that ends mid-fall leaves the prop
    hanging in the air until something else moves it.
    """
    frames = []
    fall = 18

    for i in range(fall):
        t = i / (fall - 1)

        # Gravity, roughly: distance goes as the square of the time.
        frames.append([pose(t * 14.0, -t * t * 138.0, t * 6.0, t * 190.0)])

    for _ in range(4):
        frames.append([None])

    return frames


def crow_leaves():
    """Sixteen frames of a bird deciding it has had enough.

    Up and away, with the rise flattening out -- it is climbing away from the tree, not
    launching. Two frames of nothing at the start so the water visibly hits before it goes.
    """
    frames = [[pose(0.0, 0.0, 0.0)], [None]]

    for i in range(14):
        t = (i + 1) / 14

        frames.append([pose(
            -t * 120.0,
            math.sin(t * math.pi * 0.5) * 96.0,
            -t * 210.0,
            -t * 44.0)])

    return frames


def anm(action, frames):
    """The text file that plays a clip. See docs/formats/animations.md."""
    return (
        "// Written by tools/rooms/make_rc2_animations.py. See docs/cut-content.md.\r\n"
        "[HEADER]\r\n"
        f"{frames}\r\n"
        "\r\n"
        "[ACTIONS]\r\n"
        # The count first: the reader takes a section's first line as one and starts at
        # the second. Without it the only action line is eaten as the count and the clip
        # plays nothing -- which is not an error anywhere, just a prop that never moves.
        "1\r\n"
        # No placement numbers: the clip is relative, and a prop already stands where the
        # scene put it. Eight zeros here would mean "absolute at the origin" and drop the
        # nest through the floor at the middle of the world.
        f"0,{action}\r\n")


def main():
    rooms = os.path.join(sys.argv[1], "enhanced", "rooms")
    os.makedirs(rooms, exist_ok=True)

    made = [
        ("RC2NESTFALL", "rc2_birdsnest", nest_falls()),
        ("RC2CROWFLEE", "rc2_crow", crow_leaves()),
    ]

    for clip, model, frames in made:
        # The corpus names a clip <model>_<animation>, and the header names the model. The
        # reader pairs by the header, not the file name.
        action = f"{model}_{clip}"

        with open(os.path.join(rooms, action.upper() + ".ACT"), "wb") as handle:
            handle.write(act(model, 1, frames))

        with open(os.path.join(rooms, clip + ".ANM"), "w", encoding="latin-1", newline="") as handle:
            handle.write(anm(action, len(frames)))

        print(f"{clip}: {len(frames)} frames of {model}, "
              f"{len(frames) / FPS:.1f}s -> {action.upper()}.ACT and {clip}.ANM")


main()
