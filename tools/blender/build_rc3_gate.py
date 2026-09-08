"""Builds the gateway over Rennes-le-Chateau's cemetery entrance.

    blender --background --factory-startup --python tools/blender/build_rc3_gate.py -- \
        --workspace path/to/ContentWorkspace [--table path/to/RennesLeChateau.txt] \
        [--dry-run]

Writes ``enhanced/models/RBN_RC_CEMGATE.glb`` and the one-line dressing table that puts it
in RC3. ``GK3Reborn.Content.ModelLibrary`` is where the model is found and
``GK3Reborn.Content.CutContent`` is what applies the line; the gateway is drawn only when
the model is installed, so an installation without the enhanced packs is the game as it
shipped.

What is missing, and what this puts back
----------------------------------------

RC3's way into the cemetery is the gap between the church porch's north-east corner and
the south-west end of the cemetery wall: about 75 units across, open to the sky, with
nothing over it. Rennes-le-Chateau's own entrance has a stone gateway there and, on it,
the carved pediment the place is known for -- a scrolled tympanum with a skull and
crossbones in relief and a cross above, lettered ``JHS``. Sierra modelled the wall either
side and left the doorway as a hole.

Two pieces, and neither invents anything
----------------------------------------

**The lintel** is the wall carried over the opening. It takes ``GARSTNWL``, the cemetery
wall's own rubble, at the wall's own tile size and vertical phase -- both measured off the
wall at run time -- so its courses line up with the courses either side of it. It stops
short of the wall's inner face by :data:`RIGHT_CLEARANCE` for one reason: two coplanar
faces pointing opposite ways draw in stripes, and the port keeps both sides of a model, so
the pair has to be held apart rather than culled.

**The pediment** is ``CEMENTSKULL``, the photograph of the real one, cut to its own alpha
and given the thickness the stone has. None of the carving is modelled. The silhouette is
traced off the alpha channel at the same 0.5 the shader tests, and the relief is the normal
and height maps the content pipeline derives from that same picture, exactly as for every
other surface in the game. Its rim is painted with the nearest opaque texel, which is the
rule the carved statues follow: a front-planar projection maps the rim into the transparent
margin, and the alpha test then puts a hole through the stone from every oblique angle.

The picture carries a strip of the wall's coping below the pediment proper, running off
both sides of the frame. That is dropped: a cut-out is inset from the picture it was cut
out of, so the bottom rows that reach an edge are not part of the shape.

Where it goes is measured, not typed
------------------------------------

Every number below the constants is read out of the room. ``extract-scenes`` leaves each of
RC3's objects in ``enhanced/scenes/RC3/original``; this reads two of them and finds, by
plane rather than by proximity:

* the **end cap** of ``rc3_cemwalls`` -- a vertical face standing the wall's whole height
  and only its thickness across. The wall has several; the one at the gateway is the one
  nearest the porch, and the run refuses to guess if two are close to equally near. Its
  normal is the way the gateway faces, its plane is the gateway's plane, its two extremes
  across are the wall's inner and outer faces, and its top and bottom are the wall's height
  and the ground.
* the **jamb** of ``rc3_porch`` -- the same kind of face at the end of the porch's
  north-east wall, square to the gateway and reaching the gateway's plane. The porch's far
  end is the same shape and is a hundred units out of the way, which is what rules it out.

The frame follows: **u** runs along the wall into the cemetery, **t** across the gateway.
The model is built in that frame and turned back by ``heading``. Every derived quantity is
checked, with the placement rebuilt exactly as ``SceneLoader.StandOn`` will build it, before
anything is written: a gateway a few units out is a stone slab hanging in a churchyard, and
nothing in a build log would say so.

Built in the frame the engine reads -- glTF's, with **Y up** -- and exported with the axis
conversion switched off, as ``make_props.py`` and ``carve_statues.py`` are and for the same
reason. Front is **+Z**, which is what ``heading`` turns.
"""

import argparse
import math
import os
import sys

import bmesh
import bpy
import numpy as np


# --- what the gateway is, in units ------------------------------------------------------
#
# GK3's units are about two and a half centimetres -- three independent measurements of it,
# see build_couiza.py -- so the opening comes out about 1.8 m wide and 2.1 m to the
# underside of the lintel, and the pediment about 1.9 m across.

MODEL = "RBN_RC_CEMGATE"

WALL_TEXTURE = "GARSTNWL"

PEDIMENT_TEXTURE = "CEMENTSKULL"

# The lintel. Its top is the wall's top, so the three read as one wall.
LINTEL_HEIGHT = 18.5

LINTEL_DEPTH = 13.0

# Held off the wall's inner face, the one plane the lintel would otherwise be coplanar
# with. A centimetre, and the wall itself stands behind it.
RIGHT_CLEARANCE = 0.4

# The pediment. Its height follows from the picture's own proportion, and its width is as
# near the real gateway's two and a third metres as will still stand on what is under it:
# any wider and its far end hangs off the outer face of a wall twelve units thick.
PEDIMENT_WIDTH = 96.0

PEDIMENT_DEPTH = 9.0

# Set back from the gateway's plane, so its face is not coplanar with the wall's end cap
# where the two overlap, and sunk into the lintel so its underside is not coplanar with the
# lintel's top. Both are under a centimetre, and both are a stripe that would otherwise
# flicker at the one place the eye is drawn to.
PEDIMENT_SETBACK = 0.5

PEDIMENT_SINK = 0.3

# The wall's rubble is measured at run time; this is only what the measurement is checked
# against, so a wall that has been re-cut stops the run instead of shifting the courses.
EXPECTED_TILE = 120.0

# What the alpha test in MeshShaders.cs uses. The silhouette is cut where the shader will
# cut the picture, so the shape and the paint agree.
OPAQUE = 0.5

# A row this opaque, edge to edge, is coping too -- the second half of the same rule, for
# the rows where it has run right across.
FULL_ROW = 0.98

