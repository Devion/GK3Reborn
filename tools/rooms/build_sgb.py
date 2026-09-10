"""Builds St. George's Books, the shop behind the bookstore door that never opens.

    python tools/rooms/build_sgb.py --workspace D:/Dev/GK3Reborn/ContentWorkspace [--dry-run]

Writes, into the workspace's ``enhanced`` tree:

    rooms/Sgb.glb           the room, which RoomLibrary turns into a scene
    rooms/SGBWLKBNDS.BMP    where Gabriel may walk in it
    models/sgbcambnds.glb   the shell that fences the camera in
    rooms/SGB.SIF, SGB.SCN, SGB.STK, SGB_ALL.NVC, RC1_ALL_SGB.NVC
                            copied from tools/rooms/sgb, which is their source
    audio/music/SGBTHEME.WAV.wav
                            the shop's music, from --music (an MP3), wrapped the way the
                            game's own sounds are: an MP3 stream inside a RIFF header with
                            format tag 85, which WavFile decodes in process

What this is
------------

Rennes-le-Chateau's bookshop, "Atelier Empreinte Librairie", is closed for repairs for the
whole of GK3: every OPEN on its door is "They're closed." Try it five times in a row as
Gabriel and the door gives -- onto St. George's Books, his own shop in New Orleans from the
first game, as it is remembered rather than as it was: two storeys of shelves under an iron
gallery, tall arched windows down one side, his desk in the middle of the floor and Grace's
under the painting by the brick wall. A dream, and he says so on the way in.

It is an easter egg and it is new art. Nothing about it is cut content: nobody at Sierra
wrote or recorded it. Every surface is one of the game's own textures -- the Chateau de
Serres' bookcases, Larry Chester's floorboards and desk, the hotel's windows, doors and rug,
the lobby's stair rail -- so a player with the enhanced packs sees the same upscaled
pictures here as in the rooms they came from. The lines are the game's own too, chosen
from ones the story never reaches; see SGB_ALL.NVC.

The frame is the engine's: Y up, one unit about an inch, Gabriel a little over seventy
tall. Object names are what SGB.SIF binds nouns to, so they are the contract with it.
"""

import argparse
import os
import shutil
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from glbwriter import Glb  # noqa: E402


# ---------------------------------------------------------------------------- the room ---
#
# X runs left to right as the default camera sees it, Z from the camera (front, negative)
# toward the entrance door in the far wall (back, positive). The gallery is the mezzanine
# that runs round the room at GALLERY; the camera stands on the front stretch of it.
HALF_X = 290.0
HALF_Z = 380.0
FLOOR = 0.0
GALLERY = 165.0          # top of the mezzanine floor
GALLERY_DEPTH = 100.0    # how far the mezzanine reaches in from the walls
GALLERY_THICK = 12.0
CEILING = 340.0
RAIL = 40.0              # the gallery railing's height

# Textures. Each is one of the game's, named as the archives spell it.
FLOOR_TEX = "LHIFLOOR_WOOD"
CEILING_TEX = "JANCEILING"
WALL_TEX = "LHIWALL"
WALL_TEX_WORN = "LHIWALL2"
BRICK_TEX = "RL2BRICK"
WINDOW_TEX = "27WINDOW"
DOOR_TEX = "21DOORFRM"
BACK_DOOR_TEX = "LHIDOOR"
CASE_TEX = ("CS2BOOKCASE", "CS2BOOKCASE02", "CS2BOOKCASE03")
SHELF_TEX = ("LHILAWYERBOOKS", "LHILAWYERBOOKS2")
WOOD_TEX = "27WOOD"
BEAM_TEX = "WOODBEAM"
PLANK_TEX = "WOODTILE"
POST_TEX = "COLUMNWOOD"
RAIL_TEX = "CS3STAIRRAIL"
STAIR_RAIL_TEX = "LBYSTRRAIL"
MOLDING_TEX = "MOLDING"
RUG_TEX = "21RUG"
DESK_TOP = "LHIDESKTOP"
DESK_FRONT = "LBYDESKB"
DESK_SIDE = "LHIDESKSIDES"
DESK2_TOP = "21DESKTOP"
DESK2_DRAWER = "21DESKDRWR"
CHAIR_TEX = "R27CHAIRWOOD"
CHAIR_BACK = "LHICHAIRBACK"
CHAIR_ARM = "LHICHAIRARM"
PAINTING_TEX = "21PAINTING1"
CURTAIN_TEX = "LBYCURTAINS1A"
BRASS_TEX = "21LAMPSTAND"
SHADE_LIT = "LAMPSHADE_LIT"
GLOW_TEX = "LHILAMPGLOW"
SCONCE_TEX = "HOTELHLSCONCE"
GREEN_TEX = "NUWOOD"
CUP_TEX = "COFFEECUP1"
BOOK_COVER = "SOHGBOOK"
BOOK_SPINES = ("LHIBOOK01SP", "LHIBOOK02SP", "LHIBOOK03SP", "LHIBOOK04SP")
BOOK_TOP = "LHIBOOK01TP"
BOOK_SIDE = "LHIBOOK01SD"
LEDGER_TEX = "LBYREGBOOK"
PAPER_TEX = "R23PAPER01"


