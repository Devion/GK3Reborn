"""Builds the town of Couiza, which GK3 never modelled.

    blender --background --factory-startup --python tools/blender/build_couiza.py -- \
        --workspace D:/Dev/GK3Reborn/ContentWorkspace [--only NAME ...] [--dry-run]

Writes one GLB per piece into ``enhanced/models``, which is where
``GK3Reborn.Content.ModelLibrary`` looks for a model a scene names and the 1999 archives
have no ``.MOD`` for, and writes the placements themselves as
``Assets/Story/Dressing.txt``. See ``GK3Reborn/docs/couiza.md``.

What is wrong with TR1, in one paragraph
----------------------------------------

The scene is called Couiza and the writing treats it as a town -- Gabriel's own line on
the buildings is "I'm not sure what all these buildings are, but I don't think they're
connected to the station". What was built is eleven boxes totalling about seven hundred
triangles, scattered on open grass two to eight hundred units apart, none of them facing
the road, none of them touching another, and three of them standing past the eastern edge
of the ground mesh entirely. They are skinned in ``RL1barwall*`` and carry painted
half-timbering, because they are Rennes-les-Bains' kit: Sierra built one Alsatian village
and used it for both valley towns. Rennes-le-Chateau, the hero location, got the accurate
Languedoc treatment instead -- rubble stone, stucco, terracotta pantiles.

So this is not a repaint of what is there. What is there stays exactly as it shipped. This
adds the town around it, out of the masonry Sierra built for RC2, RC3 and the cemetery.

Three rules, and all three are about not being noticed
-----------------------------------------------------

**Every piece is built from the game's own geometry and painted with the game's own
bitmaps.** The level crossing is the only thing here modelled from nothing: every house is
one of the seventeen donor buildings in RC2, RC3 and CEM, recentred and stood somewhere
else, or several of them butted into a terrace. The textures those donors name -- ``rc1rghstn``, ``rc1redroof2``, ``rc1Stucco9``, ``rc1yellowroof`` --
are all in ``common.brn``, which every scene on every day already loads, so nothing here
adds a single byte of texture and every surface has the enhanced normal, ORM and height
maps the packs already carry for Rennes-le-Chateau. A town painted with anything else
would read as a town from another game standing behind this one.

**Nothing is placed where the player can walk.** ``TR1.SIF`` writes the walk bitmap as
``size={1448.05,1819.05} offset={-153.948,1123.133}`` and ``WalkBoundary`` maps it as
``world = u * size - offset``, so it covers **X -154 to 1294 by Z -1123 to 696** -- the
offset is subtracted, not added, and reading it the other way puts the town's west line
three hundred units inside the yard. The ground mesh runs X -1405 to 2435 by Z -2584 to
2475. Every placement is checked against both rectangles and the run refuses to write a
table that fails either. The camera shell ``tr1_cambnds`` stops the camera at X 1756, so
nothing here is ever approached, walked into, or seen from close behind -- which is what
lets a house be a house-shaped facade with no interior and no back door.

**A donor is turned by what it is missing, not by what it has.** Sierra built only the
sides of a building that face the street it stands on, so ten of the seventeen donors have
no wall on one side and four have none on two or more. The four are refused outright --
``wstuccohouse`` is one wall and nothing else, and stood in the open it reads as a flat.
The ten are turned so the hole faces the back. Only a house closed on all four sides is
turned by its glass instead.

An earlier version invented the missing wall by mirroring the opposite one. It put a
detached panel in mid-air beside every asymmetric building, dragged the window quads out
there with it, and made a two-sided donor into two parallel walls with a gap. A blank back
is fine; a floating wall is not.

Why the windows travel with the house
-------------------------------------

In RC2 and RC3 a window is not part of the building. It is a separate scene object -- one
flat quad of ``rc1wstuccowin1`` laid on the wall, and there are thirty-five of them across
the two rooms -- because that is how the room was cut up for lighting. Recentring
``rc3_oldstonehouse`` on its own therefore gives a blank stone box, which is the shape of
the mistake this avoids: each body takes the window, door and shutter quads whose boxes
touch it, and the group is recentred together.

The bridge that is not here
---------------------------

A stone overbridge across the railway was asked for and does not fit, which is worth
recording so that nobody costs it twice. The trackbed is flat at y = -9.5 for the whole
length of the map and the cutting either side of it only reaches +40; three independent
checks -- the platform's height above the rail against a French low platform, the taxi's
length, a door's height -- put the scene at 2.3 to 2.7 cm per unit, so clearance above the
railhead needs a soffit at about y = +194. Getting a road up to that from ground at +5 to
+90 at any gradient a road can take needs roughly two thousand units of approach ramp,
which is most of the map. What goes in instead is a level crossing, which is what a rural
French branch line actually has.

Units are GK3's: roughly 2.4 cm to the unit, so a 75-unit door is about waist-to-head and
a 285-unit house is two storeys.
"""

import argparse
import math
import os
import re
import sys

import bpy
from mathutils import Vector


# --------------------------------------------------------------------------------------
# The donors
# --------------------------------------------------------------------------------------

# A piece is a body object plus whatever glass and doors are lying on it. The body is
# named here; the trimmings are found by the box test in `trimmings_for`.
#
# Everything comes from Rennes-le-Chateau's two streets and the cemetery, which are the
# three rooms in the game built out of Languedoc masonry rather than the half-timbered kit.
# Rennes-les-Bains is deliberately *not* a donor: RL1 is the same Alsatian set TR1 already
# has, so borrowing from it would spread the problem rather than fix it.
PIECES = {
    # name                     room    body                    what it is
    "RBN_CZ_STONE":           ("RC3", "rc3_oldstonehouse"),   # rubble stone, two storeys
    "RBN_CZ_STUCCO":          ("RC3", "rc3_stuccohouse"),     # white stucco, red pantile
    "RBN_CZ_COTTAGE":         ("RC3", "rc3_cottage"),         # low, stone, red roof
    "RBN_CZ_COTTAGE2":        ("RC3", "rc3_cottage2"),        # low, stucco, yellow roof
    "RBN_CZ_TALL":            ("RC3", "rc3_oldbighouse"),     # three storeys, yellow roof
    "RBN_CZ_HOUSE1":          ("RC3", "rc3_house1"),          # stucco, plain
    "RBN_CZ_CATHOUSE":        ("RC3", "rc3_cathouse"),        # stucco, yellow roof
    "RBN_CZ_SMALL":           ("RC2", "small_house"),         # one storey, stucco
    "RBN_CZ_SMALL2":          ("RC2", "small_house02"),       # one storey, stone
    "RBN_CZ_WHITE":           ("RC2", "wstuccohouse"),        # long white stucco frontage
    "RBN_CZ_BARN":            ("RC2", "large_building"),      # big stone shed, red roof
    "RBN_CZ_OLD2":            ("CEM", "old_house2"),          # stucco, weathered
    "RBN_CZ_OLD3":            ("CEM", "old_house3"),          # stucco, yellow roof
    "RBN_CZ_SMALL3":          ("CEM", "small_house03"),       # stone, red roof
    "RBN_CZ_WHITE2":          ("CEM", "wstuccohouse01"),      # white stucco, wide
    "RBN_CZ_ROWTOWN":         ("RC3", "rc3_townhouses"),      # a contiguous row already
    "RBN_CZ_ROWMOPED":        ("RC2", "rc2_moped_houses"),    # a long contiguous terrace
}

# Street furniture. Not buildings, so none of the wall gates apply to them: a bench has no
# walls and `open_sides` would refuse every one.
#
# The benches are TR2's own -- the waiting room on the other side of the wall -- so the
# ones outside the station are the same joinery in the same cracked wood as the ones
# inside it. The bin is the Moped Courtyard's, in the barrel and post textures TR1 already
# draws.
FURNITURE = {
    "RBN_CZ_BENCH":       ("TR2", "long_bench"),
    "RBN_CZ_BENCH2":      ("TR2", "short_bench"),
    "RBN_CZ_BENCH3":      ("TR2", "long_bench01"),
    "RBN_CZ_STREETBENCH": ("RC1_A", "rc1bench"),
    "RBN_CZ_BIN":         ("MOP", "mop_trash1"),
    "RBN_CZ_CRATES":      ("MOP", "mop_crates"),
}

# Where each stands: (piece, x, z, heading). Hand-placed, because there are six of them and
# each one wants to be somewhere a person would actually sit or drop a ticket.
#
# All along the forecourt's east edge, which is the band between where an actor can walk
# and where the surfacing ends -- the walk bitmap reaches X 1000 to 1025 through this
# stretch and the apron runs to 1090. Anywhere further in is ground Gabriel walks over,
# and he would walk through the bench.
# Heading 0, so the long axis lies along the edge rather than across it: a bench is 170
# units long and 26 deep, and turned broadside it reaches back into the walkable yard.
# North of where the station approach meets the forecourt, which is Z -513 to -343: sitting
# in the mouth of a junction is what these used to do. That leaves Z -650 to -513, and the
# band is X 1025 -- where an actor can no longer walk -- to the apron's east edge.
#
# Heading 180 turns the seats to the station. They faced the road, which is the wrong way
# round for a station bench: you sit watching for your train, not for traffic.
FURNITURE_SPOTS = [
    ("RBN_CZ_BENCH2", 1075, -552, 180),
    ("RBN_CZ_BIN", 1125, -552, 0),
]

# A tree beside the bench. Listed apart from TREES because a street tree stands *on* the
# surfacing -- a plane tree in a paved forecourt is what a French station yard looks like --
# and the ordinary check refuses a tree on a road. It is still checked against the
# buildings, the room's own trees, and the ground an actor can walk on.
#
# (x, z, height, sprite)
STREET_TREES = [
    (1062, -625, 300, "TREE00"),
]

# Which materials are glass, a door or a shutter. Used both to find the trimmings that
# belong to a body and to measure which way the body faces.
FRONTAGE = ("win", "door", "shutter", "glass")


# --------------------------------------------------------------------------------------
# The terraces
# --------------------------------------------------------------------------------------