# How far inside the cut-out the rim is painted from, in texels of the picture. Not "the
# nearest opaque texel": a rim quad's texture coordinates are interpolated *between* two of
# them, and the straight line between two points on a curved edge leaves the shape -- so the
# middle of every quad sampled the transparent margin, failed the alpha test, and put a
# speckle of holes along the edge of the stone. Six texels is deeper than any chord the
# simplified outline cuts, and what it lands on is stone rather than the edge pixels, which
# carry whatever the photograph was cut out of.
RIM_INSET = 6

# The outline is traced at about this width in texels and then simplified: finer than the
# smallest real feature -- the volutes at the ends -- and coarse enough that the whole
# pediment is a few hundred triangles.
TRACE_WIDTH = 256

# How far a traced point may sit from the line between its neighbours before it is kept, in
# texels of the traced mask. The outline arrives as a staircase, so straightening it does
# not only cost detail -- three quarters of a texel is *closer* to the silhouette than a
# half is (255 texels of disagreement against 265) and keeps a third as many points. Past
# one texel the volutes at the ends start to flatten.
SIMPLIFY = 0.75

# A full-height face no wider than this is the end of a wall rather than the side of one.
END_WIDTH = 30.0


# --------------------------------------------------------------------------------------
# Reading the room
# --------------------------------------------------------------------------------------

def to_game(v):
    """Blender's frame back to glTF's, which is the game's: (x, y, z) -> (x, z, -y)."""
    return (v[0], v[2], -v[1])


def read_object(path):
    """One of ``extract-scenes``' objects: its triangles and their texture coordinates.

    The importer converts axes and nothing downstream wants that undone twice, so the
    points are put back in the game's frame here, on the way in, and once only. Blender
    also flips v on import, and that is put back too: what this returns is what the file
    holds, which is what the engine reads.
    """
    if not os.path.exists(path):
        raise SystemExit(
            f"No {path}. Run `GK3Reborn.Tools extract-scenes` first: the gateway is "
            f"measured off the room rather than carrying its coordinates.")

    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)

    bpy.ops.import_scene.gltf(filepath=path)

    triangles = []
    points = []
    coords = []
    upright = []

    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue

        mesh = obj.data
        layer = mesh.uv_layers.active

        for polygon in mesh.polygons:
            corners = np.array([to_game(obj.matrix_world @ mesh.vertices[v].co)
                                for v in polygon.vertices])
            normal = np.array(to_game(obj.matrix_world.to_3x3() @ polygon.normal))
            length = float(np.linalg.norm(normal))

            if length < 1e-9:
                continue

            normal = normal / length
            triangles.append((normal, corners))

            for loop in polygon.loop_indices:
                points.append(to_game(mesh.vertices[mesh.loops[loop].vertex_index].co))
                coords.append((layer.data[loop].uv[0], 1.0 - layer.data[loop].uv[1])
                              if layer else (0.0, 0.0))
                upright.append(abs(normal[1]) < 0.1)

    return triangles, np.array(points), np.array(coords), np.array(upright)


def wall_ends(triangles, height, span=END_WIDTH):
    """The distinct planes of the vertical, full-height, wall-thin faces in a set.

    A wall's end is not found by looking near a point -- the opening has two of them and
    they belong to different objects -- but by what an end is: a face that stands the whole
    height and is only the wall's thickness across.
    """
    planes = []

    for normal, corners in triangles:
        if abs(normal[1]) > 0.1:
            continue

        if corners[:, 1].max() - corners[:, 1].min() < height:
            continue

        flat = corners[:, [0, 2]]

        if float(np.linalg.norm(flat[:, None, :] - flat[None, :, :], axis=-1).max()) > span:
            continue

        offset = float((corners * normal).sum(axis=1).mean())

        for plane in planes:
            if float(np.dot(plane["normal"], normal)) > 0.999 and \
                    abs(plane["offset"] - offset) < 0.5:
                plane["points"] = np.vstack([plane["points"], corners])
                break
        else:
            planes.append({"normal": normal, "offset": offset, "points": corners})

    for plane in planes:
        plane["centre"] = plane["points"].mean(axis=0)

    return planes


def tiling(points, coords, upright):
    """How far the wall's rubble runs before it repeats, and the phase of its courses.

    V is a function of height alone on every *upright* face of the cemetery wall, which is
    what lets a lintel cut to the same slope and offset carry the courses across the
    opening. The wall's copings are left out: they lie flat, so their V answers to where
    they are rather than how high, and one of them in the fit moves every course. Least
    squares over every corner of every upright face rather than one of them, so a single
    mis-read polygon cannot shift it either.
    """
    heights = points[upright, 1]
    wanted = coords[upright, 1]
    design = np.stack([heights, np.ones_like(heights)], axis=1)
    (slope, offset), *_ = np.linalg.lstsq(design, wanted, rcond=None)

    residual = float(np.abs(design @ [slope, offset] - wanted).max())

    if abs(slope) < 1e-9 or residual > 0.02:
        raise SystemExit(
            f"{WALL_TEXTURE}'s V does not run with height on the cemetery wall (worst "
            f"corner off by {residual:.3f}), so a lintel cut to it would not line up.")

    return float(1.0 / abs(slope)), float(offset), float(slope)


# --------------------------------------------------------------------------------------
# The picture
# --------------------------------------------------------------------------------------

def alpha_of(png):
    """The picture's alpha, with (0, 0) at the top left as a texture has it."""
    image = bpy.data.images.load(str(png))
    width, height = image.size
    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    bpy.data.images.remove(image)

    # Blender hands an image back bottom row first.
    return pixels.reshape(height, width, 4)[::-1, :, 3], width, height


