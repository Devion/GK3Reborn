"""Grows the modelled trees that stand in for GK3's foliage cards.

    blender --background --factory-startup --python tools/blender/grow_trees.py -- \
        --workspace path/to/ContentWorkspace [--species NAME ...] [--variants N] \
        [--dry-run]

Writes one GLB per species and variant into ``enhanced/trees`` and describes the set in
``manifests/trees.json``. Every tree is grown in a normalised frame: its trunk base is at
the origin, it is exactly one unit tall, and the manifest records how wide it came out.
The engine scales that to whatever the card it replaces was, so a species is grown once
and stands in a hundred places.

The trees are textured with the game's own bitmaps and nothing else. A leaf card takes a
sub-rectangle of the sprite it replaces, so the needles on a modelled spruce are the same
pixels the flat spruce was drawn with. That is deliberate: the lightmaps, the skyboxes and
the distant card trees were all authored against those colours, and a tree painted with
anything else reads as a tree from another game standing in this one.
"""

import argparse
import json
import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

# Blender grows the tree up +Z; the exporter turns that into the game's +Y.
UP = Vector((0.0, 0.0, 1.0))


# --------------------------------------------------------------------------------------
# Species
# --------------------------------------------------------------------------------------

# A leaf card is one whole tile of a drawn foliage texture, mirrored at random so that a
# thousand of them on one tree are not a thousand copies of the same picture. The four
# entries are (u0, v0, u1, v1) with the axes flipped or not; glTF's v grows downwards, and
# a mirrored card is still the right way up.
#
# It used to be a sub-rectangle of the original sprite, and that is worth recording as a
# thing that does not work. A GK3 tree sprite is a whole tree seen from one side, so a
# rectangle cut out of the middle of it is almost entirely opaque: every card rendered as a
# solid green box with a hard edge, and a spruce built from two hundred of them was a heap
# of boxes rather than a tree. Foliage has to be mostly holes, which is what
# `tools/foliage/make_cards.py` draws.
MIRRORS = [
    (0.0, 0.0, 1.0, 1.0),
    (1.0, 0.0, 0.0, 1.0),
    (0.0, 1.0, 1.0, 0.0),
    (1.0, 1.0, 0.0, 0.0),
]