def build_room():
    global _current
    g = Glb()
    _current = g

    # --- the shell ---------------------------------------------------------------------
    # One box wound inward: the floor, the ceiling and four walls, each with its own
    # picture. The right wall is brick; the rest is the plaster-and-dado of Larry's house,
    # with the dado's bottom set on the floor.
    g.box("sgb_floor", FLOOR_TEX, (-HALF_X, FLOOR - 1, -HALF_Z), (HALF_X, FLOOR, HALF_Z),
          faces="+y", tile=(160, 160), inward=False)

    g.box("sgb_ceiling", CEILING_TEX, (-HALF_X, CEILING, -HALF_Z), (HALF_X, CEILING + 1, HALF_Z),
          faces="-y", tile=(96, 96))

    # Walls in two storeys so the dado repeats at the gallery floor: 256 texels of plaster
    # over 165 units, then again above.
    for (lo, hi) in ((FLOOR, GALLERY), (GALLERY, CEILING)):
        g.box("sgb_walls", WALL_TEX, (-HALF_X, lo, -HALF_Z), (HALF_X, hi, HALF_Z),
              faces="-x +z -z", tile=(64, hi - lo), origin="face", inward=True)

    g.box("sgb_brickwall", BRICK_TEX, (HALF_X, FLOOR, -HALF_Z - 1), (HALF_X + 1, CEILING, HALF_Z + 1),
          faces="-x", tile=(96, 96))

    # Cornice under the ceiling, all round.
    cornice = 18.0
    g.box("sgb_cornice", MOLDING_TEX, (-HALF_X, CEILING - cornice, -HALF_Z), (HALF_X, CEILING, HALF_Z),
          faces="-x +x +z -z", tile=(96, cornice), origin="face", inward=True)

    # Ceiling beams, front to back.
    for x in (-200, -100, 0, 100, 200):
        g.box("sgb_beams", BEAM_TEX, (x - 8, CEILING - 14, -HALF_Z), (x + 8, CEILING, HALF_Z),
              tile=(64, 256), faces="-y +x -x")

    # --- the windows, left wall -------------------------------------------------------
    # Two tall arched windows through both storeys, the glass drawn as painted (unlit) so
    # daylight comes from it rather than falling on it. The object names say "window" so
    # the engine moves the room's sun to them and hangs its shafts there.
    for i, z in enumerate((-150.0, 110.0)):
        name = f"sgb_window{i + 1:02d}"
        w, y0, y1 = 120.0, 45.0, 250.0
        arch = 50.0
        x = -HALF_X + 0.6

        # Pane: a quad in the wall plane facing into the room (+x).
        g.quad(name, WINDOW_TEX,
               [(x, y0, z + w / 2), (x, y0, z - w / 2), (x, y1, z - w / 2), (x, y1, z + w / 2)],
               [(0.0, 2.0), (1.0, 2.0), (1.0, 0.0), (0.0, 0.0)], unlit=True)

        # The arch over it: a fan of the same picture's top edge.
        import math
        steps = 10
        for k in range(steps):
            a0 = math.pi * k / steps
            a1 = math.pi * (k + 1) / steps
            p0 = (x, y1 + arch * math.sin(a0), z + (w / 2) * math.cos(a0))
            p1 = (x, y1 + arch * math.sin(a1), z + (w / 2) * math.cos(a1))
            centre = (x, y1, z)
            u0 = 0.5 - 0.5 * math.cos(a0)
            u1 = 0.5 - 0.5 * math.cos(a1)
            g.triangle(name, WINDOW_TEX, (centre, p1, p0),
                       ((0.5, 0.25), (u1, 0.0), (u0, 0.0)), unlit=True)

        # Sill and frame, wood.
        g.box(name + "_frame", WOOD_TEX, (-HALF_X, y0 - 8, z - w / 2 - 8), (-HALF_X + 10, y0, z + w / 2 + 8),
              tile=32)
        for side in (-1, 1):
            g.box(name + "_frame", WOOD_TEX,
                  (-HALF_X, y0, z + side * (w / 2) - (4 if side > 0 else 0) + (0 if side > 0 else -4)),
                  (-HALF_X + 6, y1 + 4, z + side * (w / 2) + (4 if side > 0 else 0) + (0 if side > 0 else 4)),
                  tile=32)

    # Sconces between and beside the windows, lit.
    for z in (-HALF_Z + 70, -20.0, HALF_Z - 70):
        g.box("sgb_sconces", SCONCE_TEX, (-HALF_X, 118, z - 12), (-HALF_X + 3, 142, z + 12),
              faces="+x", tile="stretch")
        g.box("sgb_sconces", SHADE_LIT, (-HALF_X + 3, 128, z - 8), (-HALF_X + 14, 150, z + 8),
              tile="stretch", unlit=True)

    # --- the entrance door, back wall, left of centre -----------------------------------
    door_x, door_w, door_h = -150.0, 76.0, 150.0
    g.box("sgb_frontdoor", DOOR_TEX, (door_x - door_w / 2, FLOOR, HALF_Z - 3), (door_x + door_w / 2, door_h, HALF_Z),
          faces="-z", tile="stretch")
    g.box("sgb_frontdoor", WOOD_TEX, (door_x - door_w / 2 - 6, FLOOR, HALF_Z - 4), (door_x - door_w / 2, door_h + 6, HALF_Z),
          tile=32)
    g.box("sgb_frontdoor", WOOD_TEX, (door_x + door_w / 2, FLOOR, HALF_Z - 4), (door_x + door_w / 2 + 6, door_h + 6, HALF_Z),
          tile=32)
    g.box("sgb_frontdoor", WOOD_TEX, (door_x - door_w / 2 - 6, door_h, HALF_Z - 4), (door_x + door_w / 2 + 6, door_h + 6, HALF_Z),
          tile=32)

    # The doormat and the coat stand beside the door.
    g.box("sgb_doormat", "CARPET1", (door_x - 40, FLOOR, HALF_Z - 70), (door_x + 40, FLOOR + 1.5, HALF_Z - 10),
          faces="+y +z -z +x -x", tile=64)
    g.cylinder("sgb_coatstand", WOOD_TEX, (door_x + 80, HALF_Z - 30), 3.5, FLOOR, 130, sides=8, tile=32)
    g.cylinder("sgb_coatstand", WOOD_TEX, (door_x + 80, HALF_Z - 30), 16, FLOOR, 3, sides=10, tile=32)
    for dx, dz in ((10, 0), (-10, 0), (0, 10), (0, -10)):
        g.box("sgb_coatstand", WOOD_TEX, (door_x + 80 + dx - 2, 118, HALF_Z - 30 + dz - 2),
              (door_x + 80 + dx + 2, 132, HALF_Z - 30 + dz + 2), tile=16)

    # --- the back door to the rooms behind, right wall, near the back -------------------
    g.box("sgb_backdoor", BACK_DOOR_TEX, (HALF_X - 3, FLOOR, 250), (HALF_X, 140, 320),
          faces="-x", tile="stretch")
    g.box("sgb_backdoor", WOOD_TEX, (HALF_X - 4, FLOOR, 244), (HALF_X, 146, 250), tile=32)
    g.box("sgb_backdoor", WOOD_TEX, (HALF_X - 4, FLOOR, 320), (HALF_X, 146, 326), tile=32)
    g.box("sgb_backdoor", WOOD_TEX, (HALF_X - 4, 140, 244), (HALF_X, 146, 326), tile=32)

    # --- tall bookcases, back wall, ground floor ---------------------------------------
    # From the door's frame to the brick wall, in bays of the chateau's carved cases.
    case_depth, case_w, case_h = 26.0, 64.0, 152.0
    x = door_x + door_w / 2 + 14
    bay = 0
    while x + case_w <= HALF_X - 2:
        tex = CASE_TEX[bay % 3]
        g.box("sgb_bookcases", tex, (x, FLOOR, HALF_Z - case_depth), (x + case_w, case_h, HALF_Z - 0.5),
              faces="-z", tile="stretch")
        g.box("sgb_bookcases", WOOD_TEX, (x, FLOOR, HALF_Z - case_depth), (x + case_w, case_h, HALF_Z - 0.5),
              faces="+x -x +y", tile=32)
        x += case_w
        bay += 1

    # And to the left of the door, one narrow bay.
    g.box("sgb_bookcases", CASE_TEX[1], (-HALF_X + 2, FLOOR, HALF_Z - case_depth), (door_x - door_w / 2 - 14, case_h, HALF_Z - 0.5),
          faces="-z", tile="stretch")
    g.box("sgb_bookcases", WOOD_TEX, (-HALF_X + 2, FLOOR, HALF_Z - case_depth), (door_x - door_w / 2 - 14, case_h, HALF_Z - 0.5),
          faces="+x -x +y", tile=32)

    # Shelves under the windows on the left wall, and the low cases along the right.
    shelf_h = 40.0
    for z0, z1 in ((-HALF_Z + 30, -220.0), (-80.0, 40.0), (180.0, HALF_Z - 40)):
        g.box("sgb_lowshelves", SHELF_TEX[0], (-HALF_X + 0.5, FLOOR, z0), (-HALF_X + 24, shelf_h + 30, z1),
              faces="+x", tile="stretch")
        g.box("sgb_lowshelves", WOOD_TEX, (-HALF_X + 0.5, FLOOR, z0), (-HALF_X + 24, shelf_h + 30, z1),
              faces="+z -z +y", tile=32)

    # The reading tables at the front -- low island cases the camera looks over.
    for x0, x1 in ((-240.0, -60.0), (60.0, 240.0)):
        g.box("sgb_islands", SHELF_TEX[1], (x0, FLOOR, -HALF_Z + 40), (x1, 70, -HALF_Z + 84),
              faces="+z -z", tile="stretch")
        g.box("sgb_islands", WOOD_TEX, (x0, FLOOR, -HALF_Z + 40), (x1, 70, -HALF_Z + 84),
              faces="+x -x +y", tile=32)

    # --- the gallery -------------------------------------------------------------------
    # A mezzanine round all four walls: floor slab (planks on top, boards below), a fascia,
    # a railing of turned balusters along the inner edge with a wooden handrail, and posts
    # down to the floor at the inner corners.
    d = GALLERY_DEPTH
    slabs = [
        ((-HALF_X, -HALF_Z), (-HALF_X + d, HALF_Z)),          # left
        ((HALF_X - d, -HALF_Z), (HALF_X, HALF_Z)),            # right
        ((-HALF_X + d, HALF_Z - d), (HALF_X - d, HALF_Z)),    # back
        ((-HALF_X + d, -HALF_Z), (HALF_X - d, -HALF_Z + d)),  # front, under the camera
    ]
    for (x0, z0), (x1, z1) in slabs:
        g.box("sgb_gallery", PLANK_TEX, (x0, GALLERY - GALLERY_THICK, z0), (x1, GALLERY, z1),
              faces="+y", tile=96)
        g.box("sgb_gallery", WOOD_TEX, (x0, GALLERY - GALLERY_THICK, z0), (x1, GALLERY, z1),
              faces="-y", tile=48)
        g.box("sgb_gallery", BEAM_TEX, (x0, GALLERY - GALLERY_THICK, z0), (x1, GALLERY, z1),
              faces="+x -x +z -z", tile=(64, GALLERY_THICK), origin="face")

    # Railing: along the inner edges. Each run is a keyed card of balusters, drawn from
    # both sides, with a handrail box on top and a bottom rail.
    inner_x0, inner_x1 = -HALF_X + d, HALF_X - d
    inner_z0, inner_z1 = -HALF_Z + d, HALF_Z - d
    runs = [
        ((inner_x0, inner_z0), (inner_x0, inner_z1)),   # left edge
        ((inner_x1, inner_z0), (inner_x1, inner_z1)),   # right edge
        ((inner_x0, inner_z1), (inner_x1, inner_z1)),   # back edge
        ((inner_x0, inner_z0), (inner_x1, inner_z0)),   # front edge
    ]
    for (ax, az), (bx, bz) in runs:
        length = abs(bx - ax) + abs(bz - az)
        reps = length / 40.0
        y0, y1 = GALLERY + 3, GALLERY + RAIL
        g.quad("sgb_railing", RAIL_TEX,
               [(ax, y0, az), (bx, y0, bz), (bx, y1, bz), (ax, y1, az)],
               [(0, 1), (reps, 1), (reps, 0), (0, 0)], both=True)
        lo = (min(ax, bx) - 3, y1, min(az, bz) - 3)
        hi = (max(ax, bx) + 3, y1 + 5, max(az, bz) + 3)
        g.box("sgb_railing", WOOD_TEX, lo, hi, tile=32)
        g.box("sgb_railing", WOOD_TEX, (lo[0], GALLERY, lo[2]), (hi[0], GALLERY + 3, hi[2]), tile=32)

    for x, z in ((inner_x0, inner_z0), (inner_x1, inner_z0), (inner_x0, inner_z1), (inner_x1, inner_z1)):
        g.box("sgb_posts", POST_TEX, (x - 8, FLOOR, z - 8), (x + 8, GALLERY, z + 8), tile=(32, 128), origin="face")

    # Upper-storey shelves: lawyer's bookcases along the gallery's back and side walls.
    up0, up1 = GALLERY, GALLERY + 130
    g.box("sgb_uppershelves", SHELF_TEX[0], (-HALF_X + d - 20, up0, HALF_Z - 22), (HALF_X - d + 20, up1, HALF_Z - 0.5),
          faces="-z", tile=(128, up1 - up0), origin="face")
    g.box("sgb_uppershelves", WOOD_TEX, (-HALF_X + d - 20, up0, HALF_Z - 22), (HALF_X - d + 20, up1, HALF_Z - 0.5),
          faces="+x -x +y", tile=32)
    for z0, z1 in ((-HALF_Z + 4, -230.0), (-70.0, 30.0), (190.0, HALF_Z - 26)):
        g.box("sgb_uppershelves", SHELF_TEX[1], (HALF_X - 22, up0, z0), (HALF_X - 0.5, up1, z1),
              faces="-x", tile=(128, up1 - up0), origin="face")
        g.box("sgb_uppershelves", WOOD_TEX, (HALF_X - 22, up0, z0), (HALF_X - 0.5, up1, z1),
              faces="+z -z +y", tile=32)

    # --- the staircase, right wall, front to back --------------------------------------
    # Straight flight against the brick, rising toward the back onto the right gallery.
    steps = 11
    run, width = 24.0, 64.0
    rise = (GALLERY - FLOOR) / steps
    z_start = -HALF_Z + d + 10
    for i in range(steps):
        y1 = FLOOR + rise * (i + 1)
        z0 = z_start + run * i
        g.box("sgb_stairs", "DARKADGEDPLANK", (HALF_X - width, FLOOR, z0), (HALF_X - 0.5, y1, z0 + run),
              faces="+y -z -x", tile=(32, 128), origin="face")
    # The landing joins the gallery; a stringer closes the side.
    g.box("sgb_stairs", WOOD_TEX, (HALF_X - width - 3, FLOOR, z_start), (HALF_X - width, GALLERY, z_start + run * steps),
          faces="-x", tile=32)

    # The stair rail: the lobby's wrought scrollwork, keyed, as one diagonal card along
    # the open side, plus a newel at the foot.
    z_top = z_start + run * steps
    g.quad("sgb_stairrail", STAIR_RAIL_TEX,
           [(HALF_X - width - 1, FLOOR + 4, z_start), (HALF_X - width - 1, GALLERY + 4, z_top),
            (HALF_X - width - 1, GALLERY + RAIL, z_top), (HALF_X - width - 1, FLOOR + RAIL, z_start)],
           [(0, 1), (1, 0), (1, -0.12), (0, 0.88)], both=True)
    g.box("sgb_stairrail", WOOD_TEX, (HALF_X - width - 6, FLOOR, z_start - 6), (HALF_X - width + 2, FLOOR + RAIL + 8, z_start + 2),
          tile=16)

    # --- the rolling ladder against the back cases -------------------------------------
    lx = 120.0
    for dx in (-14, 14):
        g.box("sgb_ladder", BEAM_TEX, (lx + dx - 3, FLOOR, HALF_Z - case_depth - 34), (lx + dx + 3, 200, HALF_Z - case_depth - 30),
              tile=(16, 128), origin="face")
    for k in range(9):
        y = 18 + k * 21
        g.box("sgb_ladder", WOOD_TEX, (lx - 14, y, HALF_Z - case_depth - 35), (lx + 14, y + 3, HALF_Z - case_depth - 31),
              tile=16)

    # --- Gabriel's desk, middle of the floor, and what is on it -------------------------
    dx0, dx1, dz0, dz1, top = -70.0, 70.0, -70.0, -10.0, 32.0
    g.box("sgb_desk", DESK_TOP, (dx0, FLOOR, dz0), (dx1, top, dz1), faces="+y", tile="stretch")
    g.box("sgb_desk", DESK_FRONT, (dx0, FLOOR, dz0), (dx1, top, dz1), faces="-z +z", tile="stretch")
    g.box("sgb_desk", DESK_SIDE, (dx0, FLOOR, dz0), (dx1, top, dz1), faces="+x -x", tile="stretch")
    g.box("sgb_rug", RUG_TEX, (-150, FLOOR + 0.4, -120), (150, FLOOR + 0.8, 60), faces="+y", tile="stretch")

    # The stack of his own paperbacks -- leather-coloured spines stand in for covers
    # nobody painted -- the coffee cup, and a paper or two.
    for k, spine in enumerate((BOOK_SPINES[2], BOOK_SPINES[1], BOOK_SPINES[3], BOOK_SPINES[0])):
        y = top + k * 4
        jog = (k % 2) * 2
        g.box("sgb_novels", spine, (dx0 + 14 + jog, y, dz0 + 10), (dx0 + 34 + jog, y + 4, dz0 + 38),
              tile="stretch")
    g.cylinder("sgb_cup", CUP_TEX, (dx1 - 22, dz0 + 20), 5, top, top + 8, sides=10)
    g.box("sgb_papers", PAPER_TEX, (dx0 + 50, top + 0.3, dz1 - 34), (dx0 + 92, top + 0.6, dz1 - 8),
          faces="+y", tile="stretch")

    # His chair, behind the desk, its back to the shelves and facing the camera.
    chair("sgb_chair", CHAIR_TEX, 0.0, dz1 + 26, back="+z")

    # Books left about: a pile on the floor beside the desk, a row standing on each front
    # table, and one lying open on the desk.
    for k, spine in enumerate((BOOK_SPINES[0], BOOK_SPINES[3], BOOK_SPINES[1], BOOK_SPINES[2], BOOK_SPINES[0])):
        y = FLOOR + k * 4.5
        jog = (k % 3) * 1.5
        g.box("sgb_floorbooks", spine, (dx1 + 16 + jog, y, dz0 - 4), (dx1 + 40 + jog, y + 4.5, dz0 + 28),
              tile="stretch")

    for x0, x1 in ((-240.0, -60.0), (60.0, 240.0)):
        x = x0 + 10
        k = 0
        while x + 6 <= x1 - 10:
            spine = BOOK_SPINES[k % 4]
            h = 22 + (k * 7) % 9
            g.box("sgb_tablebooks", spine, (x, 70, -HALF_Z + 48), (x + 5.5, 70 + h, -HALF_Z + 76),
                  tile="stretch")
            x += 6.0
            k += 1

    g.box("sgb_openbook", LEDGER_TEX, (dx0 + 44, top, dz0 + 12), (dx0 + 84, top + 1.5, dz0 + 40),
          faces="+y", tile="stretch")
    g.box("sgb_openbook", BOOK_SIDE, (dx0 + 44, top, dz0 + 12), (dx0 + 84, top + 1.5, dz0 + 40),
          faces="+x -x +z -z", tile="stretch")

    # --- Grace's desk by the brick wall, the lamp, the ledger, her chair ----------------
    gx0, gx1, gz0, gz1, gtop = HALF_X - 130, HALF_X - 30, 60.0, 130.0, 32.0
    g.box("sgb_gracedesk", DESK2_TOP, (gx0, FLOOR, gz0), (gx1, gtop, gz1), faces="+y", tile="stretch")
    g.box("sgb_gracedesk", DESK2_DRAWER, (gx0, FLOOR, gz0), (gx1, gtop, gz1), faces="-x +x -z +z", tile=(50, 32), origin="face")
    g.box("sgb_ledger", LEDGER_TEX, (gx0 + 20, gtop, gz0 + 16), (gx0 + 60, gtop + 2, gz0 + 52), faces="+y", tile="stretch")
    g.box("sgb_ledger", WOOD_TEX, (gx0 + 20, gtop, gz0 + 16), (gx0 + 60, gtop + 2, gz0 + 52), faces="+x -x +z -z", tile=8)

    # A banker's lamp: brass stem, green shade, and the glow under it.
    lampx, lampz = gx1 - 22, gz1 - 18
    g.cylinder("sgb_lamp", BRASS_TEX, (lampx, lampz), 6, gtop, gtop + 2, sides=10)
    g.cylinder("sgb_lamp", BRASS_TEX, (lampx, lampz), 1.5, gtop, gtop + 22, sides=6)
    g.box("sgb_lamp", GREEN_TEX, (lampx - 14, gtop + 20, lampz - 7), (lampx + 14, gtop + 28, lampz + 7),
          faces="+y +x -x +z -z", tile=32)
    g.box("sgb_lamp", GLOW_TEX, (lampx - 13, gtop + 19.5, lampz - 6), (lampx + 13, gtop + 20, lampz + 6),
          faces="-y", tile="stretch", unlit=True)

    # Her chair, pulled out from the desk, its back to the room.
    chair("sgb_gracechair", "CHAIR", gx0 - 26, (gz0 + gz1) / 2, back="-x")

    # And the book that starts the whole affair, lying on her desk: the one somebody
    # left outside the hotel room door.
    g.box("sgb_grailbook", BOOK_COVER, (gx0 + 66, gtop, gz0 + 20), (gx0 + 86, gtop + 3, gz0 + 48),
          faces="+y", tile="stretch")
    g.box("sgb_grailbook", BOOK_SIDE, (gx0 + 66, gtop, gz0 + 20), (gx0 + 86, gtop + 3, gz0 + 48),
          faces="+x -x +z -z", tile="stretch")

    # The painting over her desk, on the brick.
    px0, px1 = 40.0, 150.0
    g.box("sgb_painting", PAINTING_TEX, (HALF_X - 3, 105, px0), (HALF_X - 0.5, 105 + (px1 - px0), px1),
          faces="-x", tile="stretch")
    g.box("sgb_painting", WOOD_TEX, (HALF_X - 4, 105, px0), (HALF_X - 0.5, 105 + (px1 - px0), px1),
          faces="+z -z +y -y", tile=16)

    # The red curtain further back along the brick wall.
    g.box("sgb_curtain", CURTAIN_TEX, (HALF_X - 10, 20, 160), (HALF_X - 1, 230, 230),
          faces="-x +z -z", tile=(70, 210), origin="face")
    g.box("sgb_curtain", BRASS_TEX, (HALF_X - 14, 230, 150), (HALF_X - 1, 234, 240), tile=16)

    # --- the chandelier, and the lights along the gallery --------------------------------
    cy = CEILING - 95
    g.cylinder("sgb_chandelier", BRASS_TEX, (0, -60), 1.5, cy + 12, CEILING, sides=6)
    g.cylinder("sgb_chandelier", BRASS_TEX, (0, -60), 30, cy, cy + 4, sides=16)
    g.cylinder("sgb_chandelier", BRASS_TEX, (0, -60), 14, cy - 18, cy + 12, sides=10)
    for k in range(8):
        import math
        a = 2 * math.pi * k / 8
        bx, bz = 30 * math.cos(a), -60 + 30 * math.sin(a)
        g.box("sgb_chandelier", BRASS_TEX, (bx - 2, cy + 4, bz - 2), (bx + 2, cy + 14, bz + 2), tile=16)
        g.box("sgb_chandelier", SHADE_LIT, (bx - 2.5, cy + 14, bz - 2.5), (bx + 2.5, cy + 22, bz + 2.5),
              tile="stretch", unlit=True)

    return g