def silhouette(alpha):
    """The pediment's own rows and columns, without the coping strip under it.

    A cut-out is inset from the picture it was cut out of; the wall's coping under this one
    runs off both sides of the frame. So the rows to drop are the ones at the bottom that
    reach an edge -- which catches the two rows where the coping has only started to widen
    as well as the ones where it has run right across.
    """
    opaque = alpha > OPAQUE
    rows = opaque.shape[0]

    cut = rows

    while cut > 0 and (opaque[cut - 1, 0] or opaque[cut - 1, -1]
                       or opaque[cut - 1].mean() >= FULL_ROW):
        cut -= 1

    if cut < rows * 0.6:
        raise SystemExit(
            f"{PEDIMENT_TEXTURE} reaches the edge of its picture for {rows - cut} of its "
            f"{rows} rows; that is not a cut-out standing on a coping, and cutting it "
            f"there would leave {cut} rows of pediment.")

    body = opaque[:cut]
    columns = np.nonzero(body.any(axis=0))[0]

    if columns.size == 0:
        raise SystemExit(f"{PEDIMENT_TEXTURE} has no opaque pixels above its coping.")

    return body, int(columns.min()), int(columns.max())


def inside(mask, depth):
    """The part of a shape at least ``depth`` texels in from its edge.

    A square erosion, done as shifts along each axis in turn because a square is separable
    and numpy carries no morphology of its own.
    """
    eroded = mask

    for axis in (0, 1):
        stacked = eroded

        for step in range(1, depth + 1):
            stacked = stacked & np.roll(eroded, step, axis=axis) \
                & np.roll(eroded, -step, axis=axis)

        # np.roll wraps, so without this the two borders are eaten by the far side of the
        # picture rather than by their own edge.
        edge = [slice(None), slice(None)]
        edge[axis] = slice(0, depth)
        stacked[tuple(edge)] = False
        edge[axis] = slice(-depth, None)
        stacked[tuple(edge)] = False

        eroded = stacked

    return eroded


def unpinch(mask):
    """Fills the diagonal pinches, where a boundary walk can cross its own path.

    Two opaque texels touching only at a corner are one shape to an eight-connected walk
    and two shapes to the outline it produces: the walk goes through the pinch twice, in
    opposite directions, and the polygon it hands back crosses itself there. A polygon that
    crosses itself cannot be triangulated -- Blender hands back a fan whose triangles face
    both ways, which is a hole in the stone that shades as though lit from behind.

    So the corner is filled. It costs a texel of silhouette at three places on this picture
    and it is the difference between a shape and a knot.
    """
    filled = mask.copy()

    for _ in range(4):
        a = filled[:-1, :-1]
        b = filled[:-1, 1:]
        c = filled[1:, :-1]
        d = filled[1:, 1:]

        pinched = (a & d & ~b & ~c) | (b & c & ~a & ~d)

        if not pinched.any():
            return filled

        # One of the two empty corners, always the same one, so the result does not depend
        # on the order the pinches are found in.
        filled[:-1, 1:] |= pinched

    raise SystemExit(
        "The silhouette still pinches after four passes; it is not one shape.")


def signed_area(points):
    """Twice the area a closed loop encloses, positive when it runs counter-clockwise."""
    x, y = points[:, 0], points[:, 1]

    return float(np.sum(x * np.roll(y, -1) - np.roll(x, -1) * y))


def crosses_itself(points):
    """Whether any two non-adjacent edges of a closed loop meet."""
    count = len(points)

    def side(p, q, r):
        return np.sign((q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0]))

    for i in range(count):
        a, b = points[i], points[(i + 1) % count]

        for j in range(i + 2, count):
            if i == 0 and j == count - 1:
                continue

            c, d = points[j], points[(j + 1) % count]

            if side(a, b, c) != side(a, b, d) and side(c, d, a) != side(c, d, b):
                return True

    return False


def trace(mask):
    """The outline of the blob in ``mask``, as a closed loop of texel corners.

    Walked along the **edges between texels** rather than from texel to texel. A walk that
    steps centre to centre goes up a one-texel spur and back down it, and the loop it hands
    back touches its own path there -- which is not a crossing anybody can see and is
    enough to make the triangulation produce a fan facing both ways. This picture has one
    such spur, at the left end of the pediment's base.

    Every corner has exactly one edge leaving it once :func:`unpinch` has run, so the walk
    has no choices to make and cannot take a wrong one. The result is a staircase, which
    :func:`simplify` straightens back into the diagonals the stone actually has.

    Returned in the same coordinates a texel centre would be, so what comes out indexes the
    picture the same way whichever walk produced it.
    """
    padded = np.zeros((mask.shape[0] + 2, mask.shape[1] + 2), dtype=bool)
    padded[1:-1, 1:-1] = mask

    step_from = {}

    for row, column in np.argwhere(padded):
        row, column = int(row), int(column)

        # Wound so that the shape is always on the same side of the edge; which side it
        # comes out on is settled afterwards, by the area the loop encloses.
        if not padded[row - 1, column]:
            step_from[(column, row)] = (column + 1, row)

        if not padded[row + 1, column]:
            step_from[(column + 1, row + 1)] = (column, row + 1)

        if not padded[row, column - 1]:
            step_from[(column, row + 1)] = (column, row)

        if not padded[row, column + 1]:
            step_from[(column + 1, row)] = (column + 1, row + 1)

    if not step_from:
        raise SystemExit("Nothing to trace.")

    if len(step_from) != sum(
            int(not padded[r - 1, c]) + int(not padded[r + 1, c])
            + int(not padded[r, c - 1]) + int(not padded[r, c + 1])
            for r, c in np.argwhere(padded)):
        raise SystemExit(
            "Two boundary edges leave one corner, so the outline forks. unpinch should "
            "have filled that corner in.")

    # The longest loop, because a shape with a hole in it has more than one and the
    # pediment's own outline is the long one. Any hole is dropped, which is what the
    # picture wants: the alpha has none.
    seen = set()
    best = []

    for start in step_from:
        if start in seen:
            continue

        loop = []
        here = start

        while here not in seen:
            seen.add(here)
            loop.append(here)
            here = step_from[here]

        if len(loop) > len(best):
            best = loop

    # Out of the padding, and off the corner lattice onto the centres the picture is
    # indexed by: corner (x, y) is half a texel before centre (x, y).
    return np.array([(y - 1 - 0.5, x - 1 - 0.5) for x, y in best], dtype=float)


