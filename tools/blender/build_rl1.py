"""Builds the town of Rennes-les-Bains, which GK3 modelled eight houses of.

    blender --background --factory-startup --python tools/blender/build_rl1.py -- \
        --workspace D:/Dev/GK3Reborn/ContentWorkspace [--dry-run]

Writes one GLB per piece into ``enhanced/models`` and the placements themselves into
``Assets/Story/Dressing.txt``, alongside Couiza's. Nothing in ``GK3/Data`` is touched and
nothing that shipped is moved: this is content the ReBarn packs carry, and an installation
without them is the game exactly as it was released.

What is wrong with RL1
----------------------

The room is a spa town in the Aude valley and what stands in it is eight buildings along
one street -- ``howse``, ``rl1_rongetseggs``, ``rl1_rongetsteethpulled``,
``rl1_rongetslaid`` down the west side, ``rl1_rongetsbread``, ``rl1_bar``,
``rl1_ronspersonalartgallery`` down the east, and ``bighouse`` closing the north end. The
walk bitmap is 618 by 1350 units, so the street is about fifteen metres wide and thirty-two
long, and past the last house on either side there is grass to the edge of the map. Gabriel
rides in on a road that goes nowhere and stands in a town that is a film set: one street
deep, with the valley visible straight through the gaps between the shops.

Unlike Couiza this is not a kit problem. RL1 *is* the Alsatian kit -- Sierra built one
half-timbered village and used it here and at the station -- and here it is the room's own
material, so the town is extended in it rather than out of Rennes-le-Chateau's masonry.
Every new building is one of these eight, recentred, turned, mirrored, or butted into a
terrace with its neighbours. Nothing is imported from another room, so there is no seam
between what shipped and what did not.

The three things this adds
--------------------------

**Ground.** The floor runs X -516 to 754 by Z -762 to 1701 and the town fills nearly all of
it, so there is nowhere to put a second street. ``RBN_RB_GROUND*`` extends it: four plates
around the existing floor, each vertex taking the height of the nearest point on the floor
itself, so the seam is continuous and the new ground rolls the way the old ground rolls at
the join. They are ``type=decal`` -- drawn like a prop and skipped by the picker -- because
an actor takes his height from the *floor* object and must never be given a second one, and
because a ground plate the player can click is a place the walk code will try to send him.

**A town on it.** Two quarters, east and west, laid out by the same packer Couiza uses: a
street is a line, the packer walks it, and a building that would touch another is not
placed. All of it is outside ``RL1CAMERABOUNDS``, which fences the camera at X -452 to 751
and Z -791 to 1736 -- so the new quarters are seen across the roofs of the old street and
never approached, which is what lets a house be a house with no inside.

**Roads.** The street already has one: ``rl1_Path2`` is baked into the floor and runs the
length of the map. That is a dirt track, and a spa town with a hotel and four shops on it
has a metalled road. ``ROAD`` is laid over the line the path already takes, one unit above
the floor, and the lanes into the two new quarters run off it.

The rules are Couiza's rules
----------------------------

They were all learned there and every one of them is a building that was simply not in the
room, with nothing on screen to say so. See ``build_couiza.py``, whose machinery this
imports rather than repeats:

* ``type=prop``. ``type=scene`` means "an object already inside the BSP" and loads no file.
* Every placement names a model no other placement names, because ``MergeModels`` keys a
  room's models by name and RL1 has both a room file and eight timeblock files.
* The game's Z is **minus** Blender's Y.
* An arrived material is renamed in place, never replaced, or the name gains a ``.001`` and
  the engine asks the archives for a bitmap that does not exist.
* A surface index comes off a material name when the geometry leaves its room.

Units are GK3's: roughly 2.4 cm to the unit.
"""

import argparse
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import build_couiza  # noqa: E402
from build_couiza import (  # noqa: E402
    Ground,
    audit_glass,
    bounds,
    box_corners,
    build_apron,
    build_piece,
    build_road,
    build_terrace,
    build_tree_card,
    build_variant,
    corners_of,
    export_glb,
    footprint,
    import_glb,
    overlap,
    pack_along,
    reset_scene,
    shapes_overlap,
    walk_polyline,
)


ROOM = "RL1"

# What every model this builds is called. `RB` for Rennes-les-Bains, beside Couiza's `CZ`,
# because the sweep at the end of the run deletes anything with this prefix that the run
# did not write and the two towns must not sweep each other away.
PREFIX = "RBN_RB_"


# --------------------------------------------------------------------------------------
# The donors
# --------------------------------------------------------------------------------------

# RL1's own eight, which is the whole point: the town is extended in the material it is
# already built from. `bighouse` and `howse` carry no room prefix because they are the two
# Sierra never renamed.
#
# Each is a body; the windows, doors and shutters lying on it are separate objects in the
# room -- that is how RL1 was cut up for lighting -- and `build_piece` collects the ones
# whose boxes touch the body and recentres the group together. Taking a body on its own
# gives a blank box with the windows left behind at the far end of the street.
PIECES = {
    # name                room   body                              what it is
    "RBN_RB_HOUSE":      (ROOM, "howse"),                          # tall, half-timbered
    "RBN_RB_BIG":        (ROOM, "bighouse"),                       # the biggest on the street
    "RBN_RB_BAR":        (ROOM, "rl1_bar"),                        # the bar, three storeys
    "RBN_RB_BREAD":      (ROOM, "rl1_rongetsbread"),               # the bakery
    "RBN_RB_EGGS":       (ROOM, "rl1_rongetseggs"),                # low, long
    "RBN_RB_TEETH":      (ROOM, "rl1_rongetsteethpulled"),         # the dentist
    "RBN_RB_LAID":       (ROOM, "rl1_rongetslaid"),                # two storeys, steep roof
    "RBN_RB_GALLERY":    (ROOM, "rl1_ronspersonalartgallery"),     # the gallery
}


# --------------------------------------------------------------------------------------
# The terraces
# --------------------------------------------------------------------------------------