def tiles(atlas, levels):
    """The mirrors of every occlusion tile, darkest level last.

    ``make_cards.py`` writes the same clump four times over at four brightnesses, tiled two
    by two. A leaf takes the tile its own occlusion has earned, so the crown carries a real
    gradient from a dark heart to a lit shell without the engine's vertex having anywhere
    to keep a per-leaf number. See AO_LEVELS there for why the picture holds it.
    """
    if atlas <= 1 or levels <= 1:
        return [MIRRORS]

    step = 1.0 / atlas
    out = []

    for level in range(levels):
        u0 = (level % atlas) * step
        v0 = (level // atlas) * step
        out.append([
            (u0 + u * step, v0 + v * step, u0 + s * step, v0 + t * step)
            for u, v, s, t in MIRRORS
        ])

    return out


# Which sprites a species stands in for. The engine matches a card's texture against
# these, so a foliage bitmap that is not named here keeps its flat card - which is the
# right default, since the background strips (TREEGROUP01, TILEDTREES) are whole hillsides
# of distant trees on one quad and there is no single tree in them to model.
SPECIES = {
    # The spruce that PINE2 draws: a single straight leader, branches in whorls that
    # shorten towards the top, needles carried on fans rather than on modelled twigs.
    #
    # Grown as a canopy rather than as a whole tree. PINE2 is a *leaves* card - the rooms
    # that place it carry the trunk in their own geometry, WOD's ten pines standing on the
    # ten trunks of `wod_pinetrunks` - so a spruce that brought its own bole would put a
    # second one through the first. The normalised unit is therefore the crown, and the
    # engine fits it to the card's box without needing to know where the ground is.
    "spruce": {
        "kind": "conifer",
        "bark": "TRUNK01",
        "leaf": "PINE2",
        "sprites": ["PINE2", "PINE2FLAT", "TALLPINE", "ARMPINE", "TREE03", "TREE04", "TREE05"],
        "canopy": True,
        "bowed": True,
        "leafSpread": 0.34,
        "leafAspect": 0.62,
        "trunkRadius": 0.014,
        "trunkTopRadius": 0.0020,
        "trunkSegments": 16,
        "trunkLean": 0.010,
        "crownRadius": 0.26,
        "crownBottom": 0.05,
        "crownTop": 1.00,
        "crownPower": 1.10,
        "crownTip": 0.13,
        "whorls": 17,
        "branchesPerWhorl": (6, 8),
        "branchRise": -0.13,
        "branchTipRise": 0.26,
        "branchSides": 3,
        "branchSegments": 3,
        "branchRadius": 0.35,
        "fansPerBranch": (7, 10),
        "fanScale": 0.26,
        "fanDroop": 0.12,
    },
    # The rounded broadleaf of TREE00: a short bole, three or four rising limbs, leaf
    # clumps hung on the twig ends.
    "broadleaf": {
        "kind": "broadleaf",
        "bark": "TRUNK01",
        "leaf": "TREE00",
        "sprites": ["TREE00", "TREE01", "TREE02", "BUSHYTREESIDE1", "BUSHYTREETOP1"],
        "canopy": False,
        "bowed": True,
        "leafSpread": 0.40,
        "leafAspect": 0.86,
        "trunkRadius": 0.045,
        "trunkTopRadius": 0.024,
        "trunkSegments": 7,
        "trunkLean": 0.030,
        "boleHeight": 0.32,
        "limbs": (4, 6),
        "limbSplits": 4,
        "limbSpread": 0.60,
        "limbRise": 0.72,
        "branchSides": 5,
        "branchSegments": 3,
        "crownHeight": 0.68,
        "crownRadius": 0.29,
        "clumpsPerTwig": (3, 5),
        "clumpScale": 0.082,
        "crownFill": 260,
    },
    # The maple of MAPLESIDE1, which is the same tree grown wider and lower.
    "maple": {
        "kind": "broadleaf",
        "bark": "TRUNK01",
        "leaf": "MAPLE",
        # MAPLE1TRILEAF is leaves on real geometry rather than on a card, and it is the
        # texture the rooms paint their modelled maples with - RC1's hotel tree, CEM's
        # three, RC2's and RC4's. Twenty-two objects across the corpus draw it, and until
        # it was named here every one of them kept its 1999 leaves under a grown tree.
        "sprites": ["MAPLESIDE1", "MAPLETOP1", "MAPLE", "MAPLE1TRILEAF"],
        "canopy": False,
        "bowed": True,
        "leafSpread": 0.40,
        "leafAspect": 0.86,
        "trunkRadius": 0.048,
        "trunkTopRadius": 0.026,
        "trunkSegments": 7,
        "trunkLean": 0.040,
        "boleHeight": 0.28,
        "limbs": (4, 6),
        "limbSplits": 4,
        "limbSpread": 0.70,
        "limbRise": 0.66,
        "branchSides": 5,
        "branchSegments": 3,
        "crownHeight": 0.72,
        "crownRadius": 0.35,
        "clumpsPerTwig": (3, 5),
        "clumpScale": 0.088,
        "crownFill": 280,
    },
    # WOODTREE3's dark, dense broadleaf: a heavier crown on a shorter bole.
    "darkbroadleaf": {
        "kind": "broadleaf",
        "bark": "TRUNK01",
        "leaf": "WOODTREE3",
        "sprites": ["WOODTREE3", "MAGENTREE"],
        "canopy": False,
        "bowed": True,
        "leafSpread": 0.40,
        "leafAspect": 0.86,
        "trunkRadius": 0.050,
        "trunkTopRadius": 0.028,
        "trunkSegments": 7,
        "trunkLean": 0.035,
        "boleHeight": 0.26,
        "limbs": (5, 6),
        "limbSplits": 4,
        "limbSpread": 0.64,
        "limbRise": 0.70,
        "branchSides": 5,
        "branchSegments": 3,
        "crownHeight": 0.74,
        "crownRadius": 0.32,
        "clumpsPerTwig": (3, 5),
        "clumpScale": 0.082,
        "crownFill": 300,
    },
    # The columnar conifer of TREE06 - narrower and taller-crowned than the spruce.
    "cypress": {
        "kind": "conifer",
        "bark": "TRUNK01",
        "leaf": "TREE06",
        "sprites": ["TREE06"],
        "canopy": True,
        "bowed": True,
        "leafSpread": 0.32,
        "leafAspect": 0.62,
        "trunkRadius": 0.016,
        "trunkTopRadius": 0.003,
        "trunkSegments": 16,
        "trunkLean": 0.008,
        "crownRadius": 0.17,
        "crownBottom": 0.03,
        "crownTop": 1.00,
        "crownPower": 0.85,
        "crownTip": 0.22,
        "whorls": 21,
        "branchesPerWhorl": (6, 8),
        "branchRise": -0.02,
        "branchTipRise": 0.40,
        "branchSides": 3,
        "branchSegments": 3,
        "branchRadius": 0.30,
        "fansPerBranch": (6, 8),
        "fanScale": 0.30,
        "fanDroop": 0.06,
    },
}


# What a tree grown for the far half of a wood is made of.
#
# A hillside is a hundred and seventy trees, and at four thousand triangles apiece that is
# most of a scene's budget spent on scenery the player never walks into. So a species is
# grown twice: once in full, and once with fewer and larger pieces. The far tree keeps the
# silhouette that says which species it is - the count of whorls, the taper of the crown -
# and gives up the detail inside it, which is the part that stops being visible first.
#
# What has to be held while it is thinned is the **leaf area**, not the leaf count: a card
# taken away is only free if the ones left grow enough to cover the hole it leaves. The
# figure to watch is the count times the square of the scale, against the near tree's.
FAR = {
    "trunkSegments": 0.55,
    "branchSegments": 0.70,
}

# A broadleaf's crown is a cloud of clumps scattered through a volume, so it survives being
# thinned hard: a third of the clumps at two and a bit times the size covers the same
# volume, and what is lost is the grain inside a mass that reads as one mass anyway.
FAR_BROADLEAF = {
    "limbSplits": 0.70,
    "clumpsPerTwig": 0.34,
    "clumpScale": 2.30,
    "crownFill": 0.10,
}

# A conifer's is not a cloud but a shell of sprays hung along whorled branches, and there
# the *count is the shape*. These factors used to be the broadleaf's - whorls 0.55,
# branches 0.70, fans 0.34 at 2.10 times the size - which left an eighth of the sprays
# carrying four times the area apiece: 56% of the near tree's leaf area, and none of the
# missing half where the eye looks for it. A spruce came out as a ragged column of ferns
# with sky between the whorls rather than as a cone, and a free camera in a backdrop wood
# sees nothing else, because only the nearest forty-eight trees are grown in full.
#
# A spray is sized against the **crown radius**, not against the branch it hangs on, so
# `fanScale` is the one factor that cannot be spent freely: at 2.10 a single spray was over
# half the crown wide and the outline it drew was a star. 1.40 is as far as it goes without
# the silhouette coarsening, and the rest of the area is bought back in count. That puts
# both conifers back at parity - spruce 513 sprays at 1.96 times the area against the near
# tree's 964, cypress 535 against 1,059 - which is where the broadleaf already stood, and
# it still costs a fifth of the near tree: spruce 2,130 triangles against 9,956.
FAR_CONIFER = {
    "whorls": 0.85,
    "branchesPerWhorl": 0.85,
    "fansPerBranch": 0.72,
    "fanScale": 1.40,
}


def far(spec):
    """The far build of one species, thinned by the rules its kind is built from."""
    return thin(spec, FAR | (FAR_CONIFER if spec["kind"] == "conifer" else FAR_BROADLEAF))


def thin(spec, factors):
    """A species grown with fewer, larger pieces.

    The far tree is also grown **flat**. Bowing a card is four times the triangles for a
    curve across a leaf clump, and a clump on a hillside a hundred metres away is two
    pixels: nothing there can show it, and the whole point of the far tree is to keep the
    silhouette and pay for nothing else.
    """
    out = dict(spec)

    for key, factor in factors.items():
        if key not in out:
            continue

        value = out[key]
        if isinstance(value, tuple):
            out[key] = tuple(max(1, int(round(part * factor))) for part in value)
        elif isinstance(value, bool):
            continue
        elif isinstance(value, int):
            out[key] = max(1, int(round(value * factor)))
        else:
            out[key] = value * factor

    out["bowed"] = False
    return out


# --------------------------------------------------------------------------------------
# Geometry
# --------------------------------------------------------------------------------------


class Leaf:
    """One clump of foliage: a bowed card, and how deep in the crown it sits."""

    __slots__ = ("centre", "right", "up", "facing", "bow", "twist", "mirror", "level")

    def __init__(self, centre, right, up, facing, bow, twist, mirror):
        self.centre = centre
        self.right = right
        self.up = up
        self.facing = facing
        self.bow = bow
        self.twist = twist
        self.mirror = mirror
        self.level = 0


class Growth:
    """The tubes and leaves a tree is made of, before any of it becomes a mesh."""

    def __init__(self):
        self.tubes = []    # (points, radii, sides)
        self.leaves = []   # Leaf

    def tube(self, points, radii, sides):
        if len(points) >= 2:
            self.tubes.append((points, radii, sides))

    def leaf(self, leaf):
        self.leaves.append(leaf)


def _jitter(rng, amount):
    return Vector((rng.uniform(-amount, amount),
                   rng.uniform(-amount, amount),
                   rng.uniform(-amount, amount)))


def _perpendicular(direction):
    """Any unit vector at right angles to a direction."""
    axis = UP if abs(direction.normalized().dot(UP)) < 0.9 else Vector((1.0, 0.0, 0.0))
    out = direction.cross(axis)
    return out.normalized() if out.length > 1e-6 else Vector((1.0, 0.0, 0.0))


def _limb(growth, rng, start, direction, length, radius, sides, segments, curve):
    """Lays a curving, tapering tube and returns where its tip ended up."""
    points = [start.copy()]
    radii = [radius]
    at = start.copy()
    heading = direction.normalized()

    for step in range(segments):
        heading = (heading + curve / segments + _jitter(rng, 0.04)).normalized()
        at = at + heading * (length / segments)
        points.append(at.copy())
        radii.append(radius * (1.0 - (step + 1) / segments) ** 0.7 + radius * 0.08)

    growth.tube(points, radii, sides)
    return at, heading


def _fan(growth, rng, centre, along, size, spec, droop, facing=None):
    """Hangs one needle fan or leaf clump.

    Two things about this are the whole difference between a crown and a heap of stickers,
    and both were learnt the hard way.

    **The card is bowed, not flat.** Every quad is a three-by-three patch pushed out along
    its own normal into a shallow dome or saddle. It catches light across itself rather than
    all at once, which is what stops a clump reading as a printed sticker — and, because no
    two bowed patches can lie in the same plane, it is also what stops the crown flickering.

    **Its plane is turned freely, never about a shared axis.** The old fan built its frame
    from the branch it hung on: ``right`` along the twig and ``up`` at right angles to it,
    spun about the twig. So every clump on one twig lay in a plane *containing* that twig,
    and two of them a half turn apart were exactly coplanar — at the same point on the
    branch, drawn over each other, fighting for the same depth. That is the flicker a crown
    used to show whenever the camera moved. A leaf now faces where it is asked to face, out
    of the crown, with the spread and the roll drawn independently.
    """
    if along.length < 1e-6:
        along = UP.copy()

    # Outwards from the trunk unless the caller knows better - the crown fill does, because
    # it knows where the middle of the tree is.
    out = facing if facing is not None and facing.length > 1e-6 else along
    normal = (out.normalized() - UP * droop + _jitter(rng, spec["leafSpread"]))

    if normal.length < 1e-6:
        normal = UP.copy()

    normal.normalize()

    right = Matrix.Rotation(rng.uniform(0.0, math.tau), 3, normal) @ _perpendicular(normal)
    up = normal.cross(right).normalized() * (size * spec["leafAspect"])

    growth.leaf(Leaf(
        centre, right.normalized() * size, up, normal,
        rng.uniform(0.20, 0.55) * rng.choice((-1.0, 1.0)) if spec["bowed"] else 0.0,
        rng.uniform(-0.35, 0.35) if spec["bowed"] else 0.0,
        rng.randrange(len(MIRRORS))))


def grow_conifer(spec, rng):
    growth = Growth()
    lean = Vector((rng.uniform(-1.0, 1.0), rng.uniform(-1.0, 1.0), 0.0)) * spec["trunkLean"]

    trunk, radii = [], []
    for step in range(spec["trunkSegments"] + 1):
        along = step / spec["trunkSegments"]
        trunk.append(Vector((0.0, 0.0, along)) + lean * along * along)
        radii.append(spec["trunkRadius"] * (1.0 - along) ** 0.8 + spec["trunkTopRadius"])
    growth.tube(trunk, radii, 6)

    def on_trunk(height):
        along = min(max(height, 0.0), 1.0) * spec["trunkSegments"]
        low = int(along)
        high = min(low + 1, spec["trunkSegments"])
        return trunk[low].lerp(trunk[high], along - low)

    bottom, top = spec["crownBottom"], spec["crownTop"]
    for whorl in range(spec["whorls"]):
        height = bottom + (top - bottom) * (whorl / max(1, spec["whorls"] - 1))
        # Branches shorten towards the leader, which is the whole of a conifer's outline.
        # Never quite nothing. A pure power curve reaches zero well below the leader and
        # leaves the top third of the tree a bare pole, which is what the first spruce
        # came out as: a real conifer carries a narrow crown all the way to its tip.
        along = (height - bottom) / (top - bottom)
        reach = spec["crownRadius"] * (
            spec["crownTip"] + (1.0 - spec["crownTip"]) * (1.0 - along) ** spec["crownPower"])
        base = on_trunk(height)
        count = rng.randint(*spec["branchesPerWhorl"])
        offset = rng.uniform(0.0, math.tau)

        for index in range(count):
            angle = offset + math.tau * index / count + rng.uniform(-0.16, 0.16)
            out = Vector((math.cos(angle), math.sin(angle), spec["branchRise"]))
            length = reach * rng.uniform(0.82, 1.12)
            tip, _ = _limb(
                growth, rng, base, out, length,
                spec["trunkRadius"] * spec["branchRadius"] * (1.0 - height * 0.7) + 0.001,
                spec["branchSides"], spec["branchSegments"],
                UP * spec["branchTipRise"])

            fans = rng.randint(*spec["fansPerBranch"])
            for fan in range(fans):
                along = (fan + 0.6) / (fans + 0.2)
                # Sized against the crown, not against the branch. Tied to the branch, the
                # sprays on a spruce's lowest whorl came out a third of the tree wide and
                # the crown read as a fern rather than as needles.
                _fan(growth, rng,
                     base.lerp(tip, along) + _jitter(rng, length * 0.10),
                     (tip - base),
                     spec["crownRadius"] * spec["fanScale"] * rng.uniform(0.75, 1.15),
                     spec, spec["fanDroop"])

        # A crowning fan, so the leader does not end in a bare spike.
        if whorl == spec["whorls"] - 1:
            _fan(growth, rng, on_trunk(1.0) - UP * 0.03,
                 Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0.35)),
                 spec["crownRadius"] * 0.5, spec, 0.0)

    return growth