def simplify(loop, tolerance):
    """Ramer-Douglas-Peucker over a closed loop, kept closed.

    Split in two before running, because the algorithm needs two fixed ends and a loop
    has none; the two halves are rejoined and the seam points are the two it was cut at.
    """
    def run(chunk):
        if len(chunk) < 3:
            return chunk[:-1].tolist()

        first, last = chunk[0], chunk[-1]
        line = last - first
        length = float(np.linalg.norm(line))
        offsets = chunk - first

        if length < 1e-9:
            distances = np.linalg.norm(offsets, axis=1)
        else:
            distances = np.abs(line[0] * offsets[:, 1] - line[1] * offsets[:, 0]) / length

        at = int(np.argmax(distances))

        if distances[at] <= tolerance:
            return [first.tolist()]

        return run(chunk[:at + 1]) + run(chunk[at:])

    half = len(loop) // 2
    kept = run(loop[:half + 1]) + run(loop[half:])

    return np.array(kept)


# --------------------------------------------------------------------------------------
# Building
# --------------------------------------------------------------------------------------

def rim_paint(texel_x, texel_y, body, tex_w, tex_h):
    """Where each point of the outline takes the colour of the rim from.

    Straight in from the edge, the same distance all the way round, so that neighbouring
    points land on neighbouring stone and the rim reads as one cut face. Taking the nearest
    deep texel instead makes each point pick its own spot and the rim comes out mottled.

    Falls back to the nearest deep texel where going straight in does not land in the
    shape, which is what happens inside the notches beside the cross.
    """
    core = inside(body, RIM_INSET)

    if not core.any():
        core = body

    points = np.stack([texel_x, texel_y], axis=1)

    # Which side of the loop the shape is on, measured rather than assumed: the loop is
    # counter-clockwise on the card and the card's y runs the other way from a texture's.
    turn = 1.0 if signed_area(points) > 0.0 else -1.0

    edges = np.roll(points, -1, axis=0) - points
    lengths = np.linalg.norm(edges, axis=1, keepdims=True)
    normals = np.divide(
        np.stack([-edges[:, 1], edges[:, 0]], axis=1) * turn, lengths,
        out=np.zeros_like(edges), where=lengths > 1e-9)

    # At a corner, the average of the two edges meeting there.
    inward = normals + np.roll(normals, 1, axis=0)
    lengths = np.linalg.norm(inward, axis=1, keepdims=True)
    inward = np.divide(inward, lengths, out=np.zeros_like(inward), where=lengths > 1e-9)

    deep_y, deep_x = np.nonzero(core)
    painted = []

    for point, step in zip(points, inward):
        at = point + step * RIM_INSET
        column, row = int(round(at[0])), int(round(at[1]))

        if not (0 <= row < core.shape[0] and 0 <= column < core.shape[1]
                and core[row, column]):
            near = int(np.argmin((deep_y - point[1]) ** 2 + (deep_x - point[0]) ** 2))
            column, row = int(deep_x[near]), int(deep_y[near])

        painted.append(((column + 0.5) / tex_w, 1.0 - (row + 0.5) / tex_h))

    return painted


def material(name):
    """A flat material whose *name* is the game texture the engine will look up.

    Reused rather than made again when one of that name is already loaded: asking Blender
    for a second material called ``GARSTNWL`` gets ``GARSTNWL.001``, the name is the whole
    binding, and the room would then be asking the archives for a bitmap that does not
    exist. This is the trap build_couiza.py found the hard way.
    """
    existing = bpy.data.materials.get(name)

    if existing is not None:
        return existing

    made = bpy.data.materials.new(name=name)
    made.use_nodes = False

    return made


def add_box(bm, uv_layer, lo, hi, slot, paint):
    """A cuboid whose faces are given texture coordinates by ``paint(face, corner)``."""
    corners = {}

    for x in (lo[0], hi[0]):
        for y in (lo[1], hi[1]):
            for z in (lo[2], hi[2]):
                corners[(x, y, z)] = bm.verts.new((x, y, z))

    x0, y0, z0 = lo
    x1, y1, z1 = hi

    quads = {
        "front": [(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)],
        "back": [(x1, y0, z0), (x0, y0, z0), (x0, y1, z0), (x1, y1, z0)],
        "left": [(x0, y0, z0), (x0, y0, z1), (x0, y1, z1), (x0, y1, z0)],
        "right": [(x1, y0, z1), (x1, y0, z0), (x1, y1, z0), (x1, y1, z1)],
        "top": [(x0, y1, z1), (x1, y1, z1), (x1, y1, z0), (x0, y1, z0)],
        "bottom": [(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)],
    }

    facing = {
        "front": (0.0, 0.0, 1.0), "back": (0.0, 0.0, -1.0),
        "left": (-1.0, 0.0, 0.0), "right": (1.0, 0.0, 0.0),
        "top": (0.0, 1.0, 0.0), "bottom": (0.0, -1.0, 0.0),
    }

    made = []

    for name, points in quads.items():
        face = bm.faces.new([corners[p] for p in points])
        face.material_index = slot

        for loop in face.loops:
            loop[uv_layer].uv = paint(name, loop.vert.co)

        made.append((face, facing[name], f"the lintel's {name}"))

    return made