# Houses butted together into one model. A French spa town is terraced -- the shops on the
# street already share party walls -- and a row is what stops the new quarters reading as
# detached boxes on a lawn.
#
# `overlap` is how far each is pushed into its neighbour: 12 units is about thirty
# centimetres, enough that no daylight shows between two walls at any angle.
TERRACES = {
    "RBN_RB_ROW_A": (["RBN_RB_HOUSE", "RBN_RB_EGGS", "RBN_RB_TEETH"], 12),
    "RBN_RB_ROW_B": (["RBN_RB_BREAD", "RBN_RB_LAID", "RBN_RB_GALLERY"], 12),
    "RBN_RB_ROW_C": (["RBN_RB_TEETH", "RBN_RB_HOUSE", "RBN_RB_BREAD", "RBN_RB_EGGS"], 12),
    "RBN_RB_ROW_D": (["RBN_RB_LAID", "RBN_RB_BAR"], 12),
    "RBN_RB_ROW_E": (["RBN_RB_GALLERY", "RBN_RB_TEETH"], 12),
    "RBN_RB_ROW_F": (["RBN_RB_EGGS", "RBN_RB_LAID", "RBN_RB_HOUSE"], 12),
    "RBN_RB_ROW_G": (["RBN_RB_BREAD", "RBN_RB_TEETH"], 12),
    "RBN_RB_ROW_H": (["RBN_RB_HOUSE", "RBN_RB_GALLERY"], 12),
}


# --------------------------------------------------------------------------------------
# The ground
# --------------------------------------------------------------------------------------

# What the floor covers, measured off `rl1_floor` itself.
FLOOR = (-516.1, -761.6, 754.3, 1700.6)

# Where the camera can get, off `RL1CAMERABOUNDS`. Anything outside this is seen from the
# street and never approached.
CAMERA = (-451.9, -791.2, 751.1, 1736.0)

# The walk bitmap, from RL1.SIF's `boundary=rl1wlkBnds,size={618.472290,1349.645874},
# offset={79.474701,26.514526}`. `WalkBoundary` maps it as `world = u * size - offset`, so
# the offset is subtracted: X -79.5 to 539.0 by Z -26.5 to 1323.1. Reading it the other way
# round puts the town's west line three hundred units inside the street.
WALK = (-79.474701, -26.514526, 539.0, 1323.1)

# The plates that extend the floor, as (name, x0, z0, x1, z1). They butt against the
# floor's own edge and overlap it by 40 units, which is a seam the depth buffer decides and
# not a gap: two grass surfaces of the same texture at the same height read as one.
#
# The corners are covered by the north and south plates running the full width, so the four
# make a ring rather than a cross.
GROUND_PLATES = [
    # Four hundred units wider all round than the streets need, because a frontage is
    # measured from the road's centreline and the deepest house reaches five hundred units
    # past it: a plate cut to the last street put a terrace's back corner twenty units over
    # the edge and into the air. The check at the end of the run is what found it.
    ("west",  -3200.0, -3300.0,  -476.0,  3900.0),
    ("east",    714.0, -3300.0,  3520.0,  3900.0),
    ("south", -3200.0, -3300.0,  3520.0,  -722.0),
    ("north", -3200.0,  1660.0,  3520.0,  3900.0),
]

# How the plates are meshed. 110 units to a cell is about two and a half metres, which is
# finer than the floor itself and keeps the extension from reading as a different surface
# where the two meet.
GROUND_CELL = 190.0

# What the new ground is painted with. The floor's own grass, so the seam is one material
# and one set of enhanced maps rather than two that nearly match.
GROUND_TEXTURE = "Grass_Dirt0"

# How far the ground plates sit below the floor at the seam. Half a unit is about a
# centimetre: enough that the floor wins every pixel the two share, so the join can never
# flicker, and far too little to see as a step.
GROUND_DROP = 0.5


# --------------------------------------------------------------------------------------
# The roads
# --------------------------------------------------------------------------------------