# A terrace is several houses butted together into one model, because that is the whole
# difference between a village and a set. French village houses share party walls and each
# one keeps its own roof pitch and its own ridge height, so butting complete donor houses
# side by side -- rather than authoring a single long block -- is both easier and more
# accurate than it sounds.
#
# `overlap` is how far each house is pushed into its neighbour, in units. It is negative
# space: 12 units is about 30 cm, enough that the two walls interpenetrate and no daylight
# shows between them at any angle the camera can reach.
TERRACES = {
    # name              pieces, left to right along the frontage             overlap
    "RBN_CZ_ROW_A": (["RBN_CZ_STONE", "RBN_CZ_COTTAGE2", "RBN_CZ_TALL",
                      "RBN_CZ_SMALL2"], 12),
    "RBN_CZ_ROW_B": (["RBN_CZ_STUCCO", "RBN_CZ_HOUSE1", "RBN_CZ_OLD2",
                      "RBN_CZ_COTTAGE"], 12),
    "RBN_CZ_ROW_C": (["RBN_CZ_OLD3", "RBN_CZ_SMALL", "RBN_CZ_STONE",
                      "RBN_CZ_CATHOUSE"], 12),
    "RBN_CZ_ROW_D": (["RBN_CZ_OLD2", "RBN_CZ_COTTAGE2", "RBN_CZ_SMALL"], 12),
    "RBN_CZ_ROW_E": (["RBN_CZ_TALL", "RBN_CZ_STUCCO", "RBN_CZ_OLD2"], 12),
    "RBN_CZ_ROW_F": (["RBN_CZ_SMALL2", "RBN_CZ_COTTAGE", "RBN_CZ_HOUSE1",
                      "RBN_CZ_OLD3", "RBN_CZ_CATHOUSE"], 12),

    # Two-house rows, because the gaps the packer actually leaves between the buildings
    # that shipped are two to nine hundred units and the four- and five-house rows are
    # thirteen hundred and up. Without these the terraces are built and never placed.
    "RBN_CZ_ROW_G": (["RBN_CZ_COTTAGE", "RBN_CZ_OLD3"], 12),
    "RBN_CZ_ROW_H": (["RBN_CZ_CATHOUSE", "RBN_CZ_SMALL2"], 12),
    "RBN_CZ_ROW_J": (["RBN_CZ_HOUSE1", "RBN_CZ_OLD2"], 12),
}


# --------------------------------------------------------------------------------------
# The streets
# --------------------------------------------------------------------------------------

# The layout is packed rather than typed. Typing it was tried and does not work: the
# terraces are thirteen to twenty hundred units long, the gaps between the buildings that
# already stand on the east line are between two and nine hundred, and a layout written by
# eye put sixty-eight pairs of buildings through each other. None of that is visible from
# any camera the room has -- two roofs in the same place read as one roof from the ground
# -- so it has to be arithmetic rather than judgement.
#
# A street is a line the fronts stand along, and the packer walks it: take the next piece,
# skip forward past anything already standing in that band, place it, leave a gap, repeat
# until the line runs out. It cannot produce an overlap, and the check at the end of the
# run proves it did not.
#
#   name        the face the fronts stand on (X), from Z, to Z, which way they look,
#               and the pieces to draw from, in the order they repeat
# Short pieces every street ends with, so that whatever room is left after the terraces
# is filled by a house rather than by grass. The order matters only in that it repeats.
INFILL = ["RBN_CZ_OLD3", "RBN_CZ_CATHOUSE", "RBN_CZ_SMALL2", "RBN_CZ_HOUSE1",
          "RBN_CZ_SMALL", "RBN_CZ_COTTAGE", "RBN_CZ_COTTAGE2", "RBN_CZ_OLD2"]

# Pieces with no window and no door on any face. Two of the eighteen donors are like this
# -- CEM's small_house03 and RC2's large_building -- because in their own rooms they are
# seen only end-on or at distance. A blank two-storey box on a village street is worse
# than no building, so they are barns: they may stand in a farmyard and nowhere else, and
# the check below refuses them anywhere a street fronts the road.
SHEDS = {"RBN_CZ_SMALL3", "RBN_CZ_BARN"}

# --------------------------------------------------------------------------------------
# The roads
# --------------------------------------------------------------------------------------

# Couiza is a working town on the D118, not a hamlet at the end of a track, so the network
# comes first and the houses are laid along it. Every road here is a polyline in the room's
# own coordinates with a width, surfaced in `Full_Road` -- the metalled road at Poussin's
# Tomb and the parking lots -- and laid as a ribbon that follows the floor.
#
# The main street follows the line TR1's own `rl1_Path2` already takes, so the town's road
# is where Sierra put a road. It is in three pieces because the corridor pinches: between
# `tudorlong02`'s east face at X 1314 and `tele_pole01` at X 1451 there is 137 units, and a
# two-hundred-wide road does not fit through it.
#
#   name, points, width
ROADS = [
    # Threaded, not drawn freehand: the corridor is narrow and the checks below name every
    # tree, pole and wall the line clips. North of the yard it passes west of the tree at
    # 1641,-1901 and the pole at 1667,-1772; south of it, it runs the 137 units between
    # tudorlong02's east face at X 1314 and tele_pole01 at 1451, which is why that stretch
    # is 120 wide and dead straight.
    ("main street north", [(1545, -2100), (1500, -1950), (1490, -1780), (1470, -1550),
                           (1445, -1250), (1445, -1000), (1380, -750), (1230, -400)], 150.0),
    ("main street", [(1230, -400), (1180, 0), (1250, 400), (1382, 560)], 150.0),
    ("main street pinch", [(1382, 560), (1382, 1020)], 120.0),
    ("main street south", [(1382, 1020), (1330, 1400), (1270, 1800), (1260, 2150)], 160.0),

    # The station approach, west off the main street to the forecourt.
    # Stops at the forecourt's edge at X 1090: past that the apron is the surface, and two
    # of them over the same ground is the flicker this used to have.
    ("station approach", [(1230, -400), (1140, -418), (1090, -428)], 170.0),

    # Two side streets east into the town. The north one runs the 197 units between
    # tudorlong01 and brothel02; the south one clears the tree at 1525,1071.
    ("north side street", [(1330, -622), (1580, -622), (1830, -622), (2060, -622)], 150.0),
    ("south side street", [(1360, 1250), (1640, 1258), (1920, 1266), (2120, 1272)], 150.0),

    # West out of the car park, over the line, to the hamlet. South of the bakery, which it
    # ran through when it left from further north.
    ("crossing lane", [(500, -930), (300, -940), (60, -980), (-150, -1040),
                       (-271, -1080), (-430, -1090)], 150.0),
    ("hamlet lane", [(-430, -1900), (-435, -1090), (-430, -700)], 130.0),
]

# Surfaced areas rather than ribbons, as (name, x0, z0, x1, z1).
#
# The forecourt is the ground Gabriel parks the moped on and the taxi waits in, and it was
# grass and sand. `tr1_station` occupies X 137 to 658, so the apron is east of it; the car
# park is the strip north of that, and it contains `tr1_taxi` at X 501-694, Z -805 to -670,
# which was standing in a field.
APRONS = [
    ("station forecourt", 665.0, -620.0, 1165.0, 430.0),
    ("station car park", 380.0, -950.0, 1165.0, -620.0),
]

# Which roads have houses along them, as (road name, which side, how far back).
#
# +1 is the left of the road's direction of travel and -1 the right. The verge is the gap
# between the kerb and the frontage: 60 units is about a metre and a half of pavement.
FRONTAGES = [
    ("main street north", -1, 60.0),
    ("main street north", +1, 60.0),
    ("main street", -1, 60.0),
    ("main street south", -1, 70.0),
    ("main street south", +1, 70.0),
    ("north side street", -1, 55.0),
    ("north side street", +1, 55.0),
    ("south side street", -1, 55.0),
    ("south side street", +1, 55.0),
    ("hamlet lane", -1, 60.0),

    # A second rank behind the first, set back far enough to clear it. Same machinery: a
    # frontage is just a distance from a road, and the town needs depth as well as a
    # street to line.
    ("main street north", -1, 520.0),
    ("main street north", +1, 520.0),
    ("north side street", -1, 500.0),
    ("north side street", +1, 500.0),
    ("south side street", -1, 500.0),
    ("south side street", +1, 500.0),
    ("main street south", +1, 520.0),
]

# How far apart two buildings stand. Twenty-six units is about sixty centimetres, which is
# a joint rather than a gap: village houses share party walls.
STREET_GAP = 26.0

# Placed by hand, because there is one of each and they answer to the railway rather than
# to a street: (model, x, z, heading).
SINGLES = [
    ("RBN_CZ_CROSSING", -271, -1080, 0),
    ("RBN_CZ_CROSSKEEP", 150, -1230, 0),
]

# How far a building is pushed into the ground, so the grass closes over the footing
# rather than a hairline of daylight showing under a wall.
SINK = 6.0

# Two units was not enough: the floor rolls more than that between samples, and the grass
# came up through the car park in patches. Four, with the grids sampled closer together.
ROAD_LIFT = 4.0

# And every surface after the first gets a little more, because two of them at the same
# height fight. The station approach crosses the forecourt apron and a side street joins
# the main street; both are the same asphalt at the same lift, so both flickered. Six
# tenths of a unit is under a centimetre and a half -- invisible from any camera the room
# has, and enough for the depth buffer to pick a winner.
SURFACE_STEP = 0.6

# What a road is surfaced with. Plain asphalt: `Full_Road` carries a white edge line down
# both sides of the bitmap, which is right for one carriageway and wrong the moment it
# tiles -- a car park came out striped, and two roads side by side had four edge lines
# between them. `ROAD` is the same asphalt with no markings.
ROAD_TEXTURE = "ROAD"

# Where the railway's ballast is, and how high its railhead sits. A lane laid on the floor
# across the track is buried: the floor there is the trackbed at about y -9.5, the sleepers
# and ballast are drawn on top of it, and the railhead is at -6. So a road crossing this
# band comes up to meet the rails instead of following the floor under them.
BALLAST = (-430.0, -115.0)
RAILHEAD = -5.0


YARD = (-153.948, -1123.133, 1294.102, 695.920)


# The avenue. Plane trees down both sides of the road through the village is the single
# most French thing that can be done to a street, and GK3 already has the sprite: a card
# drawn with TREE00 is what `Foliage` reads as a broadleaf, and the modelled-tree pass
# grows it to the size the card was drawn. Cypresses (TREE06) go on the slopes, which is
# what TR1's own seventeen cards already are.
#
# One card per tree and each one authored where it stands, because a grown tree carries
# its own transform and `pos=` is not consulted once the tree pass has claimed a model.
# That is also how 1999 did it -- TR1_FFTREE1 through 17 are seventeen separate models.
#
# (x, z, height in units, species)
TREES = [
    # The road avenue, north of the yard, both sides
    (1180, -1900, 330, "TREE00"), (1180, -1620, 350, "TREE00"),
    (1180, -1340, 320, "TREE00"), (1190, -1060, 345, "TREE00"),
    (1230, -780, 335, "TREE00"),
    (1620, -1980, 300, "TREE00"), (1640, -1700, 315, "TREE00"),
    (1600, -1420, 325, "TREE00"), (1560, -1140, 305, "TREE00"),
    # The avenue south of the yard
    (1140, 700, 340, "TREE00"), (1120, 980, 355, "TREE00"),
    (1080, 1260, 330, "TREE00"), (1040, 1540, 320, "TREE00"),
    (1450, 760, 310, "TREE00"), (1430, 1060, 325, "TREE00"),
    (1400, 1360, 300, "TREE00"),
    # Cypresses on the eastern slope, behind the second rank
    (2350, -1400, 420, "TREE06"), (2300, -1000, 390, "TREE06"),
    (2380, -300, 430, "TREE06"), (2320, 450, 400, "TREE06"),
    (2270, 950, 410, "TREE06"), (2180, -1750, 380, "TREE06"),
    # Cypresses and orchard along the western slope
    (-700, -1250, 400, "TREE06"), (-820, -900, 380, "TREE06"),
    (-760, -500, 415, "TREE06"), (-900, -1600, 390, "TREE06"),
    (-620, 300, 370, "TREE06"),
    # Orchard trees filling the empty green between road and railway, north
    (300, -1950, 290, "TREE00"), (560, -2100, 275, "TREE00"),
    (820, -2150, 300, "TREE00"), (140, -1700, 285, "TREE02"),
    (480, -1450, 270, "TREE02"), (760, -1300, 280, "TREE02"),
    # and south, where the map is emptiest
    (300, 900, 285, "TREE02"), (520, 1200, 300, "TREE02"),
    (180, 1500, 275, "TREE00"), (700, 1750, 295, "TREE00"),
    (1000, 1950, 280, "TREE00"), (-100, 1150, 290, "TREE02"),
    (400, 2050, 270, "TREE02"), (-200, 1800, 285, "TREE06"),
    # A stand behind the station, closing the view up the line
    (150, -2350, 310, "TREE06"), (420, -2400, 330, "TREE06"),
    (-50, -2200, 295, "TREE06"),
]