def add_slab(bm, uv_layer, outline, face_uvs, rim_uvs, front_z, back_z, slot):
    """The pediment: a traced outline, its mirror behind it, and the rim between them.

    The rim is built on **its own** ring of vertices, standing exactly where the faces'
    are, and shaded smooth. A traced outline wanders a texel either way along what is a
    straight slope on the stone, so rim quads that shade flat differ by a few degrees each
    and the edge of the pediment reads as a flight of steps -- which is what it looked like
    the first time, on the one thing in the room the eye goes to. Smoothing averages that
    out. It needs its own ring because vertices shared with the front and back faces would
    average those in too, and round off a corner that is square.
    """
    front = [bm.verts.new((x, y, front_z)) for x, y in outline]
    back = [bm.verts.new((x, y, back_z)) for x, y in outline]
    rim_front = [bm.verts.new((x, y, front_z)) for x, y in outline]
    rim_back = [bm.verts.new((x, y, back_z)) for x, y in outline]

    at = {}

    for ring in (front, back, rim_front, rim_back):
        at.update({vertex: index for index, vertex in enumerate(ring)})

    face = bm.faces.new(front)
    behind = bm.faces.new(list(reversed(back)))

    for ngon in (face, behind):
        ngon.material_index = slot
        ngon.smooth = False

        for loop in ngon.loops:
            loop[uv_layer].uv = face_uvs[at[loop.vert]]

    checks = [
        (face, (0.0, 0.0, 1.0), "the pediment's face"),
        (behind, (0.0, 0.0, -1.0), "the pediment's back"),
    ]

    count = len(outline)

    for i in range(count):
        j = (i + 1) % count
        rim = bm.faces.new([rim_front[j], rim_front[i], rim_back[i], rim_back[j]])
        rim.material_index = slot
        rim.smooth = True

        for loop in rim.loops:
            loop[uv_layer].uv = rim_uvs[at[loop.vert]]

        # Out of the shape, square to the edge it stands on. Written down rather than
        # trusted to the winding, because the winding is right only while the loop runs
        # counter-clockwise, and that is the one thing that was wrong.
        dx = outline[j][0] - outline[i][0]
        dy = outline[j][1] - outline[i][1]
        length = math.hypot(dx, dy)

        if length > 1e-9:
            checks.append((rim, (dy / length, -dx / length, 0.0), f"the pediment's rim at {i}"))

    return [face, behind], checks


def check_facing(faces):
    """Every face against the way it was meant to point.

    The one fault that leaves nothing to see. Both sides of a prop are drawn, so a face
    wound the wrong way round is all there, in the right place, with the right picture on
    it -- and shaded as though the sun were on the other side of the wall. That is how the
    pediment shipped its first evening: lit on the shadowed side, dark on the sunlit one.
    """
    wrong = []

    for face, wanted, what in faces:
        got = face.normal

        if got.length < 1e-9:
            wrong.append(f"{what} has no area")
            continue

        along = (got.normalized().x * wanted[0]
                 + got.normalized().y * wanted[1]
                 + got.normalized().z * wanted[2])

        if along < 0.7:
            wrong.append(
                f"{what} points ({got.x:+.2f},{got.y:+.2f},{got.z:+.2f}) and belongs "
                f"({wanted[0]:+.2f},{wanted[1]:+.2f},{wanted[2]:+.2f})")

    if wrong:
        raise SystemExit(
            f"{len(wrong)} of {len(faces)} faces are turned the wrong way, so the gateway "
            f"would be lit from behind. Nothing was written:\n  - "
            + "\n  - ".join(wrong[:6])
            + ("\n  - ..." if len(wrong) > 6 else ""))


def check_written(path, front_z, back_z):
    """Reads the file back and measures the two faces on it, out of Blender.

    The third look at the same thing, after the exporter, because the exporter is the one
    step nothing above it can see: it triangulates, it splits and merges vertices by their
    normals, and it writes the normals the game will actually shade with.
    """
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(path))
    arrived = [o for o in bpy.data.objects if o not in before and o.type == "MESH"]

    faults = []

    for obj in arrived:
        mesh = obj.data
        mesh.calc_loop_triangles()

        for triangle in mesh.loop_triangles:
            points = [to_game(mesh.vertices[v].co) for v in triangle.vertices]
            depth = [p[2] for p in points]

            if max(depth) - min(depth) > 0.01:
                continue

            edge_a = np.subtract(points[1], points[0])
            edge_b = np.subtract(points[2], points[0])
            normal = np.cross(edge_a, edge_b)
            length = float(np.linalg.norm(normal))

            if length < 1e-9:
                continue

            wanted = 1.0 if abs(depth[0] - front_z) < 0.01 else (
                -1.0 if abs(depth[0] - back_z) < 0.01 else 0.0)

            if wanted != 0.0 and normal[2] / length * wanted < 0.7:
                faults.append(depth[0])

    for obj in arrived:
        bpy.data.objects.remove(obj, do_unlink=True)

    if faults:
        raise SystemExit(
            f"{len(faults)} triangle(s) in the written file face the wrong way. The model "
            f"on disk is not the model that was built, so it was not kept.")


# --------------------------------------------------------------------------------------
# The pass
# --------------------------------------------------------------------------------------