# The main street first, then the lanes off it. Every road is a polyline in the room's own
# coordinates with a width, and `build_road` lays it as a ribbon that follows the floor.
#
# The main street traces `rl1_Path2`, which is the dirt track already baked into the floor:
# measured band by band it runs X 18-324 through the middle of the map, swings west at
# Z -500 and again at Z 1100. Laying the road on the track rather than beside it is what
# makes this a resurfacing instead of a second road.
ROADS = [
    ("main street", [(210, -2700), (210, -2200), (215, -1700), (215, -1150),
                     (205, -800), (185, -500), (175, -220), (180, 60), (200, 340),
                     (215, 620), (205, 900), (170, 1150), (150, 1400), (140, 1700),
                     (135, 2200), (130, 2800), (130, 3300)], 190.0),

    # Each quarter is a plan rather than a scatter: a spine parallel to the old street, a
    # second street eleven hundred units further out, and cross streets between them. The
    # spacing is not a taste: a frontage needs the road's half width, seventy of verge and
    # half the deepest house behind it, which is about five hundred units, so two streets
    # facing each other across less than a thousand have no room between them and the
    # packer refuses every house on the inner side of both.
    #
    # The same arithmetic decides how far out the spines go. `RL1CAMERABOUNDS` fences the
    # camera at X -452 to 751, nothing may be built inside it, and the frontage on the
    # town-facing side of a spine reaches five hundred units toward it -- so a spine at
    # -820 had its whole east side refused and the quarter was half a street.
    ("west spine", [(-1000, -2400), (-1010, -1800), (-1020, -1200), (-1015, -600),
                    (-1010, 0), (-1005, 600), (-1000, 1200), (-995, 1800),
                    (-990, 2400), (-985, 3000)], 150.0),
    ("west outer", [(-2100, -2200), (-2110, -1600), (-2115, -1000), (-2110, -400),
                    (-2105, 200), (-2100, 800), (-2095, 1400), (-2090, 2000),
                    (-2085, 2600), (-2080, 3150)], 140.0),

    ("east spine", [(1320, -2400), (1330, -1800), (1340, -1200), (1335, -600),
                    (1330, 0), (1335, 600), (1345, 1200), (1350, 1800),
                    (1355, 2400), (1360, 3000)], 150.0),
    ("east outer", [(2420, -2200), (2430, -1600), (2435, -1000), (2430, -400),
                    (2425, 200), (2430, 800), (2435, 1400), (2440, 2000),
                    (2445, 2600), (2450, 3150)], 140.0),

    # The lanes off the old street into each quarter. Both leave by a gap Sierra left: the
    # west one at the swing the track already makes at Z -500, the east one between
    # `rl1_bar`, which ends at Z 795, and the gallery, which begins at 923.
    ("west lane", [(185, -500), (-300, -520), (-800, -535), (-1300, -550),
                   (-1800, -560), (-2400, -570)], 150.0),
    ("east lane", [(215, 860), (700, 865), (1200, 870), (1700, 875), (2200, 878),
                   (2800, 880)], 150.0),

    # The cross streets, which is where most of the town ends up: they run across the
    # quarters rather than along them, so each one is frontage the spines do not have.
    ("west cross A", [(-2500, -1750), (-2000, -1745), (-1400, -1740), (-800, -1735)], 140.0),
    ("west cross B", [(-2500, -1050), (-2000, -1045), (-1400, -1040), (-800, -1035)], 140.0),
    ("west cross C", [(-2500, 200), (-2000, 205), (-1400, 210), (-800, 215)], 140.0),
    ("west cross D", [(-2500, 900), (-2000, 905), (-1400, 910), (-800, 915)], 140.0),
    ("west cross E", [(-2500, 1600), (-2000, 1605), (-1400, 1610), (-800, 1615)], 140.0),
    ("west cross F", [(-2500, 2300), (-2000, 2305), (-1400, 2310), (-800, 2315)], 140.0),
    ("west cross G", [(-2500, 3000), (-2000, 3005), (-1400, 3010), (-800, 3015)], 140.0),

    ("east cross A", [(1100, -1750), (1700, -1745), (2300, -1740), (2900, -1735)], 140.0),
    ("east cross B", [(1100, -1050), (1700, -1045), (2300, -1040), (2900, -1035)], 140.0),
    ("east cross C", [(1100, 200), (1700, 205), (2300, 210), (2900, 215)], 140.0),
    ("east cross D", [(1100, 1400), (1700, 1405), (2300, 1410), (2900, 1415)], 140.0),
    ("east cross E", [(1100, 2100), (1700, 2105), (2300, 2110), (2900, 2115)], 140.0),
    ("east cross F", [(1100, 2800), (1700, 2805), (2300, 2810), (2900, 2815)], 140.0),

    # The south end, where the road comes into the town, and the north end beyond the
    # bighouse. Both are far enough outside the camera shell to be built on both sides.
    ("south street A", [(-2500, -1500), (-1400, -1490), (-300, -1480), (800, -1470),
                        (1900, -1460), (2900, -1450)], 150.0),
    ("south street B", [(-2500, -2300), (-1400, -2290), (-300, -2280), (800, -2270),
                        (1900, -2260), (2900, -2250)], 140.0),
    ("north street A", [(-2500, 2100), (-1400, 2105), (-300, 2110), (800, 2115),
                        (1900, 2120), (2900, 2125)], 150.0),
    ("north street B", [(-2500, 2900), (-1400, 2905), (-300, 2910), (800, 2915),
                        (1900, 2920), (2900, 2925)], 140.0),
]

# Surfaced areas rather than ribbons: (name, x0, z0, x1, z1). A spa town square, where the
# two west lanes meet the back street. Somewhere for the road network to be a place rather
# than a junction.
APRONS = [
    ("west square", -1350.0, 220.0, -900.0, 660.0),
    ("east yard", 1560.0, 560.0, 2000.0, 1000.0),
]

# How wide the verge is: the gap between the kerb and the frontage line.
#
# A hundred and ten units, which is about two and a half metres, and the number is decided
# by the trees rather than by the pavement. `road_cells` claims twenty units past the kerb,
# a trunk is checked as a box twenty-eight units across, and a tree that overlaps either
# the carriageway or the house behind it is refused -- so a seventy-unit verge leaves no
# band a tree can stand in at all, and the avenue came out with three trees in it.
VERGE = 110.0

# Where a street tree stands in that verge, measured out from the kerb, and how far apart
# they are along the road.
TREE_SETBACK = 55.0
TREE_SPACING = 330.0

# Which roads have houses along them, as (road name, which side, how far back). +1 is the
# left of the road's direction of travel and -1 the right; the verge is the gap between the
# kerb and the frontage.
#
# The main street is not in this list. It is the street that shipped, it is inside the
# camera shell for its whole length, and both its sides are already built on -- the packer
# would fill the two gaps Sierra left on purpose, one of which is the bar's terrace.
FRONTAGES = [
    # The cross streets first, because they are the frontage the quarters are actually
    # built along and the packer gives what it reaches first the pick of the ground. Laying
    # the spines first filled the corridor with a row facing the wrong way and left the
    # cross streets with nothing.
    ("west cross A", -1, VERGE), ("west cross A", +1, VERGE),
    ("west cross B", -1, VERGE), ("west cross B", +1, VERGE),
    ("west cross C", -1, VERGE), ("west cross C", +1, VERGE),
    ("west cross D", -1, VERGE), ("west cross D", +1, VERGE),
    ("west cross E", -1, VERGE), ("west cross E", +1, VERGE),
    ("west cross F", -1, VERGE), ("west cross F", +1, VERGE),
    ("west cross G", -1, VERGE), ("west cross G", +1, VERGE),

    ("east cross A", -1, VERGE), ("east cross A", +1, VERGE),
    ("east cross B", -1, VERGE), ("east cross B", +1, VERGE),
    ("east cross C", -1, VERGE), ("east cross C", +1, VERGE),
    ("east cross D", -1, VERGE), ("east cross D", +1, VERGE),
    ("east cross E", -1, VERGE), ("east cross E", +1, VERGE),
    ("east cross F", -1, VERGE), ("east cross F", +1, VERGE),

    ("south street A", -1, VERGE), ("south street A", +1, VERGE),
    ("south street B", -1, VERGE), ("south street B", +1, VERGE),
    ("north street A", -1, VERGE), ("north street A", +1, VERGE),
    ("north street B", -1, VERGE), ("north street B", +1, VERGE),

    ("west spine", -1, VERGE), ("west spine", +1, VERGE),
    ("west outer", -1, VERGE), ("west outer", +1, VERGE),
    ("east spine", -1, VERGE), ("east spine", +1, VERGE),
    ("east outer", -1, VERGE), ("east outer", +1, VERGE),

    ("west lane", -1, VERGE), ("west lane", +1, VERGE),
    ("east lane", -1, VERGE), ("east lane", +1, VERGE),

    # The main street's own frontage. The packer is blocked from the camera shell outright,
    # so these fill the approach south of the town and the ground north of the bighouse and
    # touch nothing that shipped.
    ("main street", -1, VERGE + 20.0), ("main street", +1, VERGE + 20.0),
]