def grow_broadleaf(spec, rng):
    growth = Growth()
    lean = Vector((rng.uniform(-1.0, 1.0), rng.uniform(-1.0, 1.0), 0.0)) * spec["trunkLean"]
    bole = spec["boleHeight"]

    trunk, radii = [], []
    for step in range(spec["trunkSegments"] + 1):
        along = step / spec["trunkSegments"]
        trunk.append(Vector((0.0, 0.0, bole * along)) + lean * along * along)
        radii.append(spec["trunkRadius"] * (1.0 - along) + spec["trunkTopRadius"] * along)
    growth.tube(trunk, radii, 8)

    crown_top = bole + spec["crownHeight"]

    # The middle of the crown, decided before anything is hung in it. Every leaf is turned
    # to face away from this point, so the mass has a shell that catches the light and an
    # inside that does not - which is the whole of what makes a crown read as a volume
    # rather than as a heap of stickers. Facing along the twig instead, as this used to,
    # points half the leaves back into the tree.
    centre = Vector((0.0, 0.0, bole + spec["crownHeight"] * 0.48)) + lean

    def branch(start, heading, length, radius, depth):
        tip, out = _limb(
            growth, rng, start, heading, length, radius,
            spec["branchSides"], spec["branchSegments"], UP * 0.10 + _jitter(rng, 0.10))

        if depth == 0 or tip.z > crown_top:
            for _ in range(rng.randint(*spec["clumpsPerTwig"])):
                at = tip + _jitter(rng, length * 0.35)
                _fan(growth, rng, at, out,
                     spec["clumpScale"] * rng.uniform(0.8, 1.25), spec, 0.12,
                     facing=at - centre)
            return

        for _ in range(rng.randint(2, 3)):
            aside = _perpendicular(out) * rng.uniform(-1.0, 1.0)
            spin = Matrix.Rotation(rng.uniform(0.0, math.tau), 3, out)
            heading_next = (out * rng.uniform(0.55, 0.85)
                            + spin @ aside * spec["limbSpread"]
                            + UP * spec["limbRise"] * 0.35).normalized()
            branch(tip, heading_next, length * rng.uniform(0.55, 0.72),
                   radius * 0.62, depth - 1)

        # Leaves along the whole of the limb, not only near where it forks.
        #
        # A limb is painted in bark, and bark is pale where a leaf card is dark: a bare one
        # running out through a crown reads as a stick pushed into a bush, which is what the
        # first broadleaves looked like from ten feet away. Clothing it is cheaper than
        # shading it - there is nowhere to put a per-vertex occlusion on the bark either -
        # and a real limb inside a crown is not visible at all.
        #
        # Enough clumps to cover its length rather than a fixed one or two, so a long first
        # limb is covered as well as a short twig at the end of it.
        along = max(2, int(length / (spec["clumpScale"] * 0.85)))

        for step in range(along):
            at = start.lerp(tip, (step + rng.uniform(0.1, 0.9)) / along)                 + _jitter(rng, length * 0.16)
            _fan(growth, rng, at, out,
                 spec["clumpScale"] * rng.uniform(0.7, 1.05), spec, 0.12,
                 facing=at - centre)

    top = trunk[-1]
    limbs = rng.randint(*spec["limbs"])

    offset = rng.uniform(0.0, math.tau)
    for index in range(limbs):
        angle = offset + math.tau * index / limbs + rng.uniform(-0.25, 0.25)
        heading = Vector((math.cos(angle) * spec["limbSpread"],
                          math.sin(angle) * spec["limbSpread"],
                          spec["limbRise"])).normalized()
        branch(top, heading, spec["crownHeight"] * rng.uniform(0.34, 0.46),
               spec["trunkTopRadius"] * rng.uniform(0.50, 0.68), spec["limbSplits"])

    # Leaves through the body of the crown, and not only on the twigs that reach the
    # outside of it. Branching alone leaves a hollow: the limbs spread outwards, every
    # clump ends up on the rim, and the tree comes out as a ring with a gap down the
    # middle that the sky shows through. A real crown is full, so it is filled.
    across = spec["crownRadius"]
    down = spec["crownHeight"] * 0.50

    for _ in range(spec["crownFill"]):
        while True:
            u = Vector((rng.uniform(-1.0, 1.0), rng.uniform(-1.0, 1.0), rng.uniform(-1.0, 1.0)))
            # Denser towards the middle, so the outline stays broken rather than
            # becoming the ellipsoid the points were scattered into.
            if u.length <= 1.0 and rng.random() > u.length ** 3 * 0.55:
                break

        at = centre + Vector((u.x * across, u.y * across, u.z * down))
        _fan(growth, rng, at, Vector((u.x, u.y, u.z * 0.5)) if u.length > 1e-3 else UP,
             spec["clumpScale"] * rng.uniform(0.75, 1.20), spec, 0.10)

    return growth