def measure(workspace):
    """The gateway, out of the room: its frame, its opening and the wall's own paint."""
    scenes = os.path.join(workspace, "enhanced", "scenes", "RC3", "original")

    wall_triangles, wall_points, wall_coords, wall_upright = read_object(
        os.path.join(scenes, "rc3_cemwalls.glb"))
    porch_triangles, porch_points, _, _ = read_object(
        os.path.join(scenes, "rc3_porch.glb"))

    ground = float(wall_points[:, 1].min())
    wall_top = float(wall_points[:, 1].max())
    wall_height = wall_top - ground

    porch_centre = porch_points.mean(axis=0)

    ends = wall_ends(wall_triangles, wall_height * 0.98)

    if len(ends) < 1:
        raise SystemExit("rc3_cemwalls has no full-height end face; nothing to build on.")

    ends.sort(key=lambda plane: float(np.linalg.norm(plane["centre"] - porch_centre)))
    reach = [float(np.linalg.norm(plane["centre"] - porch_centre)) for plane in ends]

    if reach[0] > 200.0 or (len(ends) > 1 and reach[1] < reach[0] * 2.0):
        raise SystemExit(
            f"The cemetery wall's ends stand {', '.join(f'{r:.0f}' for r in reach[:3])} "
            f"units from the porch; which one is the gateway is not clear, so nothing "
            f"was written.")

    cap = ends[0]
    u = -cap["normal"]
    t = np.array([u[2], 0.0, -u[0]])

    if float(((wall_points - cap["centre"]) @ u).mean()) <= 0.0:
        raise SystemExit("The wall's end cap faces along the wall rather than out of it.")

    gate_plane = float((cap["points"] @ u).mean())
    cap_t = cap["points"] @ t
    inner_t, outer_t = float(cap_t.min()), float(cap_t.max())

    jambs = [plane for plane in wall_ends(porch_triangles, wall_height * 1.02, 20.0)
             if abs(float(np.dot(plane["normal"], t))) > 0.985
             and float((plane["points"] @ u).max()) > gate_plane - 20.0]

    if len(jambs) != 1:
        raise SystemExit(
            f"rc3_porch has {len(jambs)} full-height ends square to the gateway and "
            f"reaching its plane; the opening is measured between exactly one of them "
            f"and the wall's end.")

    jamb = jambs[0]
    jamb_t = float((jamb["points"] @ t).mean())
    opening = inner_t - jamb_t

    if not 50.0 < opening < 110.0:
        raise SystemExit(
            f"The opening measures {opening:.1f} units between the porch and the wall, "
            f"which is not a doorway. Nothing was written.")

    if float((jamb["points"] @ u).max()) > gate_plane + 0.5:
        raise SystemExit(
            "The porch's jamb reaches past the gateway's plane, so a lintel standing in "
            "that plane would be inside the porch.")

    tile, phase, slope = tiling(wall_points, wall_coords, wall_upright)

    if abs(tile - EXPECTED_TILE) > 1.0:
        raise SystemExit(
            f"{WALL_TEXTURE} tiles every {tile:.1f} units on the cemetery wall, not the "
            f"{EXPECTED_TILE:.0f} the lintel is cut for.")

    return {
        "u": u, "t": t, "ground": ground, "top": wall_top,
        "gate_plane": gate_plane, "inner_t": inner_t, "outer_t": outer_t,
        "jamb_t": jamb_t, "opening": opening,
        "tile": tile, "phase": phase, "slope": slope,
    }