# How far apart two buildings stand. Twenty-six units is about sixty centimetres: a joint
# rather than a gap, which is what a terraced street has.
STREET_GAP = 26.0

# How far a building is pushed into the ground, so the grass closes over the footing.
SINK = 6.0

# How far the surfacing floats above the floor, and how much each surface after the first
# adds so that two of them crossing do not fight. Both are Couiza's, for its reasons.
ROAD_LIFT = 1.0
SURFACE_STEP = 0.6

# Plain asphalt with no painted edge line, so it tiles without striping.
ROAD_TEXTURE = "ROAD"

# Which streets are planted, and with what. Plane trees down the lanes and the two spines,
# which is the single most French thing that can be done to a street; the cross streets are
# left bare, because a village does not plant every back lane and because the packer has
# already filled them tightly.
#
# The positions are not typed. They were, and every one of them landed in a house or a
# carriageway once the quarters were packed: the town is dense enough that a spot chosen on
# the map is a spot something is already standing on. So each street is walked and a tree
# tried at intervals along its verge, and the ones that fit are the avenue.
#
# **Nothing further out than about fifteen hundred units.** `Foliage` grows a card into a
# modelled tree and thins the far level of detail by leaf area, so a tree three thousand
# units from the street keeps its branches and loses its leaves -- a ring of them round the
# edge of the map came out as pale bare sticks standing over the rooftops, which is worse
# than no tree at all. RL1's own seventeen cards are all within about seven hundred units
# and none of them shows it.
#
#   road, which side, species, how tall
AVENUES = [
    ("west lane", -1, "TREE00", 310),
    ("west lane", +1, "TREE00", 320),
    ("east lane", -1, "TREE00", 315),
    ("east lane", +1, "TREE00", 305),
    ("west spine", -1, "TREE00", 320),
    ("west spine", +1, "TREE00", 310),
    ("east spine", -1, "TREE00", 315),
    ("east spine", +1, "TREE00", 325),
    ("main street", -1, "TREE06", 330),
    ("main street", +1, "TREE06", 335),
]

# How far from the old street a card may stand and still keep its leaves.
TREE_REACH = 1900.0

# What a tree's own footing takes, for the check that refuses one in a road or a wall. A
# plane tree's trunk, not its crown: the crown is meant to overhang the road and the wall.
TRUNK = 28.0


# --------------------------------------------------------------------------------------
# The room, read out of the workspace
# --------------------------------------------------------------------------------------

def scene_directory(workspace):
    return os.path.join(workspace, "enhanced", "scenes", ROOM, "original")


# Objects that cover the whole map rather than standing somewhere on it. Blocking against
# them would refuse every placement and the check would say nothing at all.
SPREAD = {"rl1_floor", "rl1_trees", "rl1_treeshadowcasters", "rl1_pinetrunks",
          "rl1_lightblocker", "rl1_exit"}


def existing_buildings(workspace):
    """The ground-plan boxes of what RL1 already has.

    Anything that stands up and is wide enough to put a building through. The gate is
    Couiza's -- forty units across both ways and a hundred tall -- which keeps the scooters
    and the barrels out and the eight buildings, the town walls and the two stairs in.
    """
    directory = scene_directory(workspace)
    found = {}

    for entry in sorted(os.listdir(directory)):
        stem = os.path.splitext(entry)[0]

        if not entry.lower().endswith(".glb") or stem in SPREAD:
            continue

        reset_scene()
        objects = import_glb(os.path.join(directory, entry))

        if not objects:
            continue

        least, most = bounds(objects)

        # Imported Z-up: Blender Y is the game's Z and the importer has already negated it,
        # so the game's Z runs from -most.y to -least.y.
        across = most.x - least.x
        through = -least.y - -most.y
        tall = most.z - least.z

        if across >= 40.0 and through >= 40.0 and tall >= 100.0:
            found[stem] = (least.x, -most.y, most.x, -least.y)

    reset_scene()

    return found


def protected(workspace, margin=70.0):
    """Everything the story needs where it is, as boxes nothing may be built over.

    Every object RL1 binds a noun to -- the bar and its door and sign, the shop doors, the
    windows, the parking block, the road exit -- plus every spot the scene file names for
    an actor to stand on. A building over one of those is a puzzle the player cannot reach.

    Roads are not checked against these: a road under the moped is the point.
    """
    import re

    sif = os.path.join(workspace, "normalized", "scenes", ROOM, ROOM + ".SIF")
    directory = scene_directory(workspace)
    keep = set()
    spots = []

    if os.path.exists(sif):
        with open(sif, encoding="latin-1") as handle:
            for line in handle:
                body = line.strip()

                if body.startswith("//"):
                    continue

                if "noun=" in body and body.lower().startswith("model="):
                    keep.add(body[len("model="):].split(",")[0].strip().lower())

                spot = re.match(r"^(\w+),\s*pos=\{([-\d.]+),\s*([-\d.]+),\s*([-\d.]+)\}",
                                body)

                if spot:
                    spots.append((float(spot.group(2)), float(spot.group(4))))

    boxes = []

    for entry in sorted(os.listdir(directory)) if os.path.isdir(directory) else []:
        stem = os.path.splitext(entry)[0]

        if not entry.lower().endswith(".glb") or stem.lower() not in keep:
            continue

        reset_scene()
        imported = import_glb(os.path.join(directory, entry))

        if not imported:
            continue

        least, most = bounds(imported)
        boxes.append((least.x - margin, -most.y - margin, most.x + margin, -least.y + margin))

    reset_scene()

    boxes.extend((x - 120.0, z - 120.0, x + 120.0, z + 120.0) for x, z in spots)

    return boxes