def chair(name, texture, cx, cz, back):
    """A plain wooden chair: a seat on four legs, and a slatted back on the side named.

    The seat is 34 across at 24 high, the back 26 above it as two uprights and three
    rails. Solid boxes read as cabinets from across a room; slats read as a chair."""
    g = _current
    half = 17.0
    g.box(name, texture, (cx - half, 22, cz - half), (cx + half, 26, cz + half), tile=32)

    for lx_, lz in ((cx - half + 1, cz - half + 1), (cx + half - 4, cz - half + 1),
                    (cx - half + 1, cz + half - 4), (cx + half - 4, cz + half - 4)):
        g.box(name, texture, (lx_, FLOOR, lz), (lx_ + 3, 22, lz + 3), tile=16)

    # Which face of the seat the back stands on: an axis and a sign.
    axis, sign = back[1], 1 if back[0] == "+" else -1

    def post(along, y0, y1, thick=3.0):
        if axis == "z":
            return ((cx + along - thick / 2, y0, cz + sign * (half - thick)),
                    (cx + along + thick / 2, y1, cz + sign * half))
        return ((cx + sign * (half - thick), y0, cz + along - thick / 2),
                (cx + sign * half, y1, cz + along + thick / 2))

    for along in (-half + 1.5, half - 1.5):
        lo, hi = post(along, 26, 54)
        g.box(name, texture, lo, hi, tile=16)

    for y in (32, 40, 48):
        if axis == "z":
            lo = (cx - half, y, cz + sign * (half - 2.5))
            hi = (cx + half, y + 3, cz + sign * half)
        else:
            lo = (cx + sign * (half - 2.5), y, cz - half)
            hi = (cx + sign * half, y + 3, cz + half)
        g.box(name, texture, (min(lo[0], hi[0]), lo[1], min(lo[2], hi[2])),
              (max(lo[0], hi[0]), hi[1], max(lo[2], hi[2])), tile=16)