def build(workspace, table_path, dry_run):
    room = measure(workspace)

    centre_t = (room["jamb_t"] + room["inner_t"]) / 2.0
    gate_plane = room["gate_plane"]
    lintel_bottom = room["top"] - LINTEL_HEIGHT

    # x runs across the gateway against t, and z out of it against u, so that +Z is the
    # way the gateway faces and the turn about Y that puts it back is a right-handed one.
    # y is up from the lintel's underside.
    def across(value):
        return -(value - centre_t)

    def through(value):
        return -(value - gate_plane)

    picture = os.path.join(workspace, "enhanced", "textures", f"{PEDIMENT_TEXTURE}.PNG")

    if not os.path.exists(picture):
        picture = os.path.join(workspace, "enhanced", "textures", f"{PEDIMENT_TEXTURE}.png")

    alpha, tex_w, tex_h = alpha_of(picture)
    body, col_lo, col_hi = silhouette(alpha)

    crop = body[:, col_lo:col_hi + 1]
    crop_h, crop_w = crop.shape
    pediment_height = PEDIMENT_WIDTH * crop_h / crop_w

    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)

    mesh = bpy.data.meshes.new(MODEL)
    obj = bpy.data.objects.new(MODEL, mesh)
    bpy.context.scene.collection.objects.link(obj)
    mesh.materials.append(material(WALL_TEXTURE))
    mesh.materials.append(material(PEDIMENT_TEXTURE))

    bm = bmesh.new()
    uv_layer = bm.loops.layers.uv.new("UVMap")

    # --- the lintel ---------------------------------------------------------------------

    corners = [
        (across(room["inner_t"] - RIGHT_CLEARANCE), 0.0, through(gate_plane + LINTEL_DEPTH)),
        (across(room["jamb_t"]), LINTEL_HEIGHT, through(gate_plane)),
    ]

    lintel_lo = tuple(min(a, b) for a, b in zip(*corners))
    lintel_hi = tuple(max(a, b) for a, b in zip(*corners))

    def rubble(face, corner):
        # V is the wall's own, so the courses carry across the opening. Blender stores
        # 1 - v of what the exporter writes and the picture's v runs down, so the two flips
        # cancel and only this one is here. A face lying flat has no height to take V from
        # and is given the depth instead; nobody sees the top of a lintel under a pediment.
        world_y = lintel_bottom + corner[1]
        world_t = centre_t - corner[0]
        world_u = gate_plane - corner[2]

        if face in ("top", "bottom"):
            return (world_t / room["tile"], 1.0 - world_u / room["tile"])

        along = world_u if face in ("left", "right") else world_t

        return (along / room["tile"], 1.0 - (room["phase"] + room["slope"] * world_y))

    facing = add_box(bm, uv_layer, lintel_lo, lintel_hi, 0, rubble)

    # --- the pediment -------------------------------------------------------------------

    step = max(1, crop_w // TRACE_WIDTH)
    kept = simplify(trace(unpinch(crop[::step, ::step])), SIMPLIFY)

    base = LINTEL_HEIGHT - PEDIMENT_SINK

    def carded(loop):
        """One traced point on the card, and the texel of the whole picture it came from.

        A traced point is the centre of a texel of the reduced mask, which is a block of
        `step` texels of the original; the half-step is what puts it back in the middle of
        that block rather than at its corner.
        """
        ty = (loop[:, 0] + 0.5) * step - 0.5
        tx = (loop[:, 1] + 0.5) * step - 0.5 + col_lo

        return np.stack([
            ((tx - col_lo) / (crop_w - 1) - 0.5) * PEDIMENT_WIDTH,
            base + (1.0 - ty / (crop_h - 1)) * pediment_height,
        ], axis=1), tx, ty

    points, texel_x, texel_y = carded(kept)

    # Which way round the loop runs decides which way the pediment is lit, and nothing
    # about the model looks wrong when it is inverted: both sides of a prop are drawn, so
    # the stone is all there and shaded as though the sun were behind it. The trace runs
    # clockwise in the picture, where y grows downward, and the card's y grows up -- but
    # that is worked out here rather than reasoned about, because reasoning about it is
    # what put the sunlit face in shadow the first time.
    if signed_area(points) < 0.0:
        kept = kept[::-1]
        points, texel_x, texel_y = carded(kept)

    if signed_area(points) <= 0.0:
        raise SystemExit("The pediment's outline encloses nothing.")

    if crosses_itself(points):
        raise SystemExit(
            "The pediment's outline crosses itself, so it cannot be triangulated into a "
            "face. Simplifying it less, or a coarser trace, will usually settle it.")

    outline = [tuple(p) for p in points]

    face_uvs = [(tx / tex_w, 1.0 - ty / tex_h) for tx, ty in zip(texel_x, texel_y)]

    rim_uvs = rim_paint(texel_x, texel_y, body, tex_w, tex_h)

    ngons, slab_facing = add_slab(
        bm, uv_layer, outline, face_uvs, rim_uvs,
        through(gate_plane + PEDIMENT_SETBACK),
        through(gate_plane + PEDIMENT_SETBACK + PEDIMENT_DEPTH),
        1)

    facing.extend(slab_facing)

    # Before triangulating, because a triangulated n-gon is no longer one face to ask.
    # `normal_update` derives each face's normal from the winding it was given; it is not
    # `recalc_face_normals`, which reorients them -- and reorienting is exactly what cannot
    # be done here, because the rim's own ring makes the mesh non-manifold and leaves that
    # pass nothing to work from.
    bm.normal_update()
    check_facing(facing)

    bmesh.ops.triangulate(bm, faces=ngons)

    bm.to_mesh(mesh)
    bm.free()

    for slot, wanted in enumerate((WALL_TEXTURE, PEDIMENT_TEXTURE)):
        if mesh.materials[slot].name != wanted:
            raise SystemExit(
                f"The material in slot {slot} is called {mesh.materials[slot].name}, not "
                f"{wanted}. The name is the texture the engine looks up, so the gateway "
                f"would ask the archives for a bitmap that does not exist.")

    # --- where it goes ------------------------------------------------------------------
    #
    # Off the box the model came out as, not the box it was meant to be: StandOn centres a
    # prop on pos in x and z and stands its lowest point on pos.y, so the coordinate has to
    # answer to the mesh.

    points = np.array([v.co[:] for v in mesh.vertices])
    lo, hi = points.min(axis=0), points.max(axis=0)

    world = (centre_t - (lo[0] + hi[0]) / 2.0) * room["t"] \
        + (gate_plane - (lo[2] + hi[2]) / 2.0) * room["u"]

    position = (float(world[0]), lintel_bottom + float(lo[1]), float(world[2]))
    heading = math.degrees(math.atan2(-room["u"][0], -room["u"][2])) % 360.0

    # Three corners whose place in the room was settled before any of this was built, so
    # that where they land is a test of the frame rather than a restatement of it.
    landmarks = [
        ("lintel's near bottom corner at the porch",
         (across(room["jamb_t"]), 0.0, through(gate_plane)),
         (room["jamb_t"], gate_plane, lintel_bottom)),
        ("lintel's far top corner at the wall",
         (across(room["inner_t"] - RIGHT_CLEARANCE), LINTEL_HEIGHT,
          through(gate_plane + LINTEL_DEPTH)),
         (room["inner_t"] - RIGHT_CLEARANCE, gate_plane + LINTEL_DEPTH, room["top"])),
        ("pediment's foot in the middle of the opening",
         (across(centre_t), base, through(gate_plane + PEDIMENT_SETBACK)),
         (centre_t, gate_plane + PEDIMENT_SETBACK, room["top"] - PEDIMENT_SINK)),
    ]

    verify(position, heading, points, room, centre_t, landmarks)

    print(f"  opening      {room['opening']:.1f} across, "
          f"{lintel_bottom - room['ground']:.1f} to the lintel, "
          f"wall {room['top'] - room['ground']:.1f} tall")
    print(f"  lintel       {hi[0] - lo[0]:.1f} x {LINTEL_HEIGHT:.1f} x {LINTEL_DEPTH:.1f}, "
          f"{WALL_TEXTURE} every {room['tile']:.0f} units")
    print(f"  pediment     {PEDIMENT_WIDTH:.1f} x {pediment_height:.1f} x {PEDIMENT_DEPTH:.1f}, "
          f"{len(outline)} points off {PEDIMENT_TEXTURE} "
          f"({crop_w}x{crop_h} of {tex_w}x{tex_h} texels)")
    print(f"  model        {len(mesh.polygons)} faces, "
          f"{hi[0] - lo[0]:.1f} x {hi[1] - lo[1]:.1f} x {hi[2] - lo[2]:.1f} units")
    print(f"  placed       pos={{{position[0]:.2f},{position[1]:.2f},{position[2]:.2f}}}, "
          f"heading={heading:.2f}")

    if dry_run:
        return

    out_dir = os.path.join(workspace, "enhanced", "models")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, MODEL + ".glb")

    for other in bpy.data.objects:
        other.select_set(other is obj)

    bpy.context.view_layer.objects.active = obj

    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        use_selection=True,
        export_yup=False,
        export_apply=True,
        export_materials="EXPORT",
        export_image_format="NONE",
        export_normals=True,
        export_texcoords=True,
    )

    check_written(path,
                  through(gate_plane + PEDIMENT_SETBACK),
                  through(gate_plane + PEDIMENT_SETBACK + PEDIMENT_DEPTH))

    print(f"  wrote {path}")

    write_table(table_path, position, heading)

    print(f"  wrote {table_path}")


def footing_of(points):
    """The point StandOn puts on ``pos``: the middle of the box in x and z, its floor."""
    lo, hi = points.min(axis=0), points.max(axis=0)

    return np.array([(lo[0] + hi[0]) / 2.0, lo[1], (lo[2] + hi[2]) / 2.0])