# --------------------------------------------------------------------------------------
# Blender plumbing
# --------------------------------------------------------------------------------------

def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(path))
    return [o for o in bpy.data.objects if o not in before and o.type == "MESH"]


def select_only(objects):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0] if objects else None


def base_name(material_name):
    """The texture a material names, without the surface index a room's cut put on it.

    ``rc1rghstn#00115`` is surface 115 of RC3 painted with ``rc1rghstn``. The index is
    meaningful only inside the room it was cut from -- ``SceneObjectGlb`` reads it to put a
    triangle back on the surface that carries its lightmap -- and this geometry is leaving
    that room for good. ``GlbReader``, which is what reads ``enhanced/models``, takes the
    material name as the texture name verbatim, so the index has to come off or the engine
    goes looking for a bitmap called ``rc1rghstn#00115``.
    """
    name = material_name.split("#")[0]

    # Blender appends .001 when two materials arrive under one name, which they will as
    # soon as two donors both use rc1rghstn.
    if len(name) > 4 and name[-4] == "." and name[-3:].isdigit():
        name = name[:-4]

    return name


def clean_materials(objects):
    """Puts every face on a bare, picture-less material named for its texture.

    The material that arrived is **renamed** rather than replaced by a new one. Asking
    Blender for a new material called `rc1yellowroof` while the imported one is still
    called `rc1yellowroof` gets you `rc1yellowroof.001`, which is then what the exporter
    writes and what the engine goes looking for in the archives -- and there is no bitmap
    of that name, so the surface draws untextured. Two hundred and forty-one materials
    across the set were in that state before this was written this way.
    """
    canonical = {}

    for obj in objects:
        for slot in obj.material_slots:
            if slot.material is None:
                continue

            name = base_name(slot.material.name)

            if name not in canonical:
                material = slot.material
                material.use_nodes = False

                # Free the name if something else in the file is holding it, or the
                # rename below silently becomes a rename to name.001 instead.
                held = bpy.data.materials.get(name)

                if held is not None and held is not material:
                    held.name = name + "__displaced"

                material.name = name
                canonical[name] = material

            slot.material = canonical[name]

    return canonical


def bounds(objects):
    least = Vector((1e30, 1e30, 1e30))
    most = Vector((-1e30, -1e30, -1e30))

    for obj in objects:
        for corner in obj.bound_box:
            world = obj.matrix_world @ Vector(corner)
            least = Vector((min(least[i], world[i]) for i in range(3)))
            most = Vector((max(most[i], world[i]) for i in range(3)))

    return least, most


def join(objects, name):
    select_only(objects)
    if len(objects) > 1:
        bpy.ops.object.join()
    joined = bpy.context.view_layer.objects.active
    joined.name = name
    return joined


# --------------------------------------------------------------------------------------
# Which way a house looks
# --------------------------------------------------------------------------------------

def front_direction(obj):
    """The direction the building's glass faces, as a unit vector in the ground plane.

    Every window, door and shutter face votes with its own outward normal, weighted by its
    area, and the sum is the front. It is a vote rather than a single reading because a
    French village house puts a window on the return wall as often as not, and one gable
    window would otherwise turn the whole house sideways.

    Returns None when the building has no glass at all, which happens for the walls and
    the barn; the caller falls back on the widest wall.
    """
    mesh = obj.data
    total = Vector((0.0, 0.0, 0.0))

    for polygon in mesh.polygons:
        slot = obj.material_slots[polygon.material_index] if obj.material_slots else None
        material = slot.material.name.lower() if slot and slot.material else ""

        if not any(word in material for word in FRONTAGE):
            continue

        normal = obj.matrix_world.to_3x3() @ polygon.normal
        normal.z = 0.0                       # Blender is Z-up here; flatten to the ground

        if normal.length < 1e-6:
            continue

        total += normal.normalized() * polygon.area

    return total.normalized() if total.length > 1e-6 else None


def widest_wall(obj):
    """The outward direction of the building's largest vertical face."""
    best = None
    area = 0.0

    for polygon in obj.data.polygons:
        normal = obj.matrix_world.to_3x3() @ polygon.normal
        normal.z = 0.0

        if normal.length < 1e-6 or polygon.area <= area:
            continue

        area = polygon.area
        best = normal.normalized()

    return best or Vector((0.0, 1.0, 0.0))


def wall_bins(obj):
    """Vertical-wall area in four bins: +X, +Y, -X, -Y, in Blender's frame."""
    bins = [0.0, 0.0, 0.0, 0.0]

    for polygon in obj.data.polygons:
        normal = obj.matrix_world.to_3x3() @ polygon.normal
        flat = math.hypot(normal.x, normal.y)

        if flat < 1e-6 or abs(normal.z) > flat:
            continue

        angle = math.atan2(normal.y, normal.x)
        bins[int(((angle + math.pi / 4.0) % (2.0 * math.pi)) // (math.pi / 2.0))] += polygon.area

    return bins


OPEN_THRESHOLD = 0.12


def open_sides(obj):
    """Which of the four sides a donor has no wall on. Bin order: +X, +Y, -X, -Y."""
    bins = wall_bins(obj)
    strongest = max(bins)

    if strongest <= 0.0:
        return [0, 1, 2, 3]

    return [i for i, area in enumerate(bins) if area / strongest < OPEN_THRESHOLD]


def turn(obj, quarters):
    """Rotates a piece by a whole number of quarter turns about the vertical."""
    if quarters % 4 == 0:
        return

    obj.rotation_euler = (0.0, 0.0, quarters * math.pi / 2.0)
    bpy.context.view_layer.update()
    select_only([obj])
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)


def orient(obj):
    """Turns a piece so its missing wall faces away and, failing that, its glass faces front.

    A donor in RC2 or RC3 is only ever seen from the street it stands on, so Sierra built
    only the sides that face one: ten of the nineteen are missing a wall. Standing such a
    house in the open needs its hole pointed away from every camera, and the front of a
    street is the one direction that is guaranteed to be looked at -- so the open side
    decides, and the glass only decides when the piece is closed all round.

    An earlier version invented the missing wall by mirroring the opposite one. That is
    what put a detached panel standing in mid-air beside every asymmetric building, took
    the window quads with it, and turned `wstuccohouse` -- which is one wall and nothing
    else -- into two parallel walls with a gap. Not worth it: a blank back is fine, a
    floating wall is not.

    Blender's -Y is the game's +Z, which is heading zero and the way a street's fronts
    look. So the front is bin 3 and the back is bin 1.
    """
    front, back = 3, 1
    holes = open_sides(obj)

    if len(holes) > 1:
        # Two or more walls missing: no turn saves it. build_piece refuses these.
        return "open on %d sides" % len(holes)

    if holes:
        turn(obj, (back - holes[0]) % 4)

        return "back is its open side"

    direction = front_direction(obj)

    if direction is None:
        return "no glass; widest wall forward"

    angle = math.atan2(direction.x, direction.y)
    obj.rotation_euler = (0.0, 0.0, math.pi - angle)
    bpy.context.view_layer.update()
    select_only([obj])
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)

    return "glass forward"



def stand_on_origin(obj):
    """Centres a piece on X and Y and drops its lowest point to Z = 0.

    ``pos={x,y,z}`` in a scene file means *stand here*: the engine centres the model on the
    point in X and Z and puts its lowest point at the point's Y. Building the piece the
    same way means the number in the table is the patch of ground the house stands on, and
    a placement can be read off the floor with ``render-scene --pick``.
    """
    least, most = bounds([obj])
    middle = (least + most) / 2.0

    obj.location = (
        obj.location.x - middle.x,
        obj.location.y - middle.y,
        obj.location.z - least.z,
    )

    bpy.context.view_layer.update()
    select_only([obj])
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)


# --------------------------------------------------------------------------------------
# Assembling a piece out of a donor room
# --------------------------------------------------------------------------------------

GLASS_TOLERANCE = 16.0


def is_glass(material_name):
    return any(word in material_name.lower() for word in FRONTAGE)


def wall_faces(objects):
    """Every solid face of a building, as (centre, unit normal, least, most).

    Glass is excluded: a window backed only by another window is still a window in
    mid-air, and two of them face to face is exactly what a badly claimed pair looks like.
    """
    found = []

    for obj in objects:
        matrix = obj.matrix_world
        rotation = matrix.to_3x3()

        for polygon in obj.data.polygons:
            slot = obj.material_slots[polygon.material_index] if obj.material_slots else None

            if slot and slot.material and is_glass(slot.material.name):
                continue

            normal = rotation @ polygon.normal

            if normal.length < 1e-9:
                continue

            corners = [matrix @ obj.data.vertices[i].co for i in polygon.vertices]
            least = Vector((min(c[i] for c in corners) for i in range(3)))
            most = Vector((max(c[i] for c in corners) for i in range(3)))

            found.append((matrix @ polygon.center, normal.normalized(), least, most))

    return found


def lies_on_a_wall(centre, normal, walls, tolerance=GLASS_TOLERANCE):
    """Whether a quad is actually lying on a solid face, rather than near one.

    Three conditions, and all three are needed. The wall has to face the same way the
    quad does, or it is a different wall of the same building. The quad has to be within
    ``tolerance`` of the wall's *plane*, which is what "laid on it" means. And it has to
    be inside the wall's own extent, or a window is claimed by a wall it merely shares a
    plane with -- the box test this replaced had only the third condition, and loosely,
    which is how windows came to hang behind the station with nothing under them.
    """
    for wall_centre, wall_normal, least, most in walls:
        if abs(wall_normal.dot(normal)) < 0.85:
            continue

        if abs((centre - wall_centre).dot(wall_normal)) > tolerance:
            continue

        if all(least[i] - tolerance <= centre[i] <= most[i] + tolerance for i in range(3)):
            return True

    return False