# --------------------------------------------------------------------------------------
# Meshing
# --------------------------------------------------------------------------------------


def shade_leaves(growth, levels, reach=0.24, weight=0.75):
    """Decides how deep in the crown each leaf sits, and so which tile it is painted from.

    What is measured is **sky, not density**: for every leaf, the neighbours standing
    between it and the sky, each counted by how directly overhead it is and how close. A
    leaf on the top of the crown has nothing above it and comes out lit; one in the heart of
    the tree has forty clumps over it and comes out dark; and the underside of the canopy
    darkens on its own, which plain density never gives because a leaf on the *bottom* of
    the crown has just as many neighbours as one on the top.

    Quantised to whatever tiles the card carries. Four steps sound crude and are not: the
    gradient a crown needs is between its shell and its heart, and inside one twelve
    centimetre clump there is nothing to resolve.
    """
    if levels <= 1 or not growth.leaves:
        return [len(growth.leaves)]

    # A grid of one reach per cell, so each leaf only asks about the twenty-seven cells
    # around it rather than about all fifteen hundred.
    cells = {}
    for leaf in growth.leaves:
        key = (int(leaf.centre.x / reach), int(leaf.centre.y / reach),
               int(leaf.centre.z / reach))
        cells.setdefault(key, []).append(leaf)

    counted = []

    for leaf in growth.leaves:
        key = (int(leaf.centre.x / reach), int(leaf.centre.y / reach),
               int(leaf.centre.z / reach))
        over = 0.0

        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (0, 1):
                    for other in cells.get((key[0] + dx, key[1] + dy, key[2] + dz), ()):
                        apart = other.centre - leaf.centre

                        if apart.z <= 0.0:
                            continue

                        far = apart.length
                        if far < 1e-6 or far > reach:
                            continue

                        # Straight overhead and close counts for most; off to one side and
                        # at arm's length counts for little.
                        over += (1.0 - far / reach) * (apart.z / far)

        counted.append(over)

    # Thresholds taken from the tree's own spread rather than from a constant, because a
    # spruce carries three times the clumps of a maple in the same volume and a fixed cut
    # would paint one of them entirely from a single tile.
    # Where the cuts fall barely changes what the tree looks like, and that is worth
    # knowing rather than rediscovering. Moving them a tenth towards the light shifts a
    # hundred and fifty of a maple's leaves one tile brighter and the rendered crown does
    # not move at all: the leaves that change are the buried ones, and the shell - which is
    # all that is ever seen - was in the brightest tile either way. A grown tree that reads
    # darker than the flat card beside it is darker for a different reason, which is that
    # the 1999 card carries IgnoreLightmapFlag and is drawn at full brightness.
    order = sorted(counted)
    shares = [order[min(len(order) - 1, int(len(order) * at))]
              for at in (0.30, 0.60, 0.85)]

    tally = [0] * levels

    for leaf, over in zip(growth.leaves, counted):
        level = 0
        for cut in shares[:levels - 1]:
            if over > cut:
                level += 1

        # Pulled back towards the light a little, so a crown is not a quarter black. The
        # deepest tile is for leaves that are genuinely buried.
        leaf.level = level if over > shares[0] * weight else 0
        tally[leaf.level] += 1

    return tally