_current = None


def build_camera_shell():
    """The shell the camera is kept inside: a box wound inward, a little inside the walls."""
    g = Glb()
    g.box("sgbcambnds", FLOOR_TEX, (-HALF_X + 30, FLOOR + 20, -HALF_Z + 30), (HALF_X - 30, CEILING - 30, HALF_Z - 30),
          tile=64, inward=True)
    return g


# ---------------------------------------------------------------------- walk boundary ---
W = H = 128
SIZE_X, SIZE_Z = HALF_X * 2, HALF_Z * 2

# What stands on the floor, as world rectangles Gabriel cannot walk through.
BLOCKS = [
    (-70 - 6, -70 - 6, 70 + 6 + 40, -10 + 6),         # his desk, and the pile beside it
    (-24, -12, 24, 36),                               # his chair
    (HALF_X - 136, 54, HALF_X - 24, 136),             # Grace's desk
    (HALF_X - 180, 70, HALF_X - 136, 120),            # her chair
    (-HALF_X, HALF_Z - 32, HALF_X, HALF_Z),           # the back-wall cases
    (-HALF_X, -HALF_Z, -HALF_X + 28, HALF_Z),         # the left-wall shelves
    (-240, -HALF_Z, -60, -HALF_Z + 90),               # front islands
    (60, -HALF_Z, 240, -HALF_Z + 90),
    (HALF_X - 70, -HALF_Z + GALLERY_DEPTH, HALF_X, HALF_Z),   # the stairs and under them
    (100, HALF_Z - 70, 140, HALF_Z - 26),             # the ladder
    (-150 + 62, HALF_Z - 50, -150 + 98, HALF_Z - 10), # the coat stand
]
POSTS = [(-HALF_X + GALLERY_DEPTH, -HALF_Z + GALLERY_DEPTH), (HALF_X - GALLERY_DEPTH, -HALF_Z + GALLERY_DEPTH),
         (-HALF_X + GALLERY_DEPTH, HALF_Z - GALLERY_DEPTH), (HALF_X - GALLERY_DEPTH, HALF_Z - GALLERY_DEPTH)]