def trimmings_for(body, candidates):
    """The window, door and shutter quads actually lying on this building.

    A window in RC2 and RC3 is a separate scene object -- a flat quad laid on the wall --
    because that is how the room was cut for lighting, so a recentred body on its own is a
    blank box.

    Claimed by geometry, not by proximity. The test this replaced asked only whether the
    quad's centre fell inside the body's bounding box grown a little, which claims a
    neighbour's window standing in the same airspace and leaves it hanging once the
    building is moved.
    """
    walls = wall_faces(body)
    claimed = []

    for obj in candidates:
        matrix = obj.matrix_world
        rotation = matrix.to_3x3()
        on_wall = False

        for polygon in obj.data.polygons:
            normal = rotation @ polygon.normal

            if normal.length < 1e-9:
                continue

            if lies_on_a_wall(matrix @ polygon.center, normal.normalized(), walls):
                on_wall = True
                break

        if on_wall:
            claimed.append(obj)

    return claimed


def strip_unbacked_glass(obj):
    """Deletes any window or door face with no wall behind it. Returns how many.

    The second half of the same rule, run on the finished piece rather than on the parts.
    `trimmings_for` decides whether a quad belongs to this building; this decides whether
    each individual face of it ended up against something solid, which is the thing that
    is actually visible. A run reports the count and `main` refuses to write a table while
    any survive.
    """
    import bmesh

    walls = wall_faces([obj])

    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    mesh.faces.ensure_lookup_table()

    materials = [slot.material.name if slot.material else "" for slot in obj.material_slots]
    doomed = []

    for face in mesh.faces:
        name = materials[face.material_index] if face.material_index < len(materials) else ""

        if not is_glass(name):
            continue

        if not lies_on_a_wall(face.calc_center_median(), face.normal, walls):
            doomed.append(face)

    if doomed:
        bmesh.ops.delete(mesh, geom=doomed, context="FACES")
        mesh.to_mesh(obj.data)
        obj.data.update()

    mesh.free()

    return len(doomed)


def build_furniture(workspace, name, room, body_name):
    """A bench, a bin, a stack of crates: recentred and stood on the origin, and nothing else.

    None of the building gates run on these. A bench has no walls, so `open_sides` calls it
    open on four and refuses it; it has no glass, so there is no front to measure. What it
    needs is the same as any other borrowed geometry: its surface index off the material
    name, and its own coordinates replaced by the origin.
    """
    reset_scene()

    path = os.path.join(workspace, "enhanced", "scenes", room, "original", body_name + ".glb")

    if not os.path.exists(path):
        return None, f"{room}/{body_name} is not in the workspace"

    imported = import_glb(path)

    if not imported:
        return None, f"{room}/{body_name} holds no mesh"

    clean_materials(imported)
    piece = join(imported, name)
    stand_on_origin(piece)

    return piece, f"{room}/{body_name}"


def build_piece(workspace, name, room, body_name):
    """Imports one donor building with its glass, cleans it, stands it on the origin."""
    reset_scene()

    directory = os.path.join(workspace, "enhanced", "scenes", room, "original")
    body_path = os.path.join(directory, body_name + ".glb")

    if not os.path.exists(body_path):
        return None, f"{room}/{body_name} is not in the workspace"

    body = import_glb(body_path)

    if not body:
        return None, f"{room}/{body_name} holds no mesh"

    # Everything else in the room that could be glass on this building.
    trimmings = []

    for entry in sorted(os.listdir(directory)):
        if not entry.lower().endswith(".glb"):
            continue

        stem = os.path.splitext(entry)[0]

        if stem == body_name:
            continue

        if not any(word in stem.lower() for word in FRONTAGE):
            continue

        imported = import_glb(os.path.join(directory, entry))
        kept = trimmings_for(body, imported)

        for obj in imported:
            if obj not in kept:
                bpy.data.objects.remove(obj, do_unlink=True)

        trimmings.extend(kept)

    everything = body + trimmings
    clean_materials(everything)

    piece = join(everything, name)
    holes = len(open_sides(piece))

    # Two or more walls missing and no turn hides them all. wstuccohouse is one wall and
    # nothing else; stood in the open it reads as a flat, which is worse than no building.
    if holes > 1:
        return None, f"{room}/{body_name} has {4 - holes} of 4 walls; not a building"

    note = orient(piece)

    # And again on the assembled piece, because trimmings_for answers "does this quad
    # belong to this building" and this answers "did this face end up on something",
    # which is the question the player's eye asks.
    stripped = strip_unbacked_glass(piece)

    stand_on_origin(piece)

    return piece, (f"{note}, {len(trimmings)} trimmings"
                   + (f", {stripped} unbacked dropped" if stripped else ""))


def build_terrace(workspace, name, piece_names, overlap, cache):
    """Butts several houses together into one model, shoulder to shoulder.

    They are laid along X with their fronts still facing +Y, so the whole row can be turned
    by one heading in the placement table. Each house keeps its own ridge height and its
    own depth, which is what a real village terrace looks like and what a single long block
    never does.
    """
    reset_scene()

    built = []
    cursor = 0.0

    for index, piece_name in enumerate(piece_names):
        room, body = PIECES[piece_name]
        path = cache.get(piece_name)

        if path is None or not os.path.exists(path):
            return None, f"{piece_name} was not built"

        imported = import_glb(path)

        if not imported:
            return None, f"{piece_name} holds no mesh"

        house = join(imported, f"{name}_{index}")
        least, most = bounds([house])
        width = most.x - least.x

        # Butted, not spaced: each house is pushed `overlap` units into its neighbour so
        # that no daylight shows between two walls at any angle the camera can reach.
        house.location.x = cursor + width / 2.0
        cursor += width - overlap

        # A row of identical depths reads as one extruded block, so each house steps back
        # a little from the frontage -- alternating, and never enough to open a gap.
        house.location.y = (-9.0 if index % 2 else 0.0)

        bpy.context.view_layer.update()
        select_only([house])
        bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
        built.append(house)

    terrace = join(built, name)
    strip_unbacked_glass(terrace)

    # Again, and it matters. Importing four GLBs that each name `rc1rghstndoor1` gives
    # Blender `rc1rghstndoor1`, `.001`, `.002` and `.003`, and joining keeps all four. The
    # engine reads a material name as a texture name verbatim, so a terrace built without
    # this asks the archives for a bitmap called `rc1rghstndoor1.001` and gets nothing.
    clean_materials([terrace])
    stand_on_origin(terrace)

    return terrace, f"{len(piece_names)} houses, {len(terrace.data.polygons)} faces"


def build_variant(name, source_path, mirrored):
    """Copies a piece under a new name, optionally mirrored across its own frontage.

    Mirroring is a negative scale on X, which leaves every face wound the wrong way round
    and every normal pointing into the building. Back-face culling is on for the rooms, so
    a mirrored house that has not had its winding put back is a house you can see straight
    through -- and only from some angles, which is the worst way for it to be wrong.
    """
    reset_scene()

    imported = import_glb(source_path)

    if not imported:
        return None, "holds no mesh"

    obj = join(imported, name)

    if mirrored:
        obj.scale = (-1.0, 1.0, 1.0)
        select_only([obj])
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)

        obj.data.flip_normals()

    strip_unbacked_glass(obj)
    clean_materials([obj])
    stand_on_origin(obj)

    return obj, ("mirrored" if mirrored else "copy")


# --------------------------------------------------------------------------------------
# The level crossing
# --------------------------------------------------------------------------------------

def box(name, size, at, material):
    """One textured box, built at `at` with size (across, through, up) in Blender's frame."""
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=at)
    obj = bpy.context.view_layer.objects.active
    obj.name = name
    obj.scale = size
    select_only([obj])
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=True)

    material_block = bpy.data.materials.get(material) or bpy.data.materials.new(material)
    material_block.use_nodes = False
    obj.data.materials.append(material_block)

    return obj


def build_crossing(name):
    """A level crossing: two barriers, their posts and the stock fencing.

    Frames, because getting them wrong is what this has cost so far. The track runs on the
    game's Z; the road crosses it, so the road runs on X. Blender's Y becomes the game's
    -Z on export, and the placement gives the model heading 0.

    **A barrier stops traffic, so it lies across the direction of travel.** Traffic runs
    along X, so the arm spans **Y** -- the road's width -- from a post that stands clear of
    the rails on X. Building the arm long on X instead lays it straight down the track and
    over both rails, which is what it did.

    No deck: the lane ribbon crosses here and follows the ground, so the road surface is
    the crossing surface. A rigid slab on rolling ballast digs in at one corner.
    """
    reset_scene()

    parts = []

    ROAD_HALF = 78.0        # half the lane's width, on Y
    CLEAR = 175.0           # how far the posts stand from the crossing centre, on X

    for side, x in (("w", -CLEAR), ("e", CLEAR)):
        # The post stands at one edge of the road; the arm reaches across it.
        parts.append(box(f"{name}_post_{side}", (12.0, 12.0, 104.0),
                         (x, ROAD_HALF, 52.0), "trnpostwood"))
        parts.append(box(f"{name}_arm_{side}", (9.0, 2.0 * ROAD_HALF, 10.0),
                         (x, 0.0, 80.0), "trnrampwood"))
        # A counterweight behind the pivot, outside the road.
        parts.append(box(f"{name}_weight_{side}", (14.0, 26.0, 14.0),
                         (x, ROAD_HALF + 26.0, 80.0), "trnpostwood"))

    # Stock fencing runs along the line, so it lies on Y -- and stops at the road, which is
    # the gap the crossing is.
    for side, x in (("w", -235.0), ("e", 235.0)):
        for end_name, centre in (("n", ROAD_HALF + 105.0), ("s", -ROAD_HALF - 105.0)):
            for rail, height in (("hi", 62.0), ("lo", 34.0)):
                parts.append(box(f"{name}_fence_{side}{end_name}{rail}", (6.0, 180.0, 6.0),
                                 (x, centre, height), "trnpostwood"))

            parts.append(box(f"{name}_fencepost_{side}{end_name}", (8.0, 8.0, 72.0),
                             (x, centre, 36.0), "trnpostwood"))

    crossing = join(parts, name)
    stand_on_origin(crossing)

    return crossing, f"{len(crossing.data.polygons)} faces"