def room_trees(workspace, trunk=45.0):
    """Where RL1's own trees stand, as boxes a new building may not be put through."""
    boxes = []

    for entry in ("rl1_trees.glb", "rl1_pinetrunks.glb"):
        path = os.path.join(scene_directory(workspace), entry)

        if not os.path.exists(path):
            continue

        reset_scene()
        objects = import_glb(path)

        # One box per island of connected faces, because both files hold every tree in the
        # room as one object and its bounding box is the whole map.
        for obj in objects:
            mesh = obj.data
            matrix = obj.matrix_world
            seen = set()

            for polygon in mesh.polygons:
                if polygon.index in seen:
                    continue

                centre = matrix @ polygon.center
                seen.add(polygon.index)
                boxes.append((centre.x - trunk, -centre.y - trunk,
                              centre.x + trunk, -centre.y + trunk))

    reset_scene()

    return merge_boxes(boxes)


def merge_boxes(boxes, slack=30.0):
    """Collapses overlapping boxes, so a tree drawn as forty cards is one obstacle."""
    merged = []

    for box in sorted(boxes):
        for i, other in enumerate(merged):
            if overlap((box[0] - slack, box[1] - slack, box[2] + slack, box[3] + slack),
                       other) != (0.0, 0.0):
                merged[i] = (min(box[0], other[0]), min(box[1], other[1]),
                             max(box[2], other[2]), max(box[3], other[3]))
                break
        else:
            merged.append(box)

    return merged


def road_cells(points, width, cell=60.0):
    """A road rasterised into boxes, which is what the packer walks."""
    cells = []
    half = width / 2.0 + 20.0

    for (x0, z0), (x1, z1) in zip(points, points[1:]):
        span = max(abs(x1 - x0), abs(z1 - z0))
        steps = max(1, int(span / cell))

        for step in range(steps + 1):
            t = step / steps
            x = x0 + (x1 - x0) * t
            z = z0 + (z1 - z0) * t
            cells.append((x - half, z - half, x + half, z + half))

    return cells


def all_road_cells():
    cells = []

    for _, points, width in ROADS:
        cells.extend(road_cells(points, width))

    for _, x0, z0, x1, z1 in APRONS:
        cells.append((x0, z0, x1, z1))

    return cells


def road_named(name):
    for label, points, width in ROADS:
        if label == name:
            return points, width

    raise SystemExit(f"There is no road called {name!r}.")


def walkable_test(workspace):
    """Whether a point is inside the walk bitmap.

    The rectangle rather than the bitmap itself: this only ever asks "could an actor be
    standing here", and the whole of the new town is hundreds of units outside it, so the
    difference between the rectangle and the walkable pixels inside it never arises.
    """
    x0, z0, x1, z1 = WALK

    def walkable(x, z):
        return x0 <= x <= x1 and z0 <= z <= z1

    return walkable


# --------------------------------------------------------------------------------------
# The ground, extended
# --------------------------------------------------------------------------------------

class TownGround(Ground):
    """The floor, plus the ground this run lays around it.

    Everything downstream asks the ground three questions -- how high is it here, is there
    any here at all, and what is the lowest of it under this footprint -- and all three go
    through ``at``. Overriding that one method is therefore the whole of extending the map:
    the packer stops refusing a house for standing off the floor, a road laid across the
    new quarter follows ground instead of hanging at zero, and a plate's own vertices and
    the road on top of it read the same number and cannot part company.

    Off the floor the answer is the height of the nearest floor there is, taken off a
    coarse grid built once. That is what makes the seam continuous: a point just outside
    the floor's edge reads the cell just inside it, which is the height the floor actually
    arrives at, so the extension leaves at whatever angle the floor is running.

    The grid exists for speed and the speed is the point. ``Ground.at`` walks every one of
    the floor's 1,663 triangles, and the plates alone want a few thousand samples with a
    search behind each one; done directly that is a quarter of a billion triangle tests and
    the run does not finish.
    """

    def __init__(self, path, plates, cell=90.0):
        super().__init__(path)

        self._cell = cell
        self._plates = list(plates)
        self._grid = {}

        x0, z0, x1, z1 = FLOOR

        # One sample per cell over the floor's own rectangle. A cell with no floor under
        # its centre is simply absent, which is how the edge of the map is described.
        self._columns = int((x1 - x0) / cell) + 1
        self._rows = int((z1 - z0) / cell) + 1
        self._origin = (x0, z0)

        for column in range(self._columns):
            for row in range(self._rows):
                x = x0 + column * cell
                z = z0 + row * cell
                found = super().at(x, z, None)

                if found is not None:
                    self._grid[(column, row)] = found

        self._nearest = {}

    def covered(self, x, z):
        """Whether this run has ground here at all, floor or plate."""
        if FLOOR[0] <= x <= FLOOR[2] and FLOOR[1] <= z <= FLOOR[3]:
            return True

        return any(x0 <= x <= x1 and z0 <= z <= z1 for _, x0, z0, x1, z1 in self._plates)

    def at(self, x, z, default=0.0):
        exact = super().at(x, z, None)

        if exact is not None:
            return exact

        if not self.covered(x, z):
            return default

        # The borrowed height itself, with no drop applied. This is a height field and
        # nothing else: the plates take the drop when they are meshed, the roads take
        # their lift when they are laid, and a height function that had already decided
        # one of those would be answering a question it was not asked.
        return self._borrowed(x, z)

    def _borrowed(self, x, z):
        """The height of the nearest floor cell, cached per cell rather than per point."""
        cell = self._cell
        key = (int(round((x - self._origin[0]) / cell)),
               int(round((z - self._origin[1]) / cell)))

        if key in self._nearest:
            return self._nearest[key]

        # Clamped into the grid first, so a point far out to the west searches from the
        # floor's west edge rather than from wherever it happens to be.
        column = min(max(key[0], 0), self._columns - 1)
        row = min(max(key[1], 0), self._rows - 1)

        best = None
        closest = None

        for (gc, gr), height in self._grid.items():
            distance = (gc - column) ** 2 + (gr - row) ** 2

            if closest is None or distance < closest:
                closest = distance
                best = height

        self._nearest[key] = best if best is not None else 0.0

        return self._nearest[key]