WALL_MARGIN = 26.0


def world(x, y):
    u = (x + 0.5) / W
    v = 1.0 - ((y + 0.5) / H)
    return u * SIZE_X - HALF_X, v * SIZE_Z - HALF_Z


def blocked(wx, wz):
    for x0, z0, x1, z1 in BLOCKS:
        if x0 <= wx <= x1 and z0 <= wz <= z1:
            return True
    for px, pz in POSTS:
        if abs(wx - px) <= 12 and abs(wz - pz) <= 12:
            return True
    return False


def region(wx, wz):
    inside = min(HALF_X - abs(wx), HALF_Z - abs(wz))

    if inside <= WALL_MARGIN or blocked(wx, wz):
        return 255

    clear = inside - WALL_MARGIN

    for x0, z0, x1, z1 in BLOCKS:
        dx = max(x0 - wx, 0.0, wx - x1)
        dz = max(z0 - wz, 0.0, wz - z1)
        clear = min(clear, max(dx, dz))

    return max(0, min(7, 7 - int(clear / 12.0)))


def write_boundary(path):
    indices = bytes(region(*world(x, y)) for y in range(H) for x in range(W))
    palette = b"".join(struct.pack("<BBBB", i, i, i, 0) for i in range(256))
    rows = b"".join(indices[(y * W):(y * W) + W] for y in range(H - 1, -1, -1))
    offset = 14 + 40 + len(palette)
    header = struct.pack("<2sIHHI", b"BM", offset + len(rows), 0, 0, offset)
    info = struct.pack("<IiiHHIIiiII", 40, W, H, 1, 8, 0, len(rows), 2835, 2835, 256, 256)

    with open(path, "wb") as handle:
        handle.write(header + info + palette + rows)

    return sum(1 for i in indices if i < 8)