def placed_at(points, footing, position, heading):
    """Where a model's points land once the scene file has placed it.

    SceneLoader.StandOn's transform, rebuilt rather than trusted: this is the one step
    where a sign or a swapped axis produces a model that is not visibly wrong in isolation
    and a slab of stone in mid-air in the room. The footing is passed in rather than taken
    from ``points``, so that a single corner can be put through the same transform as the
    whole model instead of being centred on itself.
    """
    radians = math.radians(heading)
    sin, cos = math.sin(radians), math.cos(radians)

    local = points - footing

    return np.stack([
        local[:, 0] * cos + local[:, 2] * sin + position[0],
        local[:, 1] + position[1],
        -local[:, 0] * sin + local[:, 2] * cos + position[2],
    ], axis=1)


def verify(position, heading, points, room, centre_t, landmarks):
    """Measures the gateway where the room will actually have it.

    Two kinds of check, and both are needed. The landmarks say that three corners whose
    place in the room was decided before any of this was built -- the lintel's near
    bottom corner at the porch, its far top corner at the wall, and the pediment's foot at
    the middle of the opening -- come out where they were meant to; that is what catches a
    turned or mirrored frame. The ranges then say the whole of it fits between the two
    jambs, stands on the wall rather than in front of it, and has something under every
    part of it.
    """
    problems = []
    footing = footing_of(points)

    for name, point, wanted in landmarks:
        landed = placed_at(np.array([point]), footing, position, heading)[0]
        got = (float(landed @ room["t"]), float(landed @ room["u"]), float(landed[1]))

        if max(abs(a - b) for a, b in zip(got, wanted)) > 0.02:
            problems.append(
                f"the {name} lands at t={got[0]:.2f} u={got[1]:.2f} y={got[2]:.2f}, and "
                f"belongs at t={wanted[0]:.2f} u={wanted[1]:.2f} y={wanted[2]:.2f}")

    placed = placed_at(points, footing, position, heading)
    along = placed @ room["t"]
    depth = placed @ room["u"]
    height = placed[:, 1]

    if float(depth.min()) < room["gate_plane"] - 0.01:
        problems.append(
            f"it stands {room['gate_plane'] - float(depth.min()):.2f} units out of the "
            f"gateway's plane, in front of the wall's end")

    if float(depth.max()) > room["gate_plane"] + LINTEL_DEPTH + 0.01:
        problems.append(
            f"it reaches {float(depth.max()) - room['gate_plane']:.2f} units into the "
            f"cemetery, which is deeper than the lintel")

    if abs(float(height.min()) - (room["top"] - LINTEL_HEIGHT)) > 0.01:
        problems.append(
            f"its underside stands at {float(height.min()):.2f}, not at "
            f"{room['top'] - LINTEL_HEIGHT:.2f}")

    if float(height.max()) <= room["top"]:
        problems.append("nothing of it stands above the wall, so there is no pediment")

    # Half a unit, because the anchor is the box's middle and the traced outline is
    # symmetric only to the nearest texel.
    edge = PEDIMENT_WIDTH / 2.0 + 0.5

    if float(along.min()) < centre_t - edge or float(along.max()) > centre_t + edge:
        problems.append(
            f"it spans {float(along.min()):.1f}..{float(along.max()):.1f} across the "
            f"gateway, wider than the {PEDIMENT_WIDTH:.0f} units it was cut to")

    if float(along.max()) > room["outer_t"] + 0.01:
        problems.append(
            f"it overhangs the wall's outer face by "
            f"{float(along.max()) - room['outer_t']:.1f} units, with nothing under it")

    overhang = (PEDIMENT_WIDTH - room["opening"]) / 2.0

    if overhang > 12.0:
        problems.append(
            f"the pediment overhangs each jamb by {overhang:.1f} units, far enough to be "
            f"seen past the porch's corner")

    if problems:
        raise SystemExit(
            "The gateway does not fit the opening it was measured from, so nothing was "
            "written:\n  - " + "\n  - ".join(problems))


TABLE = """\
# The gateway over Rennes-le-Chateau's cemetery entrance, which GK3 left as a hole.
#
# GENERATED by tools/blender/build_rc3_gate.py. Change the gateway there, not here.
#
# RC3's way into the cemetery is the gap between the church porch's corner and the end of
# the cemetery wall. The real entrance has a stone gateway over it and, on the gateway, the
# carved pediment the place is known for: a scrolled tympanum with a skull and crossbones
# and a cross above it. Sierra modelled the wall either side and left the doorway open to
# the sky. This puts a lintel across it in the wall's own rubble and stands the pediment on
# that, cut out of CEMENTSKULL.
#
# This is not cut content and is not in CutContent.txt: nobody at Sierra wrote, recorded or
# modelled it. It is applied only when the model it names is installed -- see SceneDressing
# -- so an installation without the enhanced content packs is exactly the game as it
# shipped, with no diagnostic about a model that is not there.
#
# type=prop, and it has to be: GK3's type=scene means "an object already inside the room's
# BSP" and loads no file at all. See SceneLoader.IsBakedIn.
#
# noun=EXIT2 with verb=EXIT_LEFT, which is the binding rc3_exittocem already carries and
# RC3_ALL.NVC already has a rule for, so the gateway is the cemetery door rather than a
# prop standing in front of one: a click on it walks Gabriel to TO_CEM and through. Without
# it the pediment would swallow every click that met it, because ScenePicker takes the
# nearest target whether it answers to anything or not.

[RC3_CEMETERY_GATE]
append RC3.SIF MODELS model={model}, noun=EXIT2, type=prop, verb=EXIT_LEFT, pos={{{x:.2f},{y:.2f},{z:.2f}}}, heading={heading:.2f}
"""


def write_table(path, position, heading):
    os.makedirs(os.path.dirname(path), exist_ok=True)

    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(TABLE.format(
            model=MODEL, x=position[0], y=position[1], z=position[2], heading=heading))


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--table", default=None,
                        help="where the dressing table goes; default is beside the engine")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    table = args.table or os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        "src", "GK3Reborn.Engine", "Assets", "Story", "RennesLeChateau.txt")

    build(args.workspace, table, args.dry_run)

    return 0


if __name__ == "__main__":
    sys.exit(main())