def panel(name, width, height, at, material):
    """One quad facing -Y, for a door or a window laid on a wall.

    A box will not do. A door built as a box has four faces that point along the wall
    rather than out of it, and the glass audit counts every one of them as a door with
    nothing behind it -- correctly, because that is what they are.
    """
    mesh = bpy.data.meshes.new(name)
    x, y, z = at
    mesh.from_pydata(
        [(x - width / 2.0, y, z - height / 2.0), (x + width / 2.0, y, z - height / 2.0),
         (x + width / 2.0, y, z + height / 2.0), (x - width / 2.0, y, z + height / 2.0)],
        [],
        [(0, 3, 2, 1)])
    mesh.update()

    uv = mesh.uv_layers.new(name="UVMap")
    for loop, coordinate in zip(mesh.loops, [(0.0, 1.0), (0.0, 0.0), (1.0, 0.0), (1.0, 1.0)]):
        uv.data[loop.index].uv = coordinate

    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)

    block = bpy.data.materials.get(material) or bpy.data.materials.new(material)
    block.use_nodes = False
    obj.data.materials.append(block)

    return obj


def build_keeper(name):
    """The crossing keeper's cabin: one storey of rubble stone under a pantile roof."""
    reset_scene()

    parts = [
        box(f"{name}_walls", (150.0, 120.0, 130.0), (0.0, 0.0, 65.0), "rc1rghstn"),
        box(f"{name}_roof", (168.0, 138.0, 16.0), (0.0, 0.0, 136.0), "rc1redroof2"),
        box(f"{name}_stack", (26.0, 26.0, 50.0), (48.0, 0.0, 165.0), "rc1rghstn"),
        panel(f"{name}_door", 46.0, 84.0, (-30.0, -61.0, 42.0), "Old_Door"),
        panel(f"{name}_win", 44.0, 44.0, (44.0, -61.0, 82.0), "rc1wstuccowin1"),
    ]

    keeper = join(parts, name)
    stand_on_origin(keeper)

    return keeper, f"{len(keeper.data.polygons)} faces"


# --------------------------------------------------------------------------------------
# Trees
# --------------------------------------------------------------------------------------

def build_road(name, points, width, ground, lift, step=35.0):
    """Lays a lane along a polyline, following the ground under it.

    A ribbon of quads, each corner sampled off `tr1_floor` and lifted clear of it, painted
    with the forecourt's own `rl1_Dirt` -- which is the sand Gabriel parks the moped on, so
    the lane and the yard are the same surface.

    Sampled every `step` units rather than only at the polyline's corners: the floor rolls,
    and a ribbon drawn corner to corner cuts through every rise between them.
    """
    reset_scene()

    # Walk the polyline at a fixed spacing so the ribbon follows the ground, not the chord.
    walked = []

    for (x0, z0), (x1, z1) in zip(points, points[1:]):
        span = math.hypot(x1 - x0, z1 - z0)
        pieces = max(1, int(span / step))

        for i in range(pieces):
            t = i / pieces
            walked.append((x0 + (x1 - x0) * t, z0 + (z1 - z0) * t))

    walked.append(points[-1])

    verts = []
    faces = []

    for i, (x, z) in enumerate(walked):
        # The direction the lane runs here, from its neighbours, so corners mitre.
        ahead = walked[min(i + 1, len(walked) - 1)]
        behind = walked[max(i - 1, 0)]
        dx, dz = ahead[0] - behind[0], ahead[1] - behind[1]
        length = math.hypot(dx, dz) or 1.0
        nx, nz = -dz / length * width / 2.0, dx / length * width / 2.0

        for sx, sz in ((x - nx, z - nz), (x + nx, z + nz)):
            height = ground.at(sx, sz) + lift

            if BALLAST[0] <= sx <= BALLAST[1]:
                height = max(height, RAILHEAD)

            # Blender is Z-up and the exporter sends its +Y to the game's -Z.
            verts.append((sx, -sz, height))

        if i:
            base = (i - 1) * 2
            faces.append((base, base + 1, base + 3, base + 2))

    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()

    uv = mesh.uv_layers.new(name="UVMap")
    run = 0.0

    for i, face in enumerate(mesh.polygons):
        here = run
        run += step / 160.0

        for loop, coordinate in zip(face.loop_indices,
                                    [(0.0, here), (1.0, here), (1.0, run), (0.0, run)]):
            uv.data[loop].uv = coordinate

    road = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(road)

    material = bpy.data.materials.get(ROAD_TEXTURE) or bpy.data.materials.new(ROAD_TEXTURE)
    material.use_nodes = False
    road.data.materials.append(material)

    return road


def build_tree_card(name, x, z, height, sprite, ground):
    """One foliage card, standing where it stands.

    Two quads crossed at the trunk, which is how GK3 draws the trees it does not billboard,
    painted with the sprite whose species `Foliage` reads. The modelled-tree pass then
    measures the card and grows a tree to the size it was drawn -- so the only thing that
    has to be right here is the size and the texture.

    Authored at the room's own coordinates and not at the origin, because a grown tree
    carries its own transform and `pos=` is not consulted once the tree pass has claimed a
    model. TR1_FFTREE1 through 17 are built the same way.

    The quads are given their vertices rather than being scaled primitives. A plane added
    by ``primitive_plane_add`` lies in XY with no thickness, so a scale of
    (width, 1, height) puts the height on the axis the plane does not have and the card
    comes out flat on the ground -- which is what happened, and which reads as a tree
    that simply did not grow rather than as a tree lying down.
    """
    reset_scene()

    width = height * 0.62
    base = ground(x, z)

    parts = []

    for turn in (0.0, math.pi / 2.0):
        mesh = bpy.data.meshes.new(f"{name}_{int(turn * 100)}")

        # Standing in Blender's XZ, base on z = 0, so the card's foot is its own origin.
        mesh.from_pydata(
            [(-width / 2.0, 0.0, 0.0), (width / 2.0, 0.0, 0.0),
             (width / 2.0, 0.0, height), (-width / 2.0, 0.0, height)],
            [],
            [(0, 1, 2, 3)])
        mesh.update()

        uv = mesh.uv_layers.new(name="UVMap")
        for loop, coordinate in zip(mesh.loops, [(0.0, 1.0), (1.0, 1.0), (1.0, 0.0), (0.0, 0.0)]):
            uv.data[loop.index].uv = coordinate

        quad = bpy.data.objects.new(mesh.name, mesh)
        bpy.context.collection.objects.link(quad)
        quad.rotation_euler = (0.0, 0.0, turn)
        select_only([quad])
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
        parts.append(quad)

    card = join(parts, name)

    material = bpy.data.materials.get(sprite) or bpy.data.materials.new(sprite)
    material.use_nodes = False
    card.data.materials.clear()
    card.data.materials.append(material)

    # Blender is Z-up and the exporter turns it into the game's Y-up by sending Blender's
    # +Y to the game's -Z. So the game's Z is written negated here, and the card's foot
    # goes straight on the ground rather than half of it under.
    card.location = (x, -z, base)
    select_only([card])
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)

    return card


# --------------------------------------------------------------------------------------
# The ground
# --------------------------------------------------------------------------------------

def reach(size_x, size_z, heading):
    """How far a piece extends across the street once it has been turned.

    The X half-width of `footprint`, doubled: the number the packer needs to stand a
    building's front on a line, and the reason it is a function rather than the piece's
    own depth is that a heading a few degrees off square swings a long terrace well past
    it.
    """
    radians = math.radians(heading)

    return size_x * abs(math.cos(radians)) + size_z * abs(math.sin(radians))


def footprint(size_x, size_z, x, z, heading):
    """Where a placement lands on the ground plan, as (minx, minz, maxx, maxz).

    The model is built centred on X and Z, so a heading turns it about the point the
    placement names. Only the axis-aligned box of the turned box is wanted, which for the
    right angles the layout uses is exact.
    """
    radians = math.radians(heading)
    cos = abs(math.cos(radians))
    sin = abs(math.sin(radians))

    half_x = (size_x * cos + size_z * sin) / 2.0
    half_z = (size_x * sin + size_z * cos) / 2.0

    return x - half_x, z - half_z, x + half_x, z + half_z


def corners_of(cx, cz, length, depth, heading):
    """The four ground corners of a placement, in order, turned by its heading."""
    radians = math.radians(heading)
    cos, sin = math.cos(radians), math.sin(radians)

    # StandOn turns local +Z to the heading, so local +X goes to (cos, -sin).
    ax, az = cos * length / 2.0, -sin * length / 2.0
    bx, bz = sin * depth / 2.0, cos * depth / 2.0

    return [(cx - ax - bx, cz - az - bz), (cx + ax - bx, cz + az - bz),
            (cx + ax + bx, cz + az + bz), (cx - ax + bx, cz - az + bz)]


def box_corners(box):
    """A box as four corners, so it can be tested against a turned placement."""
    return [(box[0], box[1]), (box[2], box[1]), (box[2], box[3]), (box[0], box[3])]


def shapes_overlap(a, b, slack=0.0):
    """Whether two convex ground shapes overlap, by separating axis.

    The axis-aligned hull is not good enough here. A house square to a bending street and a
    road cell square to the map have hulls that overlap wherever the street turns, and
    testing those refused most of the frontage -- so the packer was given slack to get any
    houses at all, and the final check, which had none, then refused what it placed.

    `slack` shrinks both shapes, so a shared wall is not an overlap.
    """
    for shape in (a, b):
        for i in range(len(shape)):
            x0, z0 = shape[i]
            x1, z1 = shape[(i + 1) % len(shape)]
            nx, nz = z1 - z0, x0 - x1
            length = math.hypot(nx, nz)

            if length < 1e-9:
                continue

            nx, nz = nx / length, nz / length

            a_lo = min(nx * x + nz * z for x, z in a)
            a_hi = max(nx * x + nz * z for x, z in a)
            b_lo = min(nx * x + nz * z for x, z in b)
            b_hi = max(nx * x + nz * z for x, z in b)

            if a_hi - slack <= b_lo or b_hi - slack <= a_lo:
                return False

    return True


def overlap(a, b, slack=0.0):
    """How far two ground-plan boxes overlap, as (on x, on z). Zero when they do not."""
    on_x = min(a[2], b[2]) - max(a[0], b[0]) - slack
    on_z = min(a[3], b[3]) - max(a[1], b[1]) - slack

    return (on_x, on_z) if on_x > 0.0 and on_z > 0.0 else (0.0, 0.0)


def existing_buildings(workspace):
    """The ground-plan boxes of the buildings TR1 already has.

    Anything over a hundred units across and a hundred tall, which is the same gate
    `extract-scenes` uses to tell a building from a barrel. The floor, the cliffs, the
    scrape decals and the wire runs are excluded by name: they cover the whole map, so
    every placement would collide with them and the check would say nothing.
    """
    directory = os.path.join(workspace, "enhanced", "scenes", "TR1", "original")
    spread = {"tr1_floor", "tr1_scrapes", "tr1_cliffs", "tr1_fftreesshadow",
              "tr1_pole_wires", "tr1_rrtracks", "tr1_exittoroad"}
    found = {}

    for entry in sorted(os.listdir(directory)):
        stem = os.path.splitext(entry)[0]

        if not entry.lower().endswith(".glb") or stem in spread or "fftree" in stem:
            continue

        reset_scene()
        objects = import_glb(os.path.join(directory, entry))

        if not objects:
            continue

        least, most = bounds(objects)

        # Imported Z-up: Blender Y is the game's Z, and the importer has already negated
        # it, so the game's Z runs from -most.y to -least.y.
        across = most.x - least.x
        tall = most.z - least.z

        # Anything standing up that a building could be put through. This did ask for 150
        # units on both horizontal axes, which excluded the telephone poles -- and two
        # houses were then placed straight over one. A pole blocks its own footprint and
        # little else, so it costs the street almost nothing.
        through = -least.y - -most.y

        if across >= 40.0 and through >= 40.0 and tall >= 100.0:
            found[stem] = (least.x, -most.y, most.x, -least.y)

    reset_scene()

    return found