def build_mesh(growth, spec, name):
    """Turns a growth into one Blender object with a bark and a leaf material."""
    mesh = bpy.data.meshes.new(name)
    mesh.materials.append(_material(spec["bark"]))
    mesh.materials.append(_material(spec["card"]))

    bm = bmesh.new()
    uvs = bm.loops.layers.uv.new("UVMap")

    for points, radii, sides in growth.tubes:
        rings = []
        for index, (point, radius) in enumerate(zip(points, radii)):
            heading = (points[min(index + 1, len(points) - 1)]
                       - points[max(index - 1, 0)])
            if heading.length < 1e-6:
                heading = UP.copy()
            heading.normalize()
            right = _perpendicular(heading)
            forward = heading.cross(right).normalized()
            ring = []
            for side in range(sides):
                angle = math.tau * side / sides
                offset = right * (math.cos(angle) * radius) + forward * (math.sin(angle) * radius)
                ring.append(bm.verts.new(point + offset))
            rings.append(ring)

        run = 0.0
        for index in range(len(rings) - 1):
            run_next = run + (points[index + 1] - points[index]).length
            for side in range(sides):
                nxt = (side + 1) % sides
                face = bm.faces.new((rings[index][side], rings[index][nxt],
                                     rings[index + 1][nxt], rings[index + 1][side]))
                face.material_index = 0
                # Bark tiles four times around and once every tenth of the tree's height,
                # which is what keeps a trunk and a twig looking like the same wood.
                for loop, (u, v) in zip(face.loops, (
                        (side / sides * 4.0, run * 4.0),
                        ((side + 1) / sides * 4.0, run * 4.0),
                        ((side + 1) / sides * 4.0, run_next * 4.0),
                        (side / sides * 4.0, run_next * 4.0))):
                    loop[uvs].uv = (u, v)
            run = run_next

    # One leaf clump is a three-by-three patch bowed out of its own plane: a shallow dome
    # or a saddle, drawn per clump. Four quads instead of one, and worth every triangle of
    # it twice over. It shades across itself, which is what a flat card can never do; and
    # because no two bowed patches can lie in the same plane, two clumps drawn over each
    # other stop fighting for the same depth. Far trees are grown flat - `bow` and `twist`
    # are zero - and the same code lays down a plain quad for them.
    span = 2 if spec["bowed"] else 1

    for leaf in growth.leaves:
        size = leaf.right.length
        rect = spec["tiles"][min(leaf.level, len(spec["tiles"]) - 1)][leaf.mirror]
        u0, v0, u1, v1 = rect

        grid = []
        for row in range(span + 1):
            line = []
            for column in range(span + 1):
                u = (column / span * 2.0) - 1.0
                v = (row / span * 2.0) - 1.0
                out = (leaf.bow * (1.0 - u * u) * (1.0 - v * v)) + (leaf.twist * u * v)
                line.append(bm.verts.new(
                    leaf.centre + (leaf.right * u) + (leaf.up * v)
                    + (leaf.facing * (out * size))))
            grid.append(line)

        for row in range(span):
            for column in range(span):
                face = bm.faces.new((
                    grid[row][column], grid[row][column + 1],
                    grid[row + 1][column + 1], grid[row + 1][column]))
                face.material_index = 1

                # Across the card with `right` and up it with `up`, which runs the other
                # way in texture space: glTF's v grows downwards.
                for loop, (u, v) in zip(face.loops, (
                        (column / span, 1.0 - row / span),
                        ((column + 1) / span, 1.0 - row / span),
                        ((column + 1) / span, 1.0 - (row + 1) / span),
                        (column / span, 1.0 - (row + 1) / span))):
                    loop[uvs].uv = (u0 + (u1 - u0) * u, v0 + (v1 - v0) * v)

    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()

    _round_the_leaves(mesh)

    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _round_the_leaves(mesh, curve=0.42):
    """Shades the leaves as one mass, with the curve of each clump still showing in it.

    A card's own normal is the wrong answer twice over. Nothing here is culled, so half
    the cards in any crown are seen from behind and would shade as though lit from the far
    side; and a crown of a thousand quads at a thousand angles reads as a heap of litter
    rather than as one mass with a lit side and a shaded one. Normals taken from the crown
    centre outwards give the mass back - it is the same trick every foliage shader has used
    since trees stopped being sprites, and it costs nothing at run time.

    What is new is the second term. Taking the crown's normal and *nothing else* makes the
    mass so smooth that the clumps inside it disappear: a broadleaf comes out as a green
    sphere. So each patch keeps a share of its own bowed normal, flipped where it faces
    into the tree, and the crown has clumps in it again without losing its shape.
    """
    leaves = [polygon for polygon in mesh.polygons if polygon.material_index == 1]

    if not leaves:
        return

    centre = Vector((0.0, 0.0, 0.0))
    for polygon in leaves:
        centre += polygon.center
    centre /= len(leaves)

    normals = [tuple(mesh.loops[index].normal) for index in range(len(mesh.loops))]

    for polygon in leaves:
        # A little upwards in the mix, so that the underside of a crown is shaded rather
        # than black: a leaf below the middle of the tree still sees the sky.
        outward = polygon.center - centre
        outward = (outward.normalized() + UP * 0.35).normalized() \
            if outward.length > 1e-6 else UP.copy()

        own = polygon.normal.copy()
        if own.length < 1e-6:
            own = outward.copy()
        elif own.dot(outward) < 0.0:
            own = -own

        mixed = (outward * (1.0 - curve)) + (own.normalized() * curve)

        if mixed.length < 1e-6:
            mixed = outward

        for index in polygon.loop_indices:
            normals[index] = tuple(mixed.normalized())

    mesh.normals_split_custom_set(normals)