def build_ground(name, x0, z0, x1, z1, ground, cell=GROUND_CELL):
    """One plate of new ground, meshed on a grid and hung off the floor's own edge.

    Every vertex takes the height of the floor under it. Where there is no floor under it
    -- which is most of a plate, because the point of them is to be outside it -- it takes
    the height of the nearest point that *does* have floor, found by walking back toward
    the floor's middle. That is what makes the seam continuous: a vertex on the join reads
    the floor directly, and the one beyond it reads the same triangle, so the two agree to
    the millimetre and the extension leaves the floor at whatever angle the floor arrives
    at.

    Dropped half a unit below what it samples, so that where the plate laps over the floor
    the floor wins every pixel and the join cannot flicker.
    """
    reset_scene()

    mesh = bpy.data.meshes.new(name)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)

    across = max(1, int(round((x1 - x0) / cell)))
    along = max(1, int(round((z1 - z0) / cell)))

    # The same answer the roads and the houses on this plate will take, because it is the
    # same call. A plate meshed off one height function and surfaced off another is a road
    # that floats over its own ground wherever the two disagree.
    height = ground.at

    verts = []
    faces = []

    for row in range(along + 1):
        for column in range(across + 1):
            x = x0 + (x1 - x0) * column / across
            z = z0 + (z1 - z0) * row / along

            # Blender's Y is the game's -Z, and Blender's Z is the game's Y.
            verts.append(Vector((x, -z, height(x, z) - GROUND_DROP)))

    for row in range(along):
        for column in range(across):
            a = row * (across + 1) + column
            b = a + 1
            c = a + across + 1
            d = c + 1

            # Wound so the face points up in the game's frame.
            faces.append((a, c, d, b))

    mesh.from_pydata([tuple(v) for v in verts], [], faces)
    mesh.update()

    material = bpy.data.materials.new(GROUND_TEXTURE)
    material.name = GROUND_TEXTURE
    mesh.materials.append(material)

    return obj


# --------------------------------------------------------------------------------------
# The table
# --------------------------------------------------------------------------------------

TABLE_HEADER = """\

[RL1_GROUND]
# Rennes-les-Bains, extended.
#
# GENERATED by tools/blender/build_rl1.py. Edit the layout there, not here.
#
# RL1 is a spa town with eight buildings on one street and grass to the edge of the map on
# both sides of it. These lines add the ground for a town, two quarters of houses on it,
# and a metalled road along the track the floor already carries -- all of it outside the
# walk bitmap and outside RL1CAMERABOUNDS, and all of it built from RL1's own eight
# buildings, so the new streets are the same half-timbered kit as the old one. Nothing
# that shipped is moved or repainted.
#
# Applied only when the geometry it names is installed -- see SceneDressing -- so an
# installation without the enhanced packs is exactly the game as it shipped.
#
# type=decal, which is this port's own: it loads and draws like a prop and ScenePicker
# skips it. Ground and road both have to be one. A prop swallows the click that would have
# walked the player, because SceneInteraction.FloorTarget only walks him when the pick is
# the floor object by name -- and an actor takes his height from the floor mesh, so a
# second ground he could stand on is a second answer to a question that has one.
"""


def write_table(path, ground_lines, road_lines, facade_lines, tree_lines):
    """Appends RL1's four sections to the dressing table Couiza already wrote.

    Read and rewritten rather than opened for append, so that a second run replaces this
    town's sections instead of adding them again. Couiza's own sections are copied through
    untouched: the two builders write into one file and neither may lose the other's work.
    """
    keep = []

    if os.path.exists(path):
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                if line.strip().startswith("[RL1_"):
                    break

                keep.append(line)

    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("".join(keep).rstrip("\n") + "\n")
        handle.write(TABLE_HEADER)
        handle.writelines(ground_lines)
        handle.write("\n[RL1_ROADS]\n")
        handle.writelines(road_lines)
        handle.write("\n[RL1_FACADES]\n"
                     "# Every one answers to BUILDINGS, which RL1_ALL.NVC already gives a\n"
                     "# LOOK rule and a recorded line for both characters, so the town needs\n"
                     "# no dialogue of its own.\n")
        handle.writelines(facade_lines)
        handle.write("\n[RL1_TREES]\n"
                     "# Foliage cards, carrying no pos on purpose: each is authored at the\n"
                     "# coordinates it stands at, because the modelled-tree pass measures a\n"
                     "# card and grows a tree that carries its own transform, and a model\n"
                     "# the tree pass has claimed never consults pos.\n")
        handle.writelines(tree_lines)