def strip_id3(mp3):
    """The MP3 stream alone: an ID3v2 tag at the front is dropped and so is an ID3v1
    footer, because the decoder wants frames and a tag is not one."""
    if mp3[:3] == b"ID3" and len(mp3) > 10:
        size = ((mp3[6] & 0x7F) << 21) | ((mp3[7] & 0x7F) << 14) | ((mp3[8] & 0x7F) << 7) | (mp3[9] & 0x7F)
        footer = 10 if mp3[5] & 0x10 else 0
        mp3 = mp3[10 + size + footer:]

    if mp3[-128:-125] == b"TAG":
        mp3 = mp3[:-128]

    return mp3


def mp3_header(mp3):
    """Channels and sample rate off the first frame header, for the RIFF fmt chunk."""
    rates = {0: (44100, 48000, 32000), 2: (22050, 24000, 16000), 0x10: (11025, 12000, 8000)}

    for at in range(min(len(mp3) - 4, 65536)):
        b0, b1, b2, b3 = mp3[at:at + 4]

        if b0 != 0xFF or (b1 & 0xE0) != 0xE0:
            continue

        version = (b1 >> 3) & 3          # 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
        rate_index = (b2 >> 2) & 3

        if version == 1 or rate_index == 3:
            continue

        table = rates[{3: 0, 2: 2, 0: 0x10}[version]]
        channels = 1 if (b3 >> 6) == 3 else 2
        return channels, table[rate_index]

    raise ValueError("no MPEG frame header found")