def free_intervals(blocked, band, z_from, z_to, clearance):
    """The stretches of a street with nothing standing in them yet.

    Everything already placed whose own X band overlaps this street's is subtracted from
    (z_from, z_to), grown by `clearance` at both ends so that a new building never touches
    an old one. Computing the free room first, rather than walking forward and stopping at
    the first thing in the way, is what lets a short house be tried where a terrace will
    not fit -- which is most of this street, because the four buildings Sierra put along
    the east line leave gaps of two to nine hundred units and the terraces are thirteen
    hundred and up.
    """
    busy = []

    for low_x, low_z, high_x, high_z in blocked:
        if high_x <= band[0] or low_x >= band[1]:
            continue

        busy.append((low_z - clearance, high_z + clearance))

    busy.sort()

    free = []
    cursor = z_from

    for low, high in busy:
        if low > cursor:
            free.append((cursor, min(low, z_to)))

        cursor = max(cursor, high)

        if cursor >= z_to:
            break

    if cursor < z_to:
        free.append((cursor, z_to))

    return [(a, b) for a, b in free if b > a]


ROAD_SURFACES = ("rl1_path2", "rl1_dirt", "depotplank")


def road_boxes(workspace, cell=60.0):
    """The ground the room already surfaces as road, as boxes the packer must keep off.

    TR1 has a road: `rl1_Path2` runs the length of the map and `rl1_Dirt` is the station
    forecourt. Nothing stopped a terrace being packed straight over it, and the east
    frontage was -- which is why the town had no road to be seen. Rasterised into cells
    rather than kept as triangles because the packer walks this list for every placement
    it tries.
    """
    reset_scene()
    objects = import_glb(os.path.join(
        workspace, "enhanced", "scenes", "TR1", "original", "tr1_floor.glb"))

    cells = set()

    for obj in objects:
        mesh = obj.data
        mesh.calc_loop_triangles()
        matrix = obj.matrix_world

        for tri in mesh.loop_triangles:
            slot = obj.material_slots[tri.material_index] if obj.material_slots else None
            material = slot.material.name.lower() if slot and slot.material else ""

            if not any(material.startswith(word) for word in ROAD_SURFACES):
                continue

            for index in tri.vertices:
                corner = matrix @ mesh.vertices[index].co
                cells.add((round(corner.x / cell), round(-corner.y / cell)))

    reset_scene()

    return [(i * cell - cell / 2.0, j * cell - cell / 2.0,
             i * cell + cell / 2.0, j * cell + cell / 2.0) for i, j in cells]


def audit_glass(path):
    """Re-reads a written GLB and counts window faces with no wall behind them.

    The third look at the same thing, deliberately: `trimmings_for` decides ownership on
    the donor, `strip_unbacked_glass` checks the assembled piece, and this checks the file
    that will actually ship, after the exporter has had it. It should always be nought,
    and `main` refuses to write the table when it is not.
    """
    reset_scene()
    imported = import_glb(path)

    if not imported:
        return 0

    obj = join(imported, "audit")
    walls = wall_faces([obj])
    materials = [slot.material.name if slot.material else "" for slot in obj.material_slots]
    unbacked = 0

    for polygon in obj.data.polygons:
        name = materials[polygon.material_index] if polygon.material_index < len(materials) else ""

        if not is_glass(name):
            continue

        normal = obj.matrix_world.to_3x3() @ polygon.normal

        if normal.length < 1e-9:
            continue

        if not lies_on_a_wall(obj.matrix_world @ polygon.center, normal.normalized(), walls):
            unbacked += 1

    reset_scene()

    return unbacked


def room_trees(workspace, trunk=45.0):
    """Where TR1's own seventeen foliage cards stand, as narrow boxes at their trunks.

    They are separate `.MOD` files carrying a node transform, so a card's own vertices say
    nothing about where it is. Read because a lane was routed straight through
    `TR1_FFTREE10` and a cypress came up through the middle of the road -- one of Sierra's,
    which this pass cannot move.

    The box is the trunk, not the crown: a road may run under branches.
    """
    directory = os.path.join(workspace, "normalized", "models")

    if not os.path.isdir(directory):
        return []

    found = []

    for entry in sorted(os.listdir(directory)):
        if not entry.upper().startswith("TR1_FFTREE") or not entry.lower().endswith(".glb"):
            continue

        reset_scene()
        imported = import_glb(os.path.join(directory, entry))

        if not imported:
            continue

        least, most = bounds(imported)
        x = (least.x + most.x) / 2.0
        z = -(least.y + most.y) / 2.0
        found.append((x - trunk, z - trunk, x + trunk, z + trunk))

    reset_scene()

    return found


def walkable_test(workspace):
    """Reads TR1's walk bitmap and answers whether an actor can stand on a point.

    White is walkable: Gabriel arrives on white at FR_MAP and walks to the station door
    across it. That is worth having because a bench where an actor can walk is a bench an
    actor walks through -- seven of the first eight were.

    Returns a function, or one that always says no when the bitmap cannot be read, so a
    missing file loses the check rather than the furniture.
    """
    path = os.path.join(workspace, "normalized", "textures", "TR1WLKBNDS.png")

    if not os.path.exists(path):
        return lambda x, z: False

    image = bpy.data.images.load(path)
    width, height = image.size
    pixels = list(image.pixels)

    # size={1448.050049,1819.052734} offset={-153.947998,1123.133057}, and WalkBoundary
    # maps it as world = u * size - offset.
    def walkable(x, z):
        i = int((x + 153.948) / 1448.05 * width)
        j = int((z + 1123.133) / 1819.053 * height)

        if not (0 <= i < width and 0 <= j < height):
            return False

        # Blender loads images bottom-up; the bitmap's rows run the other way.
        at = ((height - 1 - j) * width + i) * 4

        return all(pixels[at + c] > 0.9 for c in range(3))

    return walkable


def protected(workspace, margin=70.0):
    """Everything the story needs where it is, as boxes nothing may be built over.

    Every object TR1 binds a noun to -- the taxi, the barrels, the doors, the sign, the
    poles, the buildings that shipped -- plus the spots the scene file names for an actor
    to stand on. A building over any of them is a puzzle the player cannot reach, which is
    the one failure this pass is not allowed to cause.

    Roads are deliberately *not* checked against these: a road under the taxi is the point.
    """
    sif = os.path.join(workspace, "normalized", "scenes", "TR1", "TR1.SIF")
    directory = os.path.join(workspace, "enhanced", "scenes", "TR1", "original")
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

    # A spot an actor stands on needs room around it, not just its own point.
    boxes.extend((x - 120.0, z - 120.0, x + 120.0, z + 120.0) for x, z in spots)

    return boxes


def build_apron(name, x0, z0, x1, z1, ground, lift, step=40.0):
    """A surfaced area rather than a ribbon: the forecourt and the car park.

    Same rules as a road -- every corner sampled off the floor and lifted clear of it, and
    the same `Full_Road` surface -- but gridded over a rectangle, because a station
    forecourt is a place rather than a line.
    """
    reset_scene()

    across = max(2, int((x1 - x0) / step) + 1)
    through = max(2, int((z1 - z0) / step) + 1)

    verts = []
    faces = []

    for i in range(across):
        x = x0 + (x1 - x0) * i / (across - 1)

        for j in range(through):
            z = z0 + (z1 - z0) * j / (through - 1)
            verts.append((x, -z, ground.at(x, z) + lift))

    for i in range(across - 1):
        for j in range(through - 1):
            a = i * through + j
            faces.append((a, a + 1, a + through + 1, a + through))

    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()

    uv = mesh.uv_layers.new(name="UVMap")

    for face in mesh.polygons:
        for loop in face.loop_indices:
            corner = mesh.vertices[mesh.loops[loop].vertex_index].co
            uv.data[loop].uv = ((corner.x - x0) / 160.0, (-corner.y - z0) / 160.0)

    apron = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(apron)

    material = bpy.data.materials.get(ROAD_TEXTURE) or bpy.data.materials.new(ROAD_TEXTURE)
    material.use_nodes = False
    apron.data.materials.append(material)

    return apron


def lane_boxes(step=50.0, named=False):
    """The ground the roads and aprons cover, as boxes.

    `road_boxes` reads the room's own road surface out of the floor; this is the other
    half. The cell is sized from the road's own direction -- half its width across, half a
    step along -- and not as a square of its full width, which for a road running
    north-south claims eighty units of ground to either end of every sample and reports
    buildings it comes nowhere near.
    """
    cells = []

    for label, x0, z0, x1, z1 in APRONS:
        cells.append((label, (x0, z0, x1, z1)) if named else (x0, z0, x1, z1))

    for label, points, width in ROADS:
        half = width / 2.0

        for x, z, tx, tz in walk_polyline(points, step):
            # Across the road is the normal; along it is one step.
            reach_x = half * abs(tz) + step / 2.0 * abs(tx)
            reach_z = half * abs(tx) + step / 2.0 * abs(tz)
            box = (x - reach_x, z - reach_z, x + reach_x, z + reach_z)
            cells.append((label, box) if named else box)

    return cells


def blank_slots(path):
    """The material slots of whatever is in a GLB, for asking what it is painted with."""
    reset_scene()
    imported = import_glb(path)
    slots = [slot for obj in imported for slot in obj.material_slots
             if slot.material is not None]

    return slots


def walk_polyline(points, step):
    """Every `step` along a polyline, as (x, z, unit tangent)."""
    out = []

    for (x0, z0), (x1, z1) in zip(points, points[1:]):
        span = math.hypot(x1 - x0, z1 - z0)

        if span < 1e-6:
            continue

        tx, tz = (x1 - x0) / span, (z1 - z0) / span

        for i in range(int(span / step) + 1):
            out.append((x0 + tx * i * step, z0 + tz * i * step, tx, tz))

    return out


def road_named(name):
    for road, points, width in ROADS:
        if road == name:
            return points, width

    return None, None


