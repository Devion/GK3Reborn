"""Scene-geometry enhancement pass for GK3Reborn, run inside Blender.

    blender --background --factory-startup --python enhance_scenes.py -- \
        --workspace D:/Dev/GK3Reborn/ContentWorkspace \
        [--only ROOM ...] [--objects NAME ...] [--dispositions a,b] \
        [--limit N] [--force] [--dry-run]

Reads `manifests/scene-objects.json`, which `extract-scenes` wrote, takes each object's
extracted geometry from `enhanced/scenes/<room>/original/`, and writes the improved
version one directory up. `compose-scenes` then gathers whatever is there into the one
file per room the game reads.

What this does and does not spend triangles on
----------------------------------------------

The corpus's objects are small: an ornament is 60 triangles at the median and a piece of
furniture 142. What makes them read as 1999 is not the triangle count, it is that every
edge is infinitely sharp — an edge with no width catches no highlight, so a table reads as
a decal of a table. **A bevel is therefore worth more than a subdivision here**, and it is
nearly free: an angle-limited bevel touches only edges that are already sharp and adds
nothing at all across a flat panel.

Subdivision is spent only where there is a curve to recover, and where a curve is is
measured rather than assumed. A face is refined when it meets a neighbour at an angle that
is neither flat nor a corner — the signature of a curve someone tessellated — so the sides
of a lamp shade are subdivided and its flat cap is not. This is the whole of the answer to
"do not waste triangles on flat surfaces that would really be flat": the flats are found
and left alone.

Why the vertices are not welded into a consistent winding
---------------------------------------------------------

GK3's scene geometry is not consistently wound, which is why the exporter marks every
material double-sided. Recalculating normals outside would flip whichever faces the
artists happened to draw backwards, and the renderer derives its face normals from winding
exactly as this data stands. So winding is left as authored: the enhanced object shades as
the original does, only with edges on it.

Why not geometry nodes
----------------------

Everything here is a bevel, a selective subdivision and a weighted-normal pass, all of
which are modifiers or four lines of bmesh. A node tree would express the same operations,
add a .blend file the pipeline has to ship and version, and put the interesting decision —
which faces are curved — inside a graph that cannot be unit-tested. The measurement that
decides it is worth more than the machinery that applies it.
"""

import argparse
import json
import math
import os
import sys
import time
from pathlib import Path

import bmesh
import bpy
import mathutils


# What the classifier calls an object, and what that earns it here. Anything not named
# is left alone: see SceneObjectManifest for what each disposition means.
IMPROVED = {"ornament", "furniture", "vehicle", "rock", "architecture"}

# Dispositions that are never touched even when asked for by name, because touching them
# is known to make the picture worse rather than merely to cost triangles.
NEVER = {"collision", "terrain", "foliage", "backdrop", "flat"}


def parse_args(argv):
    parser = argparse.ArgumentParser(description="Enhance GK3 scene objects in Blender.")
    parser.add_argument("--workspace", required=True, help="Content workspace root.")
    parser.add_argument("--only", nargs="*", default=None, help="Process just these rooms.")
    parser.add_argument("--objects", nargs="*", default=None,
                        help="Process just these objects, by name, within those rooms.")
    parser.add_argument("--dispositions", default=None,
                        help="Comma-separated dispositions to process, default "
                             + ",".join(sorted(IMPROVED)) + ".")
    parser.add_argument("--limit", type=int, default=0, help="Stop after N objects.")
    parser.add_argument("--include-review", action="store_true",
                        help="Also process objects nothing classified.")
    parser.add_argument("--bevel", type=float, default=1.2,
                        help="Bevel width, in hundredths of the object's longest edge, "
                             "clamped either side. Relative to the object on purpose: one "
                             "fixed width cannot serve a chair leg and a church wall.")
    parser.add_argument("--segments", type=int, default=2, help="Bevel segments.")
    parser.add_argument("--levels", type=int, default=2,
                        help="How many times a curved region is subdivided. 0 turns "
                             "subdivision off and leaves the bevel, which is how the two "
                             "are told apart in a screenshot.")
    parser.add_argument("--growth", type=float, default=24.0,
                        help="Most an object's triangle count may multiply by. An object "
                             "that asks for more is refined one level less.")
    parser.add_argument("--ceiling", type=int, default=15000,
                        help="Most triangles one object may come to, whatever its own "
                             "size asks for. What this catches is the object that is "
                             "already detailed: a carved figure of 4,400 triangles has "
                             "facets a millimetre across and gains nothing from being "
                             "made 84,000, which is what it asked for.")
    parser.add_argument("--force", action="store_true",
                        help="Redo objects that already have an enhanced file.")
    parser.add_argument("--dry-run", action="store_true", help="Report the plan and stop.")
    return parser.parse_args(argv)


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    before = {o.name for o in bpy.context.scene.objects}
    bpy.ops.import_scene.gltf(filepath=str(path))
    return [o for o in bpy.context.scene.objects
            if o.type == "MESH" and o.name not in before]