def _material(texture):
    """A material named for the game texture it wants, which is all the engine reads."""
    existing = bpy.data.materials.get(texture)
    if existing is not None:
        return existing

    material = bpy.data.materials.new(texture)
    material.use_nodes = True
    return material


# --------------------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------------------


def normalise(obj):
    """Stands the tree on the origin, exactly one unit tall, and reports its spread."""
    mesh = obj.data
    lo = Vector((min(v.co.x for v in mesh.vertices),
                 min(v.co.y for v in mesh.vertices),
                 min(v.co.z for v in mesh.vertices)))
    hi = Vector((max(v.co.x for v in mesh.vertices),
                 max(v.co.y for v in mesh.vertices),
                 max(v.co.z for v in mesh.vertices)))
    height = max(hi.z - lo.z, 1e-6)
    centre = Vector(((lo.x + hi.x) * 0.5, (lo.y + hi.y) * 0.5, lo.z))

    for vertex in mesh.vertices:
        vertex.co = (vertex.co - centre) / height

    lo = (lo - centre) / height
    hi = (hi - centre) / height
    return {
        "radius": round(max(hi.x, hi.y, -lo.x, -lo.y), 4),
        "low": [round(lo.x, 4), round(lo.y, 4), round(lo.z, 4)],
        "high": [round(hi.x, 4), round(hi.y, 4), round(hi.z, 4)],
    }


