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

# A leaf card is the whole of one drawn foliage texture, mirrored at random so that two
# hundred of them on one tree are not two hundred copies of the same picture. The four
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
        "cards": MIRRORS,
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
        "fansPerBranch": (5, 8),
        "fanScale": 0.34,
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
        "cards": MIRRORS,
        "trunkRadius": 0.045,
        "trunkTopRadius": 0.024,
        "trunkSegments": 7,
        "trunkLean": 0.030,
        "boleHeight": 0.32,
        "limbs": (4, 6),
        "limbSplits": 4,
        "limbSpread": 0.60,
        "limbRise": 0.72,
        "branchSides": 4,
        "branchSegments": 3,
        "crownHeight": 0.68,
        "crownRadius": 0.29,
        "clumpsPerTwig": (2, 3),
        "clumpScale": 0.12,
        "crownFill": 70,
    },
    # The maple of MAPLESIDE1, which is the same tree grown wider and lower.
    "maple": {
        "kind": "broadleaf",
        "bark": "TRUNK01",
        "leaf": "MAPLE",
        "sprites": ["MAPLESIDE1", "MAPLETOP1", "MAPLE"],
        "canopy": False,
        "cards": MIRRORS,
        "trunkRadius": 0.048,
        "trunkTopRadius": 0.026,
        "trunkSegments": 7,
        "trunkLean": 0.040,
        "boleHeight": 0.28,
        "limbs": (4, 6),
        "limbSplits": 4,
        "limbSpread": 0.70,
        "limbRise": 0.66,
        "branchSides": 4,
        "branchSegments": 3,
        "crownHeight": 0.72,
        "crownRadius": 0.35,
        "clumpsPerTwig": (2, 3),
        "clumpScale": 0.13,
        "crownFill": 80,
    },
    # WOODTREE3's dark, dense broadleaf: a heavier crown on a shorter bole.
    "darkbroadleaf": {
        "kind": "broadleaf",
        "bark": "TRUNK01",
        "leaf": "WOODTREE3",
        "sprites": ["WOODTREE3", "MAGENTREE"],
        "canopy": False,
        "cards": MIRRORS,
        "trunkRadius": 0.050,
        "trunkTopRadius": 0.028,
        "trunkSegments": 7,
        "trunkLean": 0.035,
        "boleHeight": 0.26,
        "limbs": (5, 6),
        "limbSplits": 4,
        "limbSpread": 0.64,
        "limbRise": 0.70,
        "branchSides": 4,
        "branchSegments": 3,
        "crownHeight": 0.74,
        "crownRadius": 0.32,
        "clumpsPerTwig": (2, 3),
        "clumpScale": 0.12,
        "crownFill": 85,
    },
    # The columnar conifer of TREE06 - narrower and taller-crowned than the spruce.
    "cypress": {
        "kind": "conifer",
        "bark": "TRUNK01",
        "leaf": "TREE06",
        "sprites": ["TREE06"],
        "canopy": True,
        "cards": MIRRORS,
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
        "fansPerBranch": (4, 6),
        "fanScale": 0.38,
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
FAR = {
    "whorls": 0.55,
    "branchesPerWhorl": 0.70,
    "fansPerBranch": 0.45,
    "fanScale": 1.75,
    "trunkSegments": 0.55,
    "branchSegments": 0.70,
    "limbSplits": 0.70,
    "clumpsPerTwig": 0.50,
    "clumpScale": 1.90,
    "crownFill": 0.22,
}


def thin(spec, factors):
    """A species grown with fewer, larger pieces."""
    out = dict(spec)

    for key, factor in factors.items():
        if key not in out:
            continue

        value = out[key]
        if isinstance(value, tuple):
            out[key] = tuple(max(1, int(round(part * factor))) for part in value)
        elif isinstance(value, int):
            out[key] = max(1, int(round(value * factor)))
        else:
            out[key] = value * factor

    return out


# --------------------------------------------------------------------------------------
# Geometry
# --------------------------------------------------------------------------------------


class Growth:
    """The tubes and cards a tree is made of, before any of it becomes a mesh."""

    def __init__(self):
        self.tubes = []   # (points, radii, sides)
        self.cards = []   # (centre, right, up, rect)

    def tube(self, points, radii, sides):
        if len(points) >= 2:
            self.tubes.append((points, radii, sides))

    def card(self, centre, right, up, rect):
        self.cards.append((centre, right, up, rect))


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


def _fan(growth, rng, centre, along, size, rects, droop):
    """Hangs one needle fan or leaf clump, facing outwards from the trunk."""
    if along.length < 1e-6:
        along = UP.copy()
    right = along.normalized() * size
    outward = _perpendicular(along)
    up = (outward - UP * droop).normalized() * (size * 0.62)
    spin = Matrix.Rotation(rng.uniform(0.0, math.tau), 3, along.normalized())
    growth.card(centre, spin @ right, spin @ up, rng.choice(rects))


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
                     spec["cards"], spec["fanDroop"])

        # A crowning fan, so the leader does not end in a bare spike.
        if whorl == spec["whorls"] - 1:
            _fan(growth, rng, on_trunk(1.0) - UP * 0.03,
                 Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0.35)),
                 spec["crownRadius"] * 0.5, spec["cards"], 0.0)

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

    def branch(start, heading, length, radius, depth):
        tip, out = _limb(
            growth, rng, start, heading, length, radius,
            spec["branchSides"], spec["branchSegments"], UP * 0.10 + _jitter(rng, 0.10))

        if depth == 0 or tip.z > crown_top:
            for _ in range(rng.randint(*spec["clumpsPerTwig"])):
                _fan(growth, rng, tip + _jitter(rng, length * 0.35), out,
                     spec["clumpScale"] * rng.uniform(0.8, 1.25), spec["cards"], 0.12)
            return

        for _ in range(rng.randint(2, 3)):
            aside = _perpendicular(out) * rng.uniform(-1.0, 1.0)
            spin = Matrix.Rotation(rng.uniform(0.0, math.tau), 3, out)
            heading_next = (out * rng.uniform(0.55, 0.85)
                            + spin @ aside * spec["limbSpread"]
                            + UP * spec["limbRise"] * 0.35).normalized()
            branch(tip, heading_next, length * rng.uniform(0.55, 0.72),
                   radius * 0.62, depth - 1)

        # Leaves along the fork itself, so the crown is not hollow in the middle.
        for _ in range(rng.randint(1, 2)):
            _fan(growth, rng,
                 start.lerp(tip, rng.uniform(0.4, 1.0)) + _jitter(rng, length * 0.3),
                 out, spec["clumpScale"] * rng.uniform(0.7, 1.0), spec["cards"], 0.12)

    top = trunk[-1]
    limbs = rng.randint(*spec["limbs"])
    offset = rng.uniform(0.0, math.tau)
    for index in range(limbs):
        angle = offset + math.tau * index / limbs + rng.uniform(-0.25, 0.25)
        heading = Vector((math.cos(angle) * spec["limbSpread"],
                          math.sin(angle) * spec["limbSpread"],
                          spec["limbRise"])).normalized()
        branch(top, heading, spec["crownHeight"] * rng.uniform(0.42, 0.55),
               spec["trunkTopRadius"] * rng.uniform(0.62, 0.85), spec["limbSplits"])

    # Leaves through the body of the crown, and not only on the twigs that reach the
    # outside of it. Branching alone leaves a hollow: the limbs spread outwards, every
    # clump ends up on the rim, and the tree comes out as a ring with a gap down the
    # middle that the sky shows through. A real crown is full, so it is filled.
    centre = Vector((0.0, 0.0, bole + spec["crownHeight"] * 0.48)) + lean
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
             spec["clumpScale"] * rng.uniform(0.75, 1.20), spec["cards"], 0.10)

    return growth