# --------------------------------------------------------------------------------------

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--table", default=None)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    workspace = args.workspace
    out = os.path.join(workspace, "enhanced", "models")

    made = {}
    report = []

    def keep(name, obj, note):
        path = os.path.join(out, name + ".glb")

        if not args.dry_run:
            export_glb(obj, path)

        made[name] = path
        report.append(f"  {name:22s} {len(obj.data.polygons):5d} faces  {note}")

    # 1. The eight, each with the glass that belongs to it.
    for name, (room, body) in PIECES.items():
        piece, note = build_piece(workspace, name, room, body)

        if piece is None:
            report.append(f"  {name:22s} SKIPPED  {note}")
            continue

        keep(name, piece, note)

    # `build_terrace` falls back to rebuilding a piece from the donor table when the file
    # it was told about is not there, which is every piece on a dry run. The table it reads
    # is its own module's, so it is pointed at this town's for the length of this run --
    # otherwise a terrace of RL1 houses is assembled out of Rennes-le-Chateau's masonry, or
    # more likely refuses to assemble at all.
    build_couiza.PIECES = PIECES

    # 2. The terraces, out of the houses that survived.
    for name, (pieces, butt) in TERRACES.items():
        if any(p not in made for p in pieces):
            report.append(f"  {name:22s} SKIPPED  a piece it needs was not built")
            continue

        cache = {p: os.path.join(out, p + ".glb") for p in pieces}
        terrace, note = build_terrace(workspace, name, pieces, butt, cache)

        if terrace is None:
            report.append(f"  {name:22s} SKIPPED  {note}")
            continue

        keep(name, terrace, note)

    # 3. The floor, and the ground this run will lay around it. One object, because the
    #    packer, the roads and the houses all have to agree about where the map ends -- and
    #    with the plain floor they would agree that it ends at the old town's edge, which
    #    refuses every placement in the two new quarters.
    ground = TownGround(
        os.path.join(scene_directory(workspace), "rl1_floor.glb"), GROUND_PLATES)

    # 4. What each piece measures, taken from the file that was written rather than from
    #    the object that wrote it, so a piece the exporter changed is measured as read.
    sizes = {}

    for name, path in list(made.items()):
        reset_scene()
        imported = import_glb(path)

        if imported:
            least, most = bounds(imported)
            sizes[name] = (most.x - least.x, most.y - least.y)

    reset_scene()

    # 5. The layout.
    shipped = existing_buildings(workspace)
    blocked = list(shipped.values())
    blocked.extend(protected(workspace))

    # The street that shipped, plus a margin, is off limits to the packer outright: the two
    # gaps in it are the bar's terrace and the lane the east quarter leaves by, and a house
    # in either is a house standing in the room the player walks through.
    blocked.append((CAMERA[0] - 60.0, CAMERA[1] - 60.0, CAMERA[2] + 60.0, CAMERA[3] + 60.0))

    roads = all_road_cells()
    standing = room_trees(workspace)
    blocked.extend(standing)

    houses = [p for p in sizes if p in PIECES or p in TERRACES]

    layout = []

    for name, side, verge in FRONTAGES:
        points, width = road_named(name)

        # `pack_along` rather than `pack_frontage`, which looks its road up in Couiza's own
        # table and would find none of these.
        placed = pack_along(points, width, side, verge, houses,
                            {p: sizes[p] for p in houses},
                            blocked, roads, ground)
        layout.extend(placed)

        # Everything placed blocks what comes after it, or the second rank is laid through
        # the first.
        for model, x, z, heading in placed:
            blocked.append(footprint(sizes[model][0], sizes[model][1], x, z, heading))

        report.append(
            f"  {name + (' left' if side > 0 else ' right') + (' back' if verge > 200 else ''):34s}"
            f" {len(placed)} building(s)")

    # 6. A placement must name a model no other placement names: MergeModels keys a room's
    #    models by name and RL1 has a room file and eight timeblock files, so the merge
    #    runs and the second of two identical names silently replaces the first. Every
    #    second copy is mirrored as well, which a street of eight repeating houses needs.
    used = {}
    final = []

    for piece, x, z, heading in layout:
        count = used.get(piece, 0)
        used[piece] = count + 1

        if count == 0:
            final.append((piece, x, z, heading))
            continue

        name = f"{piece}_{count:02d}"
        obj, note = build_variant(name, os.path.join(out, piece + ".glb"), count % 2 == 1)

        if obj is None:
            report.append(f"  {name:22s} SKIPPED  {piece} {note}")
            continue

        keep(name, obj, f"{piece}, {note}")
        sizes[name] = sizes[piece]
        final.append((name, x, z, heading))

    # 7. The ground plates, then the roads on top of them, then the trees.
    ground_lines = []

    for index, (label, x0, z0, x1, z1) in enumerate(GROUND_PLATES, start=1):
        name = f"{PREFIX}GROUND{index:02d}"
        plate = build_ground(name, x0, z0, x1, z1, ground)

        if not args.dry_run:
            export_glb(plate, os.path.join(out, name + ".glb"))

        made[name] = os.path.join(out, name + ".glb")
        ground_lines.append(f"append {ROOM}.SIF MODELS model={name}, type=decal\n")
        report.append(f"  {name:22s} {len(plate.data.polygons):5d} faces  {label}")

    reset_scene()

    # The ground has to be under the roads, so the roads' own lift is measured from the
    # floor and every plate is already below it.
    road_lines = []

    for index, (label, points, width) in enumerate(ROADS, start=1):
        name = f"{PREFIX}ROAD{index:02d}"
        road = build_road(name, points, width, ground,
                          ROAD_LIFT + (len(APRONS) + index - 1) * SURFACE_STEP)

        if not args.dry_run:
            export_glb(road, os.path.join(out, name + ".glb"))

        made[name] = os.path.join(out, name + ".glb")
        road_lines.append(f"append {ROOM}.SIF MODELS model={name}, type=decal\n")
        report.append(f"  {name:22s} {len(road.data.polygons):5d} faces  {label}")

    for index, (label, x0, z0, x1, z1) in enumerate(APRONS, start=1):
        name = f"{PREFIX}APRON{index:02d}"
        apron = build_apron(name, x0, z0, x1, z1, ground,
                            ROAD_LIFT + (index - 1) * SURFACE_STEP)

        if not args.dry_run:
            export_glb(apron, os.path.join(out, name + ".glb"))

        made[name] = os.path.join(out, name + ".glb")
        road_lines.append(f"append {ROOM}.SIF MODELS model={name}, type=decal\n")
        report.append(f"  {name:22s} {len(apron.data.polygons):5d} faces  {label}")

    # 8. The placements, with the ground sampled under each one.
    facade_lines = []
    boxes = {}
    shapes = {}

    for model, x, z, heading in final:
        box = footprint(sizes[model][0], sizes[model][1], x, z, heading)
        boxes[(model, x, z)] = box
        shapes[(model, x, z)] = corners_of(x, z, sizes[model][0], sizes[model][1], heading)

        # The lowest ground the footprint covers, so a house on a slope is dug into the
        # hill rather than standing on one corner and showing daylight under the other
        # three. `lowest` samples through `at`, so it reads the plates as readily as the
        # floor.
        y = ground.lowest(box) - SINK

        facade_lines.append(
            f"append {ROOM}.SIF MODELS model={model}, noun=BUILDINGS, type=prop, "
            f"pos={{{x},{y:.1f},{z}}}, heading={heading}\n")

    # 9. The trees, checked against everything already standing.
    walkable = walkable_test(workspace)
    tree_lines = []
    grown = 0
    refused = []

    against = [("a road", cell) for cell in roads]
    against += [(model, box) for (model, _, _), box in boxes.items()]
    against += [(name, box) for name, box in shipped.items()]
    against += [("a tree of the room's own", box) for box in standing]

    def plant(x, z, height, sprite):
        """Puts a card down where one fits, and says why where one does not."""
        nonlocal grown

        foot = (x - TRUNK, z - TRUNK, x + TRUNK, z + TRUNK)
        blocking = next(
            (why for why, box in against if overlap(foot, box) != (0.0, 0.0)), None)

        if blocking is None and walkable(x, z):
            blocking = "ground an actor can walk on"

        if blocking is None and ground.off_map(x, z):
            blocking = "no ground"

        if blocking is not None:
            refused.append(f"{x:.0f},{z:.0f} in {blocking}")
            return

        grown += 1
        name = f"{PREFIX}TREE{grown:02d}"
        card = build_tree_card(name, x, z, height, sprite, ground.at)

        if not args.dry_run:
            export_glb(card, os.path.join(out, name + ".glb"))

        made[name] = os.path.join(out, name + ".glb")
        tree_lines.append(f"append {ROOM}.SIF MODELS model={name}, type=prop\n")

        # A tree blocks the next one, or an avenue is one tree drawn ten times in a row.
        against.append(("a tree already planted", foot))

    for road, side, sprite, height in AVENUES:
        points, width = road_named(road)
        marks = walk_polyline(points, TREE_SPACING)

        for x, z, tx, tz in marks:
            # The outward normal of this side of the road, out past the kerb into the verge.
            nx, nz = -tz * side, tx * side
            back = width / 2.0 + TREE_SETBACK

            at_x = x + nx * back
            at_z = z + nz * back

            # Far enough out and the foliage pass thins the card to bare branches, which
            # reads as a dead tree on the skyline. Measured from the old street rather than
            # from the origin, because that is where it is seen from.
            if abs(at_x - 200.0) > TREE_REACH or abs(at_z - 650.0) > TREE_REACH:
                continue

            plant(at_x, at_z, height, sprite)

    # The refusals are counted rather than listed. A packed town refuses most of the spots
    # an avenue asks for -- there is a building or a carriageway on nearly all of them --
    # so the list is pages long and says nothing; the count is what tells you a street has
    # gone unplanted.
    report.append(f"  {'trees':22s} {grown} cards, {len(refused)} spots taken")

    # 10. Every way this can be wrong, checked before anything is written.
    complaints = []

    for (model, x, z), mine in boxes.items():
        for other, theirs in shipped.items():
            on_x, on_z = overlap(mine, theirs)

            if on_x > 40.0 and on_z > 40.0:
                complaints.append(
                    f"{model} at {x},{z} stands {on_x:.0f}x{on_z:.0f} into {other}")

    entries = list(boxes.items())

    for i, ((model, x, z), mine) in enumerate(entries):
        for (other, ox, oz), theirs in entries[i + 1:]:
            on_x, on_z = overlap(mine, theirs)

            if on_x > 40.0 and on_z > 40.0:
                complaints.append(
                    f"{model} at {x},{z} stands {on_x:.0f}x{on_z:.0f} into "
                    f"{other} at {ox},{oz}")

    for (model, x, z), shape in shapes.items():
        if any(shapes_overlap(shape, box_corners(cell), 8.0) for cell in roads):
            complaints.append(f"{model} at {x},{z} stands on a road")

    for (model, x, z), mine in boxes.items():
        on_x, on_z = overlap(mine, WALK)

        if on_x > 40.0 and on_z > 40.0:
            complaints.append(
                f"{model} at {x},{z} reaches {on_x:.0f}x{on_z:.0f} into the walk bitmap")

        on_x, on_z = overlap(mine, CAMERA)

        if on_x > 40.0 and on_z > 40.0:
            complaints.append(
                f"{model} at {x},{z} reaches {on_x:.0f}x{on_z:.0f} inside the camera shell")

    # Nothing may stand off the ground this run laid, or it floats over the valley.
    covered = [(x0, z0, x1, z1) for _, x0, z0, x1, z1 in GROUND_PLATES] + [FLOOR]

    for (model, x, z), mine in boxes.items():
        corners = [(mine[0], mine[1]), (mine[2], mine[1]),
                   (mine[0], mine[3]), (mine[2], mine[3])]

        loose = [c for c in corners
                 if not any(p[0] <= c[0] <= p[2] and p[1] <= c[1] <= p[3] for p in covered)]

        if loose:
            complaints.append(f"{model} at {x},{z} stands off the edge of the ground")

    if not args.dry_run:
        floating = {name: audit_glass(path) for name, path in sorted(made.items())
                    if "GROUND" not in name and "ROAD" not in name
                    and "APRON" not in name and "TREE" not in name}
        floating = {name: count for name, count in floating.items() if count}

        if floating:
            complaints.extend(
                f"{name} ships {count} window or door face(s) with no wall behind them"
                for name, count in floating.items())
        else:
            report.append(f"  {'glass audit':22s} every window and door is on a wall")

    if complaints:
        print("Rennes-les-Bains")
        print("\n".join(report))
        raise SystemExit(
            "The layout is not clear:\n  " + "\n  ".join(sorted(complaints)))

    table = args.table or os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        "src", "GK3Reborn.Engine", "Assets", "Story", "Dressing.txt")

    # Anything with this prefix the run did not write is from a layout that no longer
    # exists, and leaving it is not harmless: `pack-content` takes the whole directory, so
    # a stale terrace ships. The prefix is the boundary; Couiza's `RBN_CZ_` files and
    # everything else in enhanced/models are untouched.
    swept = 0

    if not args.dry_run:
        kept = {os.path.basename(path).lower() for path in made.values()}

        for entry in sorted(os.listdir(out)):
            if entry.lower().startswith(PREFIX.lower()) and entry.lower() not in kept:
                os.remove(os.path.join(out, entry))
                swept += 1

    if swept:
        report.append(f"  {'swept':22s} {swept} file(s) from an older layout")

    if not args.dry_run:
        write_table(table, ground_lines, road_lines, facade_lines, tree_lines)

    print("Rennes-les-Bains")
    print("\n".join(report))
    print(f"  {len(facade_lines)} facade placements, {len(road_lines)} surfaces, "
          f"{len(tree_lines)} trees")
    print(f"  table -> {table}")


if __name__ == "__main__":
    main()