def main(argv):
    parser = argparse.ArgumentParser(description="Grow modelled trees for GK3Reborn.")
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--species", nargs="*", default=None)
    parser.add_argument("--variants", type=int, default=4)
    parser.add_argument("--far", type=int, default=3,
                        help="How many cheaper variants to grow per species for the far "
                             "half of a wood (default 3).")
    parser.add_argument("--dry-run", action="store_true")
    options = parser.parse_args(argv)

    wanted = options.species or sorted(SPECIES)
    unknown = [name for name in wanted if name not in SPECIES]
    if unknown:
        print("unknown species: " + ", ".join(unknown), file=sys.stderr)
        return 2

    out = os.path.join(options.workspace, "enhanced", "trees")
    if not options.dry_run:
        os.makedirs(out, exist_ok=True)

    # The drawn cards, and not a guess at what they are called. Growing a tree that names a
    # texture nobody has drawn produces a tree the engine cannot paint, and it fails at
    # load time in another program rather than here.
    drawn = os.path.join(out, "cards.json")
    if not os.path.exists(drawn):
        print("no foliage cards at " + drawn
              + "; run tools/foliage/make_cards.py first", file=sys.stderr)
        return 2

    with open(drawn, encoding="utf-8") as handle:
        cards = {entry["species"]: entry for entry in json.load(handle).get("cards", [])}

    for species in wanted:
        if species not in cards:
            print("no card drawn for " + species, file=sys.stderr)
            return 2

        card = cards[species]
        SPECIES[species]["card"] = card["texture"]
        # However many occlusion tiles the card was drawn with, and one if it has none.
        # An older card still works and its trees come out evenly lit, which is what they
        # were before the tiles existed.
        SPECIES[species]["tiles"] = tiles(
            card.get("atlas", 1), len(card.get("aoLevels", [1.0])))

    records = []
    for species in wanted:
        full = SPECIES[species]
        near = [("near", full, variant) for variant in range(options.variants)]
        distant = [("far", far(full), variant) for variant in range(options.far)]

        for detail, spec, variant in near + distant:
            bpy.ops.wm.read_factory_settings(use_empty=True)
            # Seeded by name, detail and number, so the same tree comes out of every run.
            # A forest that reshuffles itself between builds cannot be compared with the
            # one before it.
            rng = random.Random(species + "/" + detail + "/" + str(variant))
            growth = (grow_conifer if spec["kind"] == "conifer" else grow_broadleaf)(spec, rng)
            name = (species + "_" + format(variant, "02d")
                    + ("" if detail == "near" else "_far"))
            tally = shade_leaves(growth, len(spec["tiles"]))
            obj = build_mesh(growth, spec, name)
            shape = normalise(obj)

            triangles = sum(len(polygon.vertices) - 2 for polygon in obj.data.polygons)
            record = {
                "name": name,
                "species": species,
                "variant": variant,
                "detail": detail,
                "kind": spec["kind"],
                "bark": spec["bark"],
                "leaf": spec["leaf"],
                "card": spec["card"],
                "canopy": spec["canopy"],
                "triangles": triangles,
                "vertices": len(obj.data.vertices),
                "cards": len(growth.leaves),
                "shade": tally,
            }
            record.update(shape)
            records.append(record)
            print(name + ": " + str(triangles) + " triangles, "
                  + str(len(growth.leaves)) + " leaves "
                  + "/".join(str(count) for count in tally)
                  + ", radius " + str(shape["radius"]))

            if not options.dry_run:
                bpy.ops.export_scene.gltf(
                    filepath=os.path.join(out, name + ".glb"),
                    export_format="GLB",
                    export_yup=True,
                    export_apply=True,
                    export_materials="EXPORT",
                    export_normals=True,
                    use_selection=False)

    if not options.dry_run:
        # Beside the trees rather than under manifests/, because this one is content and
        # not a report: the engine reads the directory as a unit and needs to be told what
        # is in it, and a pack that carries the trees has to carry this with them.
        with open(os.path.join(out, "trees.json"), "w", encoding="utf-8") as handle:
            json.dump({
                "schemaVersion": 1,
                "stage": "C7.trees",
                "note": "Trees are normalised: base at the origin, one unit tall. "
                        "Radius and bounds are in those units.",
                "species": {name: {"kind": spec["kind"], "leaf": spec["leaf"],
                                   "bark": spec["bark"], "card": spec["card"],
                                   "canopy": spec["canopy"],
                                   "sprites": spec["sprites"]}
                            for name, spec in SPECIES.items() if "card" in spec},
                "trees": records,
            }, handle, indent=1)

    print("grew " + str(len(records)) + " trees")
    return 0


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    sys.exit(main(args))