def wrap_music(mp3_path, out_path):
    """Writes an MP3 as the RIFF the game stores its own music in."""
    with open(mp3_path, "rb") as handle:
        mp3 = strip_id3(handle.read())

    channels, rate = mp3_header(mp3)

    # MPEGLAYER3WAVEFORMAT: the 16-byte WAVEFORMAT, a 12-byte extension length and the
    # extension. The reader here needs only the first sixteen; the rest is what the
    # game's own files carry and what any other player expects to find.
    fmt = struct.pack("<HHIIHH", 85, channels, rate, len(mp3) * 8 // 140 // 8 or 1, 1, 0)
    fmt += struct.pack("<HHIHHH", 12, 1, 2, 1, 1, 1393)
    fact = struct.pack("<I", 0)
    body = (b"fmt " + struct.pack("<I", len(fmt)) + fmt
            + b"fact" + struct.pack("<I", len(fact)) + fact
            + b"data" + struct.pack("<I", len(mp3)) + mp3 + (bytes(1) if len(mp3) & 1 else b""))

    with open(out_path, "wb") as handle:
        handle.write(b"RIFF" + struct.pack("<I", 4 + len(body)) + b"WAVE" + body)

    return channels, rate, len(mp3)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--music", help="an MP3 to wrap as the shop's music, SGBTHEME")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    room = build_room()
    shell = build_camera_shell()
    objects, tris, verts = room.stats()
    print(f"Sgb: {objects} objects, {tris} triangles, {verts} vertices")
    for name in room.order:
        print(f"  {name}")

    if args.dry_run:
        return

    rooms = os.path.join(args.workspace, "enhanced", "rooms")
    models = os.path.join(args.workspace, "enhanced", "models")
    os.makedirs(rooms, exist_ok=True)
    os.makedirs(models, exist_ok=True)

    room.write(os.path.join(rooms, "Sgb.glb"))
    print(f"wrote {os.path.join(rooms, 'Sgb.glb')}")
    shell.write(os.path.join(models, "sgbcambnds.glb"))
    print(f"wrote {os.path.join(models, 'sgbcambnds.glb')}")

    open_texels = write_boundary(os.path.join(rooms, "SGBWLKBNDS.BMP"))
    print(f"wrote {os.path.join(rooms, 'SGBWLKBNDS.BMP')}: {open_texels} of {W * H} texels open")

    source = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sgb")
    for name in sorted(os.listdir(source)):
        shutil.copyfile(os.path.join(source, name), os.path.join(rooms, name))
        print(f"{os.path.join(rooms, name)}: copied")

    if args.music:
        music = os.path.join(args.workspace, "enhanced", "audio", "music")
        os.makedirs(music, exist_ok=True)
        out = os.path.join(music, "SGBTHEME.WAV.wav")
        channels, rate, size = wrap_music(args.music, out)
        print(f"wrote {out}: {size} bytes of MPEG layer 3, {channels} channel(s) at {rate} Hz")


if __name__ == "__main__":
    main()