def pack_frontage(frontage, sizes, blocked, roads, ground, step=25.0):
    """Lays houses along one side of a road, facing it.

    The street is the road, so a bend in the road is a bend in the frontage. Walking the
    polyline and offsetting sideways is what gives that; the earlier version packed along
    a straight line of fixed X and could only ever produce a grid.

    A house is placed with its front square to the road, which is what `heading` means:
    `StandOn` turns a model's local +Z to the heading it is given, and `orient` has already
    put the front on local +Z.
    """
    name, side, verge = frontage
    points, width = road_named(name)

    if points is None:
        return []

    return pack_along(points, width, side, verge, list(sizes), sizes, blocked, roads,
                      ground, step)


def pack_along(points, width, side, verge, available, sizes, blocked, roads, ground,
               step=25.0):
    """The walk itself. Separated so the frontage list stays readable."""
    marks = walk_polyline(points, step)
    placed = []
    index = 0
    i = 0

    while i < len(marks):
        x, z, tx, tz = marks[i]

        # The outward normal of this side of the road, and the way a house here looks.
        nx, nz = -tz * side, tx * side
        facing = math.degrees(math.atan2(-nx, -nz)) % 360.0

        choice = None

        for offset in range(len(available)):
            piece = available[(index + offset) % len(available)]
            length, depth = sizes[piece]

            if length > (len(marks) - i) * step:
                continue

            # Half the piece further along, then out past the kerb to the frontage line.
            # The road's own half-width has to be in this: without it the house is set out
            # from the centreline by the verge alone and stands in the carriageway, which
            # the road check then refuses -- so every frontage came out empty.
            ahead = marks[min(i + int(length / 2 / step), len(marks) - 1)]
            back = width / 2.0 + verge + depth / 2.0
            cx = ahead[0] + nx * back
            cz = ahead[1] + nz * back

            shape = corners_of(cx, cz, length, depth, facing)

            if any(ground.off_map(px, pz) for px, pz in shape):
                continue

            if any(shapes_overlap(shape, box_corners(other), 4.0) for other in blocked):
                continue

            if any(shapes_overlap(shape, box_corners(cell), 4.0) for cell in roads):
                continue

            box = footprint(length, depth, cx, cz, facing)
            choice = (piece, offset, cx, cz, box, length)
            break

        if choice is None:
            i += 1
            continue

        piece, offset, cx, cz, box, length = choice
        index += offset + 1
        placed.append((piece, round(cx), round(cz), round(facing)))
        blocked.append(box)
        i += max(1, int((length + STREET_GAP) / step))

    return placed



class Ground:
    """The height of TR1's floor under any point, read off the room's own floor object.

    Held in the game's own frame rather than Blender's, and the conversion is the whole
    reason this is a class. The glTF importer turns glTF's (x, y, z) into Blender's
    (x, -z, y): the game's Z is the **negative** of Blender's Y, not Blender's Y. Reading
    it as the positive samples the floor mirrored north-to-south, which puts every
    building at the height of the ground on the far side of the station and rejects, as
    off the map, most of the ground the town actually stands on.
    """

    def __init__(self, path):
        reset_scene()
        objects = import_glb(path)
        self.triangles = []

        for obj in objects:
            mesh = obj.data
            mesh.calc_loop_triangles()
            matrix = obj.matrix_world

            for tri in mesh.loop_triangles:
                corners = [matrix @ mesh.vertices[i].co for i in tri.vertices]
                self.triangles.append(
                    tuple((corner.x, corner.z, -corner.y) for corner in corners))

        reset_scene()

    def at(self, x, z, default=0.0):
        """The highest surface under (x, z), in the game's frame."""
        best = None

        for a, b, c in self.triangles:
            d = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2])

            if abs(d) < 1e-9:
                continue

            u = ((b[2] - c[2]) * (x - c[0]) + (c[0] - b[0]) * (z - c[2])) / d
            v = ((c[2] - a[2]) * (x - c[0]) + (a[0] - c[0]) * (z - c[2])) / d
            w = 1.0 - u - v

            if u < -1e-6 or v < -1e-6 or w < -1e-6:
                continue

            y = u * a[1] + v * b[1] + w * c[1]

            if best is None or y > best:
                best = y

        return default if best is None else best

    def off_map(self, x, z):
        """Whether there is no floor under a point at all."""
        return self.at(x, z, None) is None

    def lowest(self, box, steps=5):
        """The lowest ground under a footprint, sampled on a grid.

        `pos` puts a model's lowest point at the Y it names, so a building given the
        height under its centre stands on the centre and hangs off the downhill corner.
        Taking the lowest instead buries the uphill corner a little, which nobody sees,
        instead of leaving a shadow under the whole house, which everybody does.
        """
        low_x, low_z, high_x, high_z = box
        found = None

        for i in range(steps):
            for j in range(steps):
                x = low_x + (high_x - low_x) * i / (steps - 1)
                z = low_z + (high_z - low_z) * j / (steps - 1)
                y = self.at(x, z, None)

                if y is not None and (found is None or y < found):
                    found = y

        return found if found is not None else self.at(
            (low_x + high_x) / 2.0, (low_z + high_z) / 2.0)


# --------------------------------------------------------------------------------------
# Output
# --------------------------------------------------------------------------------------

def export_glb(obj, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    select_only([obj])
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_yup=True,
        export_normals=True,
        export_texcoords=True,

        # No pictures. The engine reads the material *name* and resolves the bitmap out of
        # the archives, which already hold every texture this town is painted with.
        export_image_format="NONE")


TABLE_HEADER = """\
# The town of Couiza, which GK3 never modelled.
#
# GENERATED by tools/blender/build_couiza.py. Edit the layout there, not here.
#
# TR1 is called Couiza and written as a town -- Gabriel's own line on the buildings is
# "I'm not sure what all these buildings are, but I don't think they're connected to the
# station" -- and what was built is eleven boxes on open grass, in the half-timbered kit
# Sierra made for Rennes-les-Bains. Every line below adds a building, a wall or a tree
# outside the walk bitmap and outside the camera's shell, out of the Languedoc masonry
# the game already carries for Rennes-le-Chateau. Nothing that shipped is moved or
# repainted.
#
# This is not cut content and is not in CutContent.txt: nobody at Sierra wrote, recorded
# or modelled it. It is applied only when the geometry it names is actually present --
# see SceneDressing -- so an installation without the enhanced content packs is exactly
# the game as it shipped, with no diagnostics about models that are not there.
#
#   append <FILE.SIF> <SECTION> <LINE>
#
# `append` rather than `place`, because a facade wants the line written out in full: the
# type, the noun, and a pos with a heading. It is idempotent -- a line already in the
# section is left alone -- and it does nothing at all if the section is not there.
#
# type=prop, and it has to be. GK3's `type=scene` does not mean "part of the scenery": it
# means the name refers to an object already inside the room's BSP, and only `prop` and
# `gasprop` load a model file at all (SceneLoader.IsBakedIn). A facade declared
# `type=scene` is declared, counted, and never drawn.
#
# y is sampled off tr1_floor, so it is the ground the room actually has. pos means
# "stand here": the model is centred on the point in X and Z with its lowest point at Y.
#
# Every building answers to OTR_BUILDINGS, which TR1_ALL.NVC already gives a LOOK rule
# and a recorded line, so the town needs no dialogue of its own.

[FACADES]
"""


def write_table(path, facades, trees):
    os.makedirs(os.path.dirname(path), exist_ok=True)

    lines = [TABLE_HEADER]
    lines.extend(facades)
    lines.append("\n[TREES]\n")
    lines.append("# Foliage cards, and they carry no pos on purpose: each one is authored at\n"
                 "# the coordinates it stands at, because the modelled-tree pass measures a\n"
                 "# card and grows a tree carrying its own transform, and a model the tree\n"
                 "# pass has claimed never consults pos. TR1's own seventeen are built the\n"
                 "# same way. type=prop matches those seventeen; no noun, because a tree in\n"
                 "# this room is scenery and has never had one.\n")
    lines.extend(trees)

    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("".join(lines))


