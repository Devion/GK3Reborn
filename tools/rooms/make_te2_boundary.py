"""Publishes the hand-written room assets and TE2's walk boundary into a content workspace.

    python tools/rooms/make_te2_boundary.py D:/Dev/GK3Reborn/ContentWorkspace

A walk boundary is an 8-bit indexed bitmap: index 0-7 is open floor, ascending away from
the walls as a gradient the pathfinder uses to keep actors from scraping along them, and
8, 9 and 255 are wall. See GK3Reborn.Game.Navigation.WalkBoundary.

The boundary is written rather than painted because TE2's floor plan is known exactly -- it
is the blockout tools/blender/build_te2.py builds -- and a boundary that disagrees with the
geometry is the sort of fault that reads as "the room is haunted" rather than as a bad
bitmap.

The scene and action files beside it in te2/ are hand-written and are the room's source;
they are copied rather than generated. Everything lands in enhanced/rooms, beside the
geometry, because that is what it is: content. Nothing of a cut room lives in the engine.
"""

import os
import shutil
import struct
import sys

W = H = 128
HALF_X, HALF_Z = 700.0, 690.0
SIZE_X, SIZE_Z = HALF_X * 2, HALF_Z * 2

# What stands on the floor, as world-space rectangles the actor cannot walk through.
BLOCKS = [
    (-130, -130, 130, 130),          # te2_centerwalls, the block in the middle
    (-505, 155, -205, 455),          # water nook
    (205, 155, 505, 455),            # fire nook
    (-505, -455, -205, -155),        # air nook
    (205, -455, 505, -155),          # earth nook
    (-505, 240, -355, 360),          # rockformations
    (-85, 475, 85, 645),             # the elevator
]

WALL_MARGIN = 70.0


def world(x, y):
    u = (x + 0.5) / W
    v = 1.0 - ((y + 0.5) / H)
    return u * SIZE_X - HALF_X, v * SIZE_Z - HALF_Z


def region(wx, wz):
    inside = min(HALF_X - abs(wx), HALF_Z - abs(wz))

    if inside <= WALL_MARGIN:
        return 255

    for x0, z0, x1, z1 in BLOCKS:
        if x0 <= wx <= x1 and z0 <= wz <= z1:
            return 255

    # The gradient: 7 hard against something, falling to 0 out in the open. The pathfinder
    # reads it to keep an actor off the walls rather than through them.
    clear = inside - WALL_MARGIN

    for x0, z0, x1, z1 in BLOCKS:
        dx = max(x0 - wx, 0.0, wx - x1)
        dz = max(z0 - wz, 0.0, wz - z1)
        clear = min(clear, max(dx, dz))

    return max(0, min(7, 7 - int(clear / 22.0)))


def main():
    workspace = sys.argv[1]
    rooms = os.path.join(workspace, "enhanced", "rooms")
    os.makedirs(rooms, exist_ok=True)

    here = os.path.dirname(os.path.abspath(__file__))

    for folder in ("te2", "rc2"):
        source = os.path.join(here, folder)

        if not os.path.isdir(source):
            continue

        for name in sorted(os.listdir(source)):
            shutil.copyfile(os.path.join(source, name), os.path.join(rooms, name))
            print(f"{os.path.join(rooms, name)}: copied")

    out = os.path.join(rooms, "TE2WLKBNDS.BMP")
    indices = bytes(region(*world(x, y)) for y in range(H) for x in range(W))

    # An 8-bit BMP: rows are bottom-up and padded to four bytes, which 128 already is.
    palette = b"".join(struct.pack("<BBBB", i, i, i, 0) for i in range(256))
    rows = b"".join(indices[(y * W):(y * W) + W] for y in range(H - 1, -1, -1))
    offset = 14 + 40 + len(palette)

    header = struct.pack("<2sIHHI", b"BM", offset + len(rows), 0, 0, offset)
    info = struct.pack("<IiiHHIIiiII", 40, W, H, 1, 8, 0, len(rows), 2835, 2835, 256, 256)

    with open(out, "wb") as handle:
        handle.write(header + info + palette + rows)

    open_texels = sum(1 for i in indices if i < 8)
    print(f"{out}: {W}x{H}, {open_texels} of {W * H} texels open")


main()