def select_only(objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    if objects:
        bpy.context.view_layer.objects.active = objects[0]


def triangle_count(obj):
    return sum(max(1, len(p.vertices) - 2) for p in obj.data.polygons)


def clean(obj):
    """Weld the object back into one surface, without touching its winding.

    The extracted geometry is split at every crease and at every surface boundary,
    because that is how a glTF primitive per surface has to be written. Bevel needs the
    topology back: two quads that share a position but not an edge have no edge between
    them to round. Merging by distance restores it, and merging by distance alone cannot
    flip a face.

    The custom split normals go first. They were reconstructed by the extractor for a
    viewer's benefit and are about to be reconstructed again from the same crease angle;
    keeping them would freeze the shading of an object whose shape is being changed.
    """
    select_only([obj])

    if obj.data.has_custom_normals:
        bpy.ops.mesh.customdata_custom_splitnormals_clear()

    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    # **A hundredth of a unit, not a ten-thousandth.** A room's coordinates run to a few
    # thousand, where a 32-bit float resolves about two ten-thousandths -- so the tighter
    # threshold could not merge two corners the original file holds at exactly the same
    # place, because writing them through a float had already moved them apart by more
    # than it. What survived was a shading seam down the side of every object whose two
    # halves are different surfaces, which is most of them: a hard line down the middle of
    # a barrel that is otherwise round. A hundredth is forty times that resolution and a
    # hundredth of the smallest thing anybody modelled.
    bpy.ops.mesh.remove_doubles(threshold=0.01)
    bpy.ops.mesh.dissolve_degenerate(threshold=0.0001)
    bpy.ops.mesh.delete_loose()
    bpy.ops.object.mode_set(mode="OBJECT")


# An edge whose two faces meet at less than this is part of a flat panel: the artists
# split a wall into quads and nothing about it is curved. Half a degree above zero would
# do; three degrees allows for the coordinates having been through a float.
FLAT = math.radians(3.0)

# And an edge whose faces meet at more than this is a corner, not a curve. How far past
# a curve has to turn before it is a corner is the one number here that cannot be measured
# from the geometry, because it is not in the geometry: an eight-sided prism and a crude
# cylinder are the same mesh, and only what the thing *is* separates them. So it is a
# property of the disposition, in TREATMENT below, and this is what gathers a vertex's
# faces into smoothing groups rather than what decides a curve.
#
# A lathe of N sides turns 360/N at each of its own sides: 30 degrees at twelve, 45 at
# eight, 60 at six. A box corner is 90. A roof ridge is 40 to 70 depending on its pitch,
# which is why architecture stops below 60 and an ornament does not.
CORNER = math.radians(55.0)


def dihedral(edge):
    """The angle between an edge's two faces, whichever way round they are wound.

    **GK3's scene geometry is not consistently wound**, so half the time a perfectly flat
    seam between two quads of one wall measures 180 degrees rather than nothing. Every
    decision here is made from that measurement - what to refine, what to bevel - and
    taking it at face value cut a groove along the middle of a tablecloth and left a dark
    slit across it.

    **Folding the angle back is not good enough, and was the second mistake.** `min(a,
    180-a)` does read a flat seam as flat from either side, but it also reads a razor
    edge at 170 degrees as a gentle 10-degree curve and refines it, and it reads a roof
    ridge at 120 as a 60-degree lathe step. Measured across the corpus that is wrong on
    47 edges of a fountain and 73 of one room's walls.

    So the angle is taken exactly, from the normals, and only its *sign* is recovered from
    the geometry: the two faces' centres, seen from the middle of the edge, are 180 degrees
    apart when the surface is flat and closer together the harder it folds. That estimate
    is biased by the shape of the triangles and is nowhere near good enough to use as the
    answer -- but it does not have to be, because the two candidates it chooses between are
    a whole fold apart, and where they are not (both near 90) there is nothing to choose.
    """
    if len(edge.link_faces) != 2:
        return None

    try:
        angle = edge.calc_face_angle()
    except ValueError:
        return None

    middle = (edge.verts[0].co + edge.verts[1].co) / 2.0
    here = edge.link_faces[0].calc_center_median() - middle
    there = edge.link_faces[1].calc_center_median() - middle

    if here.length < 1e-9 or there.length < 1e-9:
        return angle

    wanted = math.pi - here.angle(there)

    return angle if abs(angle - wanted) <= abs((math.pi - angle) - wanted) else math.pi - angle


def curved_faces(bm, corner):
    """The faces that are part of a tessellated curve rather than of a flat or a corner.

    Measured from the angle between neighbouring faces, which is the only signal in this
    data that says the difference. A cylinder's sides meet each other gently and are
    curved; a wall's quads meet at nothing and are flat; a box's corner meets at a right
    angle and is a corner. Nothing here is a name.
    """
    wanted = set()

    for edge in bm.edges:
        if len(edge.link_faces) != 2:
            continue

        angle = dihedral(edge)

        if angle is not None and FLAT < angle < corner:
            for face in edge.link_faces:
                wanted.add(face.index)

    return wanted


def smoothing_groups(bm, centre, group):
    """A normal per vertex per smoothing group, so a crease does not leak across itself.

    Blender's own `vert.normal` averages every face at a vertex regardless of what angle
    they meet at, and on this data that is not merely imprecise, it is wrong: GK3's scene
    geometry is not consistently wound, so two faces of one surface can have opposed
    normals that cancel to nearly nothing. Feeding that to a smoothing pass sends the new
    vertex off in an arbitrary direction proportional to the length of the edge, which is
    how a telephone pole came out four times its own width.

    So faces are gathered by the *unsigned* angle between them, which is winding-blind,
    and each group's normal is accumulated with every face turned to agree with the first
    one in it. The group is then pointed away from the object's own middle, so that a
    curve recovered from it bows outward rather than into the object it belongs to.
    """
    parent = {}

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    limit = math.cos(group)
    anchor = {}

    for vert in bm.verts:
        here = list(vert.link_faces)

        for face in here:
            parent[(vert.index, face.index)] = (vert.index, face.index)

        for i, a in enumerate(here):
            for b in here[i + 1:]:
                if abs(a.normal.dot(b.normal)) >= limit:
                    parent[find((vert.index, a.index))] = find((vert.index, b.index))

    normals = {}

    for vert in bm.verts:
        for face in vert.link_faces:
            root = find((vert.index, face.index))

            if root not in anchor:
                anchor[root] = face.normal.copy()

            turned = face.normal if face.normal.dot(anchor[root]) >= 0 else -face.normal
            normals[root] = (
                normals.get(root, mathutils.Vector((0, 0, 0))) + turned * face.calc_area())

    for key, normal in list(normals.items()):
        if normal.dot(bm.verts[key[0]].co - centre) < 0:
            normals[key] = -normal

    return parent, find, normals


# How much of the full width a bevel gets where it has to cross a texture seam.
#
# **A bevel across a seam draws whatever lies between the two sides of it.** Every edge
# between two of a room's surfaces is a seam - each surface carries its own mapping - and
# the strip a bevel cuts there sweeps its texture coordinates from one side's to the
# other's, drawing a smeared band of whatever the picture holds in between. On the dining
# room's tables that came out as a dark dashed line across the tablecloth, and it is the
# width of the bevel, not the bevel itself: at a sixth of the width it is not visible at
# any distance the player sees the table from.
#
# Not zero, because these are the edges most worth rounding. A table's top meeting its
# side is a seam in this data, and it is also the silhouette.
SEAM_WEIGHT = 0.18

# How far apart two faces' texture coordinates have to be at a shared corner before the
# edge between them counts as a seam. Small: a seam in this data is a jump to somewhere
# else on the picture, not a rounding difference.
SEAM_TOLERANCE = 1e-4


def is_seam(edge, uv):
    """Whether an edge's two faces disagree about where they are on the texture."""
    if uv is None or len(edge.link_faces) != 2:
        return False

    first, second = edge.link_faces

    for vert in edge.verts:
        here = [loop[uv].uv for loop in first.loops if loop.vert is vert]
        there = [loop[uv].uv for loop in second.loops if loop.vert is vert]

        if not here or not there:
            continue

        if (here[0] - there[0]).length > SEAM_TOLERANCE:
            return True

    return False


def mark_bevels(obj, limit):
    """Weight the edges that are worth rounding, and only those.

    Two decisions, both of which the bevel modifier cannot make for itself on this data.

    Its own angle limit reads the raw dihedral angle, which `dihedral` exists to correct:
    it takes a flat seam between two oppositely wound quads for a fold and cuts a groove
    along it. And it has one width for every edge, where an edge that crosses a texture
    seam wants a narrower one. Marking the edges here, and setting the modifier to bevel
    by weight, puts both decisions where the measurement is.
    """
    mesh = obj.data
    name = "bevel_weight_edge"

    if name in mesh.attributes:
        mesh.attributes.remove(mesh.attributes[name])

    layer = mesh.attributes.new(name, "FLOAT", "EDGE")

    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.edges.ensure_lookup_table()
    uv = bm.loops.layers.uv.active

    weights = [0.0] * len(bm.edges)
    marked = 0

    for edge in bm.edges:
        if len(edge.link_faces) != 2:
            continue

        angle = dihedral(edge)

        if angle is not None and angle >= limit:
            weights[edge.index] = SEAM_WEIGHT if is_seam(edge, uv) else 1.0
            marked += 1

    bm.free()
    layer.data.foreach_set("value", weights)
    return marked


# The most a refined vertex may move off the chord it was cut from, as a fraction of that
# chord's length. Not a fudge: it is the exact bow of the coarsest curve this refines. A
# chord subtending an angle t on a circle of radius r is 2r sin(t/2) long and its arc
# stands r(1 - cos(t/2)) proud of it, which at CORNER's 55 degrees is 0.124 of the chord.
# Anything past that is not a curve being recovered, it is a normal pointing somewhere it
# should not.
BOW = 0.15


def subdivide_curves(obj, levels, corner):
    """Refine the curved regions, leaving every authored vertex where it is.

    Interpolating rather than approximating, which is the same choice the renderer's own
    rounding makes and for the reason recorded there: an approximating scheme moves every
    vertex towards the average of its neighbours, which is invisible on a dense mesh and
    is the whole shape on a twelve-sided lamp shade - its panels sag between their ribs
    and its rim spikes. Here the authored vertices do not move at all and each new one is
    placed on the cubic that the two ends of its edge and their normals describe: the PN
    construction, which is what `ObjectRounding` uses in the engine.

    **Blender's own smoothing is not used, and the reason is worth writing down.**
    `subdivide_edges(smooth=1.0)` offsets a new vertex along the average of its edge's
    vertex normals by an amount proportional to the edge's *length*. On a mesh whose
    normals are good that approximates the same curve; on this data it is unbounded, and
    it doubled a crumbling pillar and quadrupled the width of a telephone pole. Placing
    the vertex on the arc instead is bounded by the arc.

    Returns how many faces were refined.
    """
    refined = 0
    missed = [0]

    for _ in range(max(0, levels)):
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.faces.ensure_lookup_table()
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()

        wanted = curved_faces(bm, corner)

        if not wanted:
            bm.free()
            break

        # Sorted by index, and the edges gathered through one as well. A Python set of
        # bmesh elements iterates in memory-address order, so the same object imported
        # twice in one run subdivides its edges in two different orders and comes out
        # with two different meshes - identical to look at and not equal, which defeats
        # the content addressing the shipped set is deduplicated by. This is the only
        # place the pass had a reason to iterate a set of anything.
        faces = [bm.faces[i] for i in sorted(wanted) if i < len(bm.faces)]
        edges = [bm.edges[i] for i in sorted({e.index for f in faces for e in f.edges})]

        if not edges:
            bm.free()
            break

        centre = mathutils.Vector((0, 0, 0))

        for vert in bm.verts:
            centre = centre + vert.co

        centre = centre / max(1, len(bm.verts))
        _, find, normals = smoothing_groups(bm, centre, corner)

        # Where each new vertex belongs, keyed by the linear midpoint it is about to be
        # created at. Worked out before the split, because afterwards there is nothing
        # left to say which edge a vertex came from.
        targets = {}

        for edge in edges:
            a, b = edge.verts
            middle = (a.co + b.co) / 2.0
            span = b.co - a.co
            limit = BOW * span.length
            offset = mathutils.Vector((0, 0, 0))

            # **Both of the edge's faces are asked, and the one that bows further wins.**
            # Choosing either arbitrarily was what left every barrel in the game faceted:
            # a stave meets the lid along a rim edge, and where the lid was asked its
            # normals are all straight up, they agree with each other, and the answer is
            # that the edge is straight. It is straight *across the lid*. Across the stave
            # it is a twelfth of the way round a circle. The curve is wherever the normals
            # disagree, so the larger answer is the true one and a flat seam gives zero
            # from both sides.
            for face in edge.link_faces:
                na = normals.get(find((a.index, face.index)))
                nb = normals.get(find((b.index, face.index)))

                if na is None or nb is None or na.length < 1e-9 or nb.length < 1e-9:
                    continue

                na = na.normalized()
                nb = nb.normalized()

                # The PN cubic's two inner control points, and the point it reaches
                # halfway. Every authored vertex stays where it is; only this moves.
                b210 = (2.0 * a.co + b.co - span.dot(na) * na) / 3.0
                b120 = (2.0 * b.co + a.co + span.dot(nb) * nb) / 3.0
                candidate = ((a.co + 3.0 * b210 + 3.0 * b120 + b.co) / 8.0) - middle

                # A degenerate face has no normal worth having, and one NaN reaches the
                # exporter as a file it refuses to write - which stops the whole corpus on
                # one signpost. Checked here, where the number is made.
                if not finite(candidate):
                    continue

                if candidate.length > offset.length:
                    offset = candidate

            if offset.length > limit:
                offset = offset.normalized() * limit

            targets[quantise(middle)] = middle + offset if finite(offset) else middle

        bmesh.ops.subdivide_edges(
            bm, edges=edges, cuts=1, smooth=0.0, use_grid_fill=True)

        bm.verts.ensure_lookup_table()
        placed = 0

        for vert in bm.verts:
            target = targets.get(quantise(vert.co))
            if target is not None:
                vert.co = target
                placed += 1

        # Every edge that was split made one vertex, and every one of them has to be
        # found again. Anything else means the key is wrong, which is a silent failure:
        # the triangles are all there and the shape is the shape it started as.
        if placed < len(targets):
            missed[0] += len(targets) - placed

        refined += len(faces)
        bm.to_mesh(obj.data)
        bm.free()
        obj.data.update()

    if missed[0] > 0:
        print(f"    WARNING {missed[0]} refined vertices could not be placed")

    return refined


def finite(vector):
    """Whether a vector is a number at all."""
    return all(math.isfinite(c) for c in vector)


def quantise(point):
    """A key a vertex can be found again by, once an operator has made it.

    **A hundredth of a unit, and not the ten-thousandth this started at.** A room's
    coordinates run to a few thousand, where a 32-bit float's own resolution is about two
    ten-thousandths -- so the finer key was quantising below the precision of the numbers
    it was keying on, Blender's midpoint and this one landed either side of a rounding
    boundary, and most of the refinement silently failed to find the vertex it had just
    computed a position for. A barrel came back with more of its own facets than it went
    in with. A hundredth is forty times that resolution and a hundredth of the smallest
    thing anybody modelled.
    """
    return (round(point.x, 2), round(point.y, 2), round(point.z, 2))


def smooth_by_angle(obj, degrees):
    select_only([obj])
    try:
        bpy.ops.object.shade_smooth_by_angle(angle=math.radians(degrees))
    except (AttributeError, TypeError, RuntimeError):
        bpy.ops.object.shade_smooth()


# What a bevel may come to in world units, whatever an object's own size asks for.
#
# **A bevel that is wide enough to see across is too wide.** Every edge between two of a
# room's surfaces is a texture seam - each surface carries its own mapping - so the strip
# a bevel cuts there sweeps its texture coordinates from one side's to the other's, and
# whatever the picture holds in between is drawn along the edge. At four per cent of a
# dining table that came out as a dark dashed line across the tablecloth. At a unit or so
# it is a hairline, and a hairline is all a bevel has to be: its job is to give an edge
# somewhere to catch a highlight, not to chamfer it.
NARROWEST = 0.15
WIDEST = 1.2


def bevel(obj, width, segments, angle):
    """Round the edges that are already sharp, and only those.

    Sized from the object rather than fixed, because the things in one room differ in
    size by two orders of magnitude: a hundredth of a chair leg and a hundredth of a
    church wall are both about right, from one number, and the clamps stop either end
    running away.
    """
    mark_bevels(obj, math.radians(angle))

    size = max(obj.dimensions)

    modifier = obj.modifiers.new(name="GK3R Bevel", type="BEVEL")
    modifier.offset_type = "OFFSET"
    modifier.width = min(WIDEST, max(NARROWEST, size * width / 100.0))
    modifier.segments = segments
    modifier.limit_method = "WEIGHT"
    modifier.use_clamp_overlap = True
    modifier.harden_normals = True

    # The default sharp miter, and not the arc, because the arc and the overlap clamp
    # together are a Blender bug: on 25 objects across the corpus - the wall lanterns
    # above all - the two produce a mesh three hundred trillion units across. Either one
    # alone is fine, and the clamp is the one that has to stay, because without it a bevel
    # eats past the middle of the face it is cutting and turns a thin panel inside out.
    modifier.miter_outer = "MITER_SHARP"

    weighted = obj.modifiers.new(name="GK3R Weighted Normals", type="WEIGHTED_NORMAL")
    weighted.keep_sharp = True


# Per disposition: the bevel's angle limit, how many times a curved region is refined,
# how wide the bevel is relative to the default, and the angle its shading smooths across.
#
# Architecture gets the tightest angle limit, the narrowest bevel, no subdivision, and
# almost no smoothing. It is the case where a modifier stack does the most damage: walls
# and floors have to keep meeting exactly, and a wide bevel on an edge a wall abuts opens
# a seam you can see the room through.
#
# **The smoothing angle is the number that changes the most and shows the least.** The
# renderer shades scene geometry by each triangle's own plane, on purpose: flat is wrong
# for the few curved surfaces a room contains and right for the walls, floors and doorways
# that are nearly all of it, and it invents no smoothing groups the data never had. So the
# things that are meant to be curves — a lantern, a fountain, a moped — are the only things
# smoothed across their facets, and a stone wall keeps every one of its own. Smoothing a
# wall at forty degrees turns a lit face into a gradient and takes the whole room a stop
# darker; it looks like a lighting bug and it is a shading decision.
#
# Eight degrees is not "no smoothing": it merges the quads a flat panel was split into,
# which is what lets a bevel run along the panel's outer edge as one edge rather than as
# a row of unrelated ones.
# **`smooth` has to keep up with `corner`.** Refining a barrel and then shading it at
# forty degrees leaves its eight original staves as eight hard bands: the geometry is a
# curve and the shading still says it is a prism. Sixty is the same threshold the
# renderer's own rounding reaches for, and for the same reason recorded there - an
# eight-sided bell turns forty-five degrees at each of its own sides - while still leaving
# a shade's rim, at ninety, as the edge it is meant to be.
#
# **`corner` is what decides whether a facet is a curve or an edge**, and it is here
# rather than measured because it is not in the geometry: an eight-sided prism and a crude
# cylinder are the same mesh. Seventy takes in a six-sided lathe, which is what the moped
# shop's barrels are, and is the right answer for things that are round on purpose. Fifty
# stops below a roof ridge, which is what architecture needs.
#
# **Architecture is refined, at one level.** It was at zero, on the grounds that a modifier
# stack does the most damage to walls -- and that left every archway in the game as
# faceted as it shipped. What actually protects a wall is that its seams measure zero
# degrees and are never selected at all; the arch over the museum steps is a curve by the
# same measurement that says the wall beside it is not.
#
# One level and not two, which was tried: the second buys a barely visible improvement on
# the arches and costs the corpus nine times its triangles instead of four, because a
# building has a great many edges that measure as a gentle curve without being one. It is
# also where the drift check starts refusing things -- a long Tudor beam claimed a bow of
# 87% of its own length -- and a refusal is the pass telling you it has been let too far
# out.
TREATMENT = {
    "ornament":     {"angle": 30, "levels": 2, "width": 1.0,  "smooth": 60, "corner": 70},
    "rock":         {"angle": 35, "levels": 2, "width": 1.0,  "smooth": 60, "corner": 70},
    "vehicle":      {"angle": 30, "levels": 2, "width": 1.25, "smooth": 60, "corner": 70},
    "furniture":    {"angle": 30, "levels": 1, "width": 1.0,  "smooth": 35, "corner": 65},
    "architecture": {"angle": 45, "levels": 1, "width": 0.6,  "smooth": 8,  "corner": 50},
}


def export_glb(objects, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    select_only(objects)
    bpy.ops.export_scene.gltf(
        filepath=str(path),
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_yup=True,
        export_normals=True,
        export_texcoords=True,

        # No pictures. The composer reads the material *name*, which is where the surface
        # index lives; the picture on it is decided by that surface and is already in the
        # game. Embedding one would put a megabyte of texture into every object file to
        # say something the room already knows.
        export_image_format="NONE")


def build(source, treatment, levels, args, crease):
    """Imports one object and puts it through the stack. Returns it and its triangle counts."""
    reset_scene()
    objects = import_glb(source)

    if not objects:
        return None, 0, 0, 0

    obj = objects[0]
    before = triangle_count(obj)

    clean(obj)

    refined = subdivide_curves(obj, levels, math.radians(treatment["corner"])) if levels > 0 else 0

    smooth_by_angle(obj, min(crease, treatment["smooth"]))
    bevel(obj, args.bevel * treatment["width"], args.segments, treatment["angle"])

    # Modifiers are applied by the exporter, so the count has to come from an evaluated
    # copy. Counting the mesh as it stands would report the bevel as free.
    graph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(graph)
    after = sum(max(1, len(p.vertices) - 2) for p in evaluated.data.polygons)

    return obj, before, after, refined


def process(room, role, args, crease):
    """Improves one object. Returns a row for the report, or None when nothing was done.

    **A budget reduces the treatment; it does not discard it.** The first version of this
    refused any object that grew past the cap, and what it refused was the list of things
    most worth doing - a toothbrush, a sink, a lamppost, a chafing dish - because those are
    exactly the objects that are curves all over and so multiply fastest. Stepping down a
    level and trying again keeps the improvement and keeps the bound; only an object that
    cannot fit even with the bevel alone is left as it was.
    """
    source = Path(args.workspace) / room["directory"] / "original" / role["file"]
    target = Path(args.workspace) / room["directory"] / role["file"]

    if not source.exists():
        return None

    if target.exists() and not args.force:
        return None

    treatment = TREATMENT.get(role["disposition"], TREATMENT["furniture"])
    wanted = min(args.levels, treatment["levels"])

    for levels in range(wanted, -1, -1):
        obj, before, after, refined = build(source, treatment, levels, args, crease)

        if obj is None:
            return None

        row = {"room": room["room"], "object": role["name"], "before": before,
               "after": after, "refined": refined, "levels": levels, "skipped": None}

        if (before > 0 and after > before * args.growth) or after > args.ceiling:
            if levels > 0:
                continue

            row["skipped"] = "grew too far with no refinement at all"
            return row

        # Nothing changed, so nothing is written. An object with no sharp edge and no
        # curve in it - a plain cube of a crate - is drawn better from the original
        # geometry than from a copy of it, because the original costs no file, no read
        # and no matching at load.
        if after == before:
            row["skipped"] = "unchanged"
            return row

        if not args.dry_run:
            try:
                export_glb([obj], target)
            except RuntimeError:
                # One object the exporter refuses must not end the corpus. It is reported
                # and left as it shipped, which is what everything else here does too.
                #
                # Four signposts across the corpus do this, and only in a batch: each of
                # them exports perfectly on its own, so it is state the exporter carries
                # between calls rather than anything wrong with the mesh, whose positions,
                # normals, UVs and every attribute were checked and are all finite. The
                # message is not kept because it is a Blender traceback, not information.
                row["skipped"] = "the exporter refused it"
                return row

        return row

    return None


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = parse_args(argv)

    manifest_path = Path(args.workspace) / "manifests" / "scene-objects.json"

    if not manifest_path.exists():
        print(f"no {manifest_path}: run extract-scenes first")
        return 2

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    crease = manifest.get("crease", 40.0)

    wanted = set(IMPROVED)
    if args.dispositions:
        wanted = {d.strip() for d in args.dispositions.split(",") if d.strip()}
    if args.include_review:
        wanted.add("review")
    wanted -= NEVER

    rooms = {r.upper() for r in (args.only or [])}
    names = {n.lower() for n in (args.objects or [])}

    started = time.time()
    rows = []
    done = 0

    for room in manifest["rooms"]:
        if rooms and room["room"].upper() not in rooms:
            continue

        for role in room["objects"]:
            if role["disposition"] not in wanted:
                continue
            if names and role["name"].lower() not in names:
                continue

            row = process(room, role, args, crease)

            if row is None:
                continue

            rows.append(row)

            if row["skipped"] is None:
                done += 1
                print(f"  {row['room']:<12} {row['object'][:34]:<36} "
                      f"{row['before']:>6} -> {row['after']:>7}  "
                      f"({row['after'] / max(1, row['before']):.1f}x, "
                      f"{row['refined']} faces refined at level {row['levels']})")
            else:
                print(f"  {row['room']:<12} {row['object'][:34]:<36} "
                      f"{row['before']:>6}    {row['skipped']}")

            if args.limit and done >= args.limit:
                break

        if args.limit and done >= args.limit:
            break

    written = [r for r in rows if r["skipped"] is None]
    before = sum(r["before"] for r in written)
    after = sum(r["after"] for r in written)

    print()
    print(f"{len(written)} object(s) improved, {len(rows) - len(written)} left as they were")
    print(f"{before} -> {after} triangles ({after / max(1, before):.2f}x) "
          f"in {time.time() - started:.0f}s")

    for reason in sorted({r["skipped"] for r in rows if r["skipped"]}):
        print(f"  {sum(1 for r in rows if r['skipped'] == reason):5d}  {reason}")

    if args.dry_run:
        print("dry run: nothing was written")

    return 0


if __name__ == "__main__":
    sys.exit(main())