# --------------------------------------------------------------------------------------
# Meshing
# --------------------------------------------------------------------------------------


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

    for centre, right, up, rect in growth.cards:
        corners = [centre - right - up, centre + right - up,
                   centre + right + up, centre - right + up]
        verts = [bm.verts.new(corner) for corner in corners]
        face = bm.faces.new(verts)
        face.material_index = 1
        u0, v0, u1, v1 = rect
        for loop, (u, v) in zip(face.loops, ((u0, v1), (u1, v1), (u1, v0), (u0, v0))):
            loop[uvs].uv = (u, v)

    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()

    _round_the_leaves(mesh)

    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _round_the_leaves(mesh):
    """Points every leaf card's normals out of the crown instead of out of the quad.

    A card's own normal is the wrong answer twice over. Nothing here is culled, so half
    the cards in any crown are seen from behind and shade as though lit from the far side;
    and a crown of two hundred flat quads at two hundred angles reads as a heap of litter
    rather than as one mass with a lit side and a shaded one. Normals taken from the crown
    centre outwards give the mass back - it is the same trick every foliage shader has used
    since trees stopped being sprites, and it costs nothing at runtime.
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
        for index in polygon.loop_indices:
            out = mesh.vertices[mesh.loops[index].vertex_index].co - centre
            # A little upwards in the mix, so that the underside of a crown is shaded
            # rather than black: a leaf below the middle of the tree still sees the sky.
            out = out.normalized() + UP * 0.35 if out.length > 1e-6 else UP.copy()
            normals[index] = tuple(out.normalized())

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
        cards = {entry["species"]: entry["texture"]
                 for entry in json.load(handle).get("cards", [])}

    for species in wanted:
        if species not in cards:
            print("no card drawn for " + species, file=sys.stderr)
            return 2
        SPECIES[species]["card"] = cards[species]

    records = []
    for species in wanted:
        full = SPECIES[species]
        near = [("near", full, variant) for variant in range(options.variants)]
        distant = [("far", thin(full, FAR), variant) for variant in range(options.far)]

        for detail, spec, variant in near + distant:
            bpy.ops.wm.read_factory_settings(use_empty=True)
            # Seeded by name, detail and number, so the same tree comes out of every run.
            # A forest that reshuffles itself between builds cannot be compared with the
            # one before it.
            rng = random.Random(species + "/" + detail + "/" + str(variant))
            growth = (grow_conifer if spec["kind"] == "conifer" else grow_broadleaf)(spec, rng)
            name = (species + "_" + format(variant, "02d")
                    + ("" if detail == "near" else "_far"))
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
                "cards": len(growth.cards),
            }
            record.update(shape)
            records.append(record)
            print(name + ": " + str(triangles) + " triangles, "
                  + str(len(growth.cards)) + " cards, radius " + str(shape["radius"]))

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