# --------------------------------------------------------------------------------------

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--table", default=None,
                        help="where Dressing.txt goes; default is beside the engine")
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

    # 1. The houses, one at a time, each with the glass that belongs to it.
    for name, (room, body) in PIECES.items():
        piece, note = build_piece(workspace, name, room, body)

        if piece is None:
            report.append(f"  {name:22s} SKIPPED  {note}")
            continue

        keep(name, piece, note)

    # 2. The terraces, built out of the houses.
    for name, (pieces, butt) in TERRACES.items():
        cache = {p: os.path.join(out, p + ".glb") for p in pieces}
        terrace, note = build_terrace(workspace, name, pieces, butt, cache)

        if terrace is None:
            report.append(f"  {name:22s} SKIPPED  {note}")
            continue

        keep(name, terrace, note)

    for name, (room, body) in FURNITURE.items():
        piece, note = build_furniture(workspace, name, room, body)

        if piece is None:
            report.append(f"  {name:22s} SKIPPED  {note}")
            continue

        keep(name, piece, note)

    # 3. The ground, which the packer needs before it can put anything on it.
    ground = Ground(os.path.join(
        workspace, "enhanced", "scenes", "TR1", "original", "tr1_floor.glb"))

    # 4. What each piece measures, which is what the packer lays out. Taken from the file
    #    that was written rather than from the object that wrote it, so that a piece the
    #    exporter changed is measured as it will be read.
    sizes = {}

    for name, path in made.items():
        reset_scene()
        imported = import_glb(path)

        if imported:
            least, most = bounds(imported)
            sizes[name] = (most.x - least.x, most.y - least.y)

    reset_scene()

    # 5. The layout. Every building that already stands in TR1 blocks the band it is in,
    #    and so does everything the packer has already placed.
    shipped = existing_buildings(workspace)
    blocked = [box for box in shipped.values()]
    roads = road_boxes(workspace) + lane_boxes()
    standing = room_trees(workspace)

    # A lane may not be routed through one of the room's own trees: this pass cannot move
    # them, so the tree wins and the road has to bend. Refused rather than warned, because
    # what it looks like is a cypress growing out of the tarmac.
    # A road may not run through a wall, but it may run past a pole: a lamppost standing in
    # a car park is what a car park looks like. Anything under 150 units on either side is
    # street furniture rather than a building.
    obstacles = [("a tree of the room's own", box) for box in standing]
    obstacles += [(name, box) for name, box in shipped.items()
                  if box[2] - box[0] >= 150.0 and box[3] - box[1] >= 150.0]

    through = [
        f"{label} runs through {what} at "
        f"{(box[0] + box[2]) / 2:.0f},{(box[1] + box[3]) / 2:.0f}"
        for what, box in obstacles
        for label, cell in lane_boxes(named=True)
        if overlap(cell, box) != (0.0, 0.0)]

    if through:
        raise SystemExit("The lanes are not clear:\n  " + "\n  ".join(sorted(set(through))))

    # Nothing may be built over a thing the player can click on, or over a spot the story
    # puts somebody on. That is the whole of "the quest still works": the objects keep the
    # coordinates they shipped with and the town is laid out around them.
    blocked.extend(protected(workspace))

    # Houses only. Naming the exclusions by pattern let the benches into the street packer,
    # which lined the main road with them.
    furniture = set(FURNITURE)
    houses = [p for p in sizes
              if p in PIECES or p in TERRACES
              if p not in furniture]

    layout = []

    for frontage in FRONTAGES:
        placed = pack_frontage(frontage, {p: sizes[p] for p in houses},
                               blocked, roads, ground)
        layout.extend(placed)
        rank = " back" if frontage[2] > 200.0 else ""
        report.append(
            f"  {frontage[0] + (' left' if frontage[1] > 0 else ' right') + rank:32s} "
            f"{len(placed)} building(s)")

    layout.extend(SINGLES)

    # Street furniture, placed last so it is checked against everything already standing.
    # A copy per spot, for the same reason the houses get one: two placements naming one
    # model leave only the last of them in the room.
    for index, (piece, x, z, heading) in enumerate(FURNITURE_SPOTS):
        base = piece.rstrip("0123456789") if piece not in sizes else piece

        if base not in sizes:
            report.append(f"  {piece:22s} SKIPPED  {base} was not built")
            continue

        layout.append((base, x, z, heading))

    # 6. A placement must name a model no other placement names. SceneDefinition.MergeModels
    #    keys a room's models by name -- TR1 has both a room file and timeblock files, so
    #    the merge runs -- and the second of two identical names silently replaces the
    #    first. The symptom in the room is a building that is simply not there, which is
    #    why the packer is allowed to repeat a piece and the naming is done here instead.
    #
    #    Every second copy is mirrored, which is worth having anyway: a street whose garden
    #    wall is the same fifty faces six times over reads as wallpaper.
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

    # 7. The level crossing, which SINGLES names and nothing builds until here.
    for name, build in (("RBN_CZ_CROSSING", build_crossing),
                        ("RBN_CZ_CROSSKEEP", build_keeper)):
        obj, note = build(name)
        keep(name, obj, note)

    # 8. The lanes, and then the trees. Both are authored where they stand and carry no
    #    pos: a lane follows the ground and a grown tree brings its own transform.
    road_lines = []

    for index, (label, points, width) in enumerate(ROADS, start=1):
        name = f"RBN_CZ_ROAD{index:02d}"
        road = build_road(name, points, width, ground,
                          ROAD_LIFT + (len(APRONS) + index - 1) * SURFACE_STEP)

        if not args.dry_run:
            export_glb(road, os.path.join(out, name + ".glb"))

        made[name] = os.path.join(out, name + ".glb")
        road_lines.append(f"append TR1.SIF MODELS model={name}, type=prop\n")
        report.append(f"  {name:22s} {len(road.data.polygons):5d} faces  {label}")

    # 9. The placements, with the ground sampled under each one.
    facade_lines = []
    boxes = {}

    shapes = {}

    for model, x, z, heading in final:
        if model in sizes:
            box = footprint(sizes[model][0], sizes[model][1], x, z, heading)
            boxes[(model, x, z)] = box
            shapes[(model, x, z)] = corners_of(
                x, z, sizes[model][0], sizes[model][1], heading)
            y = ground.lowest(box) - SINK
        else:
            y = ground.at(x, z) - SINK

        # A bench on the forecourt stands on the forecourt. The surfacing is lifted clear
        # of the floor, so anything put on it at floor height is buried to the ankles.
        if any(cell[0] <= x <= cell[2] and cell[1] <= z <= cell[3] for cell in roads):
            y = ground.at(x, z) + ROAD_LIFT

        facade_lines.append(
            f"append TR1.SIF MODELS model={model}, noun=OTR_BUILDINGS, type=prop, "
            f"pos={{{x},{y:.1f},{z}}}, heading={heading}\n")

    # Furniture and street trees may stand on a road -- that is where a bench goes -- but
    # not where an actor can walk, because nothing stops him walking through them.
    walkable = walkable_test(workspace)

    # 10. The trees, after the layout because they are checked against it. A tree in the
    #     road is the same fault as a house in it, and the trees were the one thing
    #     nothing checked -- a cypress came up through the middle of the crossing lane.
    #     The test is the trunk's own footing, not the crown, which is meant to overhang.
    TRUNK = 40.0

    # Aprons sit lowest and the roads stack above them, so a road crossing open surfacing
    # reads as a road rather than as a flicker.
    for index, (label, x0, z0, x1, z1) in enumerate(APRONS, start=1):
        name = f"RBN_CZ_APRON{index:02d}"
        apron = build_apron(name, x0, z0, x1, z1, ground,
                            ROAD_LIFT + (index - 1) * SURFACE_STEP)

        if not args.dry_run:
            export_glb(apron, os.path.join(out, name + ".glb"))

        made[name] = os.path.join(out, name + ".glb")
        road_lines.append(f"append TR1.SIF MODELS model={name}, type=prop\n")
        report.append(f"  {name:22s} {len(apron.data.polygons):5d} faces  {label}")

    tree_lines = []
    grown = 0
    nudged = 0
    refused = []

    # Street trees first: they stand on the surfacing on purpose, so the road check is not
    # theirs. Everything else about them is checked the same way.
    for x, z, height, sprite in STREET_TREES:
        against = [(model, box) for (model, _, _), box in boxes.items()]
        against += [(name, box) for name, box in shipped.items()]
        against += [("a tree of the room's own", box) for box in standing]
        foot = (x - 25.0, z - 25.0, x + 25.0, z + 25.0)

        blocking = next(
            (why for why, box in against if overlap(foot, box) != (0.0, 0.0)), None)

        if blocking is None and any(walkable(px, pz) for px, pz in
                                    ((x - 25, z - 25), (x + 25, z - 25),
                                     (x - 25, z + 25), (x + 25, z + 25), (x, z))):
            blocking = "ground an actor can walk on"

        if blocking is not None:
            refused.append(f"street tree {x},{z} in {blocking}")
            continue

        grown += 1
        name = f"RBN_CZ_TREE{grown:02d}"
        card = build_tree_card(name, x, z, height, sprite, ground.at)

        if not args.dry_run:
            export_glb(card, os.path.join(out, name + ".glb"))

        made[name] = os.path.join(out, name + ".glb")
        tree_lines.append(f"append TR1.SIF MODELS model={name}, type=prop\n")

    for x, z, height, sprite in TREES:
        against = [("a road", cell) for cell in roads]
        against += [(model, box) for (model, _, _), box in boxes.items()]
        against += [(name, box) for name, box in shipped.items()]
        against += [("a tree of the room's own", box) for box in standing]

        moved = None

        for dx, dz in ((0, 0), (110, 0), (-110, 0), (0, 110), (0, -110),
                       (170, 0), (-170, 0), (0, 170), (0, -170),
                       (130, 130), (-130, 130), (130, -130), (-130, -130)):
            foot = (x + dx - TRUNK, z + dz - TRUNK, x + dx + TRUNK, z + dz + TRUNK)

            if any(overlap(foot, box) != (0.0, 0.0) for _, box in against):
                continue

            if ground.off_map(x + dx, z + dz):
                continue

            moved = (x + dx, z + dz)
            break

        if moved is None:
            blocking = next(
                (why for why, box in against if overlap(foot, box) != (0.0, 0.0)), "the map")
            refused.append(f"{x},{z} in {blocking}")
            continue

        if moved != (x, z):
            nudged += 1

        x, z = moved
        grown += 1
        name = f"RBN_CZ_TREE{grown:02d}"
        card = build_tree_card(name, x, z, height, sprite, ground.at)

        if not args.dry_run:
            export_glb(card, os.path.join(out, name + ".glb"))

        made[name] = os.path.join(out, name + ".glb")
        tree_lines.append(f"append TR1.SIF MODELS model={name}, type=prop\n")

    report.append(f"  {'trees':22s} {grown} cards"
                  + (f", {nudged} nudged clear" if nudged else "")
                  + (f", {len(refused)} refused ({'; '.join(refused)})" if refused else ""))

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

    for (model, x, z), mine in boxes.items():
        if not any(model.startswith(piece) for piece in FURNITURE):
            continue

        corners = [(mine[0], mine[1]), (mine[2], mine[1]),
                   (mine[0], mine[3]), (mine[2], mine[3]), (x, z)]

        if any(walkable(px, pz) for px, pz in corners):
            complaints.append(
                f"{model} at {x},{z} stands where an actor can walk, so he walks through it")

    # A building on a road is a fault; a bench on the forecourt is the point, and so is the
    # level crossing.
    on_purpose = set(FURNITURE) | {"RBN_CZ_CROSSING"}

    for (model, x, z), mine in boxes.items():
        if any(model.startswith(allowed) for allowed in on_purpose):
            continue

        shape = shapes.get((model, x, z))

        if shape and any(shapes_overlap(shape, box_corners(cell), 8.0) for cell in roads):
            complaints.append(f"{model} at {x},{z} stands on a road")

    for (model, x, z), mine in boxes.items():
        if any(model.startswith(piece) for piece in on_purpose):
            continue

        on_x, on_z = overlap(mine, YARD)

        if on_x > 40.0 and on_z > 40.0:
            complaints.append(
                f"{model} at {x},{z} reaches {on_x:.0f}x{on_z:.0f} into the walk bitmap")

    # Every window and door in every file that will ship, checked once more against the
    # geometry it is meant to be lying on.
    if not args.dry_run:
        floating = {name: audit_glass(path) for name, path in sorted(made.items())}
        floating = {name: count for name, count in floating.items() if count}

        if floating:
            complaints.extend(
                f"{name} ships {count} window or door face(s) with no wall behind them"
                for name, count in floating.items())
        else:
            report.append(f"  {'glass audit':22s} every window and door is on a wall")

    if complaints:
        print("Couiza")
        print("\n".join(report))
        raise SystemExit(
            "The layout is not clear:\n  " + "\n  ".join(sorted(complaints)))

    table = args.table or os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        "src", "GK3Reborn.Engine", "Assets", "Story", "Dressing.txt")

    # Anything with this prefix that this run did not write is from a layout that no
    # longer exists, and leaving it there is not harmless: `pack-content` takes the whole
    # directory, so a stale terrace ships, and it was a stale one that kept sixteen
    # materials named `rc1rghstn.001` in the set long after the code that made them was
    # gone. The prefix is the boundary -- nothing else in enhanced/models is touched.
    swept = 0

    if not args.dry_run:
        kept = {os.path.basename(path).lower() for path in made.values()}

        for entry in sorted(os.listdir(out)):
            if entry.lower().startswith("rbn_cz_") and entry.lower() not in kept:
                os.remove(os.path.join(out, entry))
                swept += 1

    if swept:
        report.append(f"  {'swept':22s} {swept} file(s) from an older layout")

    if not args.dry_run:
        write_table(table, facade_lines, road_lines + tree_lines)

    print("Couiza")
    print("\n".join(report))
    print(f"  {len(facade_lines)} facade placements, {len(tree_lines)} tree placements")
    print(f"  table -> {table}")


if __name__ == "__main__":
    main()
