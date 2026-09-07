"""Turns the church's sculpted saints into props the game can place.

    blender --background --factory-startup --python tools/blender/carve_statues.py -- \
        --workspace path/to/ContentWorkspace [--only NAME ...] [--faces 6000] [--dry-run]

Takes the raw shapes ``PbrLab/make_statues.py`` left in ``enhanced/statues`` -- half a
million triangles apiece, in no particular place -- and writes one finished model per
statue into ``enhanced/models``, which is where ``GK3Reborn.Content.ModelLibrary`` looks.
What it decides is everything except the shape: how many triangles, which way the statue
faces, how big it is, where it stands, and which pixels are painted on it.

Everything is done in the frame the engine reads -- glTF's, with **Y up** -- and exported
with the axis conversion switched off, exactly as ``make_props.py`` does and for the same
reason: which way Blender converts is a setting whose effect is invisible until a saint is
lying on his back in a niche. The importer *does* convert, so what it hands back is turned
into the game's frame once, by hand, on the way in.

Five decisions, each taken from something the game already ships
---------------------------------------------------------------

**Where it stands** is the card's own placement. Each of these five ships as a ``.MOD``
holding one quad, and that quad already carries a matrix putting it in the niche. The
carved statue is centred on exactly the same rectangle, so it stands on the same pedestal
at the same height and nothing about the room has to move.

**How big it is** is the card's own rectangle, 18 units across by 42 tall. The picture is
1:2 and the card is 1:2.33, so the statue was always drawn a sixth taller than the
photograph it was cut from; matching the card rather than the picture keeps that, because
the lightmaps, the pedestal and the niche were all built around the card. Depth is scaled
with the width, so the figure keeps its own proportion front to back.

**Which way it faces** is the inspect camera. A billboard has no true facing -- that is
what makes it a billboard -- so there is nothing in the model to read. But ``CHU.SIF``
puts a camera in front of every one of these five for the player to look at it from, and
the direction from the statue to that camera is the direction the designers meant it to
present. It even recovers the small turn on St Antoine de Padoue, whose niche is not
square to the nave.

**What it is painted with** is the game's own bitmap, projected straight on from the
front, in the card's own rectangle. The sculpt was made from that picture, so a front-on
projection puts every pixel back where it came from -- and the normal, ORM and height maps
the content pipeline derives from the same picture go on working unchanged.

**What its sides are painted with** is the nearest painted pixel. A statue is nearly a
sixth as deep as it is wide, and a flat projection maps its rim to the very edge of the
cut-out where the alpha has already fallen to nothing -- so the sides would be drawn and
then thrown away by the alpha test, leaving a statue with a hole through it from every
oblique angle. Every vertex that projects into the transparent margin is pulled to the
nearest opaque texel instead, which is the colour of the edge it belongs to. This is the
same rule the cutout-card rims follow; see ``docs/cutout-cards.md``.
"""

import argparse
import json
import math
import pathlib
import re
import sys

import bmesh
import bpy
import numpy as np

# Every statue: the model a scene file asks for, the texture it is painted with, and the
# room it is in. Kept here rather than derived because the set is the point -- this pass
# exists for these five and refuses to guess at a sixth.
STATUES = [
    {"model": "CHU_STROCHFF", "texture": "CHUROCH", "room": "CHU", "noun": "ST_ROCH"},
    {"model": "CHU_STEGERMAINEFF", "texture": "CHUGERMAINE", "room": "CHU", "noun": "ST_GERMAINE"},
    {"model": "CHU_STERMITEFF", "texture": "CHUANTE", "room": "CHU", "noun": "ST_ANTHONY"},
    {"model": "CHU_ANTIONEFF", "texture": "CHUANTP", "room": "CHU", "noun": "ST_ANTHONY_DE_PADOUE"},
    {"model": "CHU_STEMADELEINEFF", "texture": "CHUMARYM", "room": "CHU", "noun": "ST_MAGDALEN_STATUE"},
]

# A statue is an 18-by-42-unit object a player walks up to and reads, so it is worth more
# than a chair and much less than a character. Six thousand is where the drapery stops
# improving; the five together cost about a tenth of what the room's floor relief does.
FACE_BUDGET = 6000

# Anything smaller than this share of the mesh is a speck the marching cubes left behind
# in the transparent margin, not part of the saint.
SPECK = 0.004

# The alpha is read at this height to snap the rim UVs against. Full resolution answers the
# same question a hundred times slower: a 2,048-row mask against six thousand vertices is
# twelve billion distances.
MASK_ROWS = 512
OPAQUE = 0.03


# --------------------------------------------------------------------------------------
# The game's frame
# --------------------------------------------------------------------------------------

def to_game(v):
    """Blender's Z-up back into the game's Y-up, undoing what the glTF importer did."""
    return np.stack([v[:, 0], v[:, 2], -v[:, 1]], axis=1)


def yaw_to(direction):
    """The turn about Y that takes the sculpt's front, +Z, to point along ``direction``."""
    return math.atan2(direction[0], direction[2])


# --------------------------------------------------------------------------------------
# What the scene says
# --------------------------------------------------------------------------------------

SECTION = re.compile(r"^\[([A-Z_]+)")
FIELD = re.compile(r"(\w+)\s*=\s*(\{[^}]*\}|[^,]*)")


def inspect_cameras(sif_path):
    """Every ``noun -> where the player is asked to stand`` in a scene file."""
    found = {}
    section = None

    for line in sif_path.read_text(errors="replace").splitlines():
        line = line.split("//")[0].strip()

        if not line:
            continue

        if line.startswith("["):
            section = SECTION.match(line).group(1) if SECTION.match(line) else None
            continue

        if section != "INSPECT_CAMERAS":
            continue

        fields = {k.lower(): v.strip() for k, v in FIELD.findall(line)}

        if "noun" in fields and "pos" in fields:
            numbers = [float(n) for n in re.findall(r"-?\d*\.?\d+", fields["pos"])]

            if len(numbers) == 3:
                found[fields["noun"].upper()] = np.array(numbers)

    return found


# --------------------------------------------------------------------------------------
# Blender
# --------------------------------------------------------------------------------------

def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def load(path):
    """Imports a GLB and returns one joined mesh object, or None."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(path))
    added = [o for o in set(bpy.data.objects) - before if o.type == "MESH"]

    if not added:
        return None

    for obj in bpy.data.objects:
        obj.select_set(obj in added)

    bpy.context.view_layer.objects.active = added[0]

    if len(added) > 1:
        bpy.ops.object.join()

    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    return obj


def positions(obj):
    flat = np.empty(len(obj.data.vertices) * 3, dtype=np.float64)
    obj.data.vertices.foreach_get("co", flat)
    return flat.reshape(-1, 3)


def replace(obj, points):
    obj.data.vertices.foreach_set("co", points.reshape(-1).astype(np.float32))
    obj.data.update()


def weld(obj, distance=1e-5):
    """Joins the coincident vertices a GLB always arrives with.

    glTF stores one vertex per corner of every triangle, so a marching-cubes surface
    imports as half a million loose triangles that only look joined. Nothing downstream
    works on that: island-finding sees every face as its own island, and decimation
    collapses faces while leaving every original vertex behind -- five hundred thousand
    orphans on a six-thousand-triangle mesh, which is what made the rim snap take a minute
    and cover the whole picture.
    """
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    before = len(mesh.verts)
    bmesh.ops.remove_doubles(mesh, verts=mesh.verts, dist=distance)
    after = len(mesh.verts)
    mesh.to_mesh(obj.data)
    mesh.free()
    obj.data.update()

    return before - after


def loose(obj):
    """Drops vertices no face uses, which is what a collapse decimation leaves."""
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    orphans = [v for v in mesh.verts if not v.link_faces]

    if orphans:
        bmesh.ops.delete(mesh, geom=orphans, context="VERTS")

    mesh.to_mesh(obj.data)
    mesh.free()
    obj.data.update()

    return len(orphans)


def drop_specks(obj, share=SPECK):
    """Removes loose islands too small to be part of the figure."""
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)

    islands = []
    seen = set()

    for face in mesh.faces:
        if face.index in seen:
            continue

        stack, island = [face], []

        while stack:
            here = stack.pop()

            if here.index in seen:
                continue

            seen.add(here.index)
            island.append(here)

            for edge in here.edges:
                for other in edge.link_faces:
                    if other.index not in seen:
                        stack.append(other)

        islands.append(island)

    total = len(mesh.faces)
    dropped = [f for island in islands if len(island) < share * total for f in island]

    if dropped and len(dropped) < total:
        bmesh.ops.delete(mesh, geom=dropped, context="FACES")

    mesh.to_mesh(obj.data)
    mesh.free()

    return len(dropped)


def decimate(obj, budget):
    """Collapses the sculpt down to the budget, triangulating as it goes.

    Collapse rather than planar: a marching-cubes surface has no flat regions to merge, so
    a planar decimation of half a million triangles returns half a million triangles.
    """
    faces = len(obj.data.polygons)

    if faces <= budget:
        return faces

    modifier = obj.modifiers.new(name="carve", type="DECIMATE")
    modifier.decimate_type = "COLLAPSE"
    modifier.ratio = budget / faces
    modifier.use_collapse_triangulate = True

    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=modifier.name)

    return len(obj.data.polygons)


def smooth(obj):
    """Smooth shading throughout, so decimated drapery reads as cloth and not as facets.

    No crease angle. A marching-cubes surface has no authored creases to keep, and a
    threshold picked here would only decide which decimation artefacts get a hard edge
    drawn round them.
    """
    for face in obj.data.polygons:
        face.use_smooth = True

    obj.data.update()


# --------------------------------------------------------------------------------------
# Painting
# --------------------------------------------------------------------------------------

def alpha_mask(png, rows=MASK_ROWS):
    """The picture's alpha, small, with (0, 0) at the top left as a texture has it."""
    image = bpy.data.images.load(str(png))
    width, height = image.size
    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    bpy.data.images.remove(image)

    # Blender hands an image back bottom row first.
    alpha = pixels.reshape(height, width, 4)[::-1, :, 3]

    step = max(1, height // rows)

    return alpha[::step, ::step]


def snap_uvs(uv, mask):
    """Pulls every UV that lands in the transparent margin onto the nearest painted texel.

    Everything here is in **Blender's** UV space, where v grows upwards, because that is
    what has to be stored: the glTF exporter writes ``1 - v`` and the picture's own v grows
    downwards, so the two flips cancel and only one of them is this file's business.
    Storing the game's own downward v instead flips every statue's picture -- and with it
    the alpha, so each was cut to the silhouette of its own reflection.
    """
    rows, cols = mask.shape
    opaque = mask > OPAQUE

    if not opaque.any():
        return uv, 0

    u = np.clip(uv[:, 0], 0.0, 1.0)
    v = np.clip(uv[:, 1], 0.0, 1.0)

    xi = np.clip((u * (cols - 1)).astype(int), 0, cols - 1)
    yi = np.clip(((1.0 - v) * (rows - 1)).astype(int), 0, rows - 1)

    outside = ~opaque[yi, xi]

    if not outside.any():
        return uv, 0

    ys, xs = np.nonzero(opaque)
    # Distances in texel space, which is what "nearest painted pixel" means; the picture is
    # 1:2 and so is the mask, so no aspect correction is wanted.
    for index in np.nonzero(outside)[0]:
        d = (ys - yi[index]) ** 2 + (xs - xi[index]) ** 2
        near = int(np.argmin(d))
        uv[index, 0] = (xs[near] + 0.5) / cols
        uv[index, 1] = 1.0 - (ys[near] + 0.5) / rows

    return uv, int(outside.sum())


def project(obj, points, centre, across, up, width, height, mask):
    """Front-planar UVs in the card's own rectangle, then snapped off the margin.

    Takes the points rather than reading them back off the object, because by now the
    object holds the game's coordinates directly -- the export does not convert -- and
    reading them back through :func:`to_game` would turn the statue on its side a second
    time. That is not visible in the geometry, which is already correct; it lands the
    whole projection in the transparent margin and paints the saint in the nearest edge
    colour from head to foot.
    """
    local = points - centre

    u = np.clip(local @ across / width + 0.5, 0.0, 1.0)
    v = np.clip(local @ up / height + 0.5, 0.0, 1.0)

    uv = np.stack([u, v], axis=1)
    uv, snapped = snap_uvs(uv, mask)

    layer = obj.data.uv_layers.new(name="UVMap")
    loops = np.empty(len(obj.data.loops), dtype=np.int32)
    obj.data.loops.foreach_get("vertex_index", loops)
    layer.uv.foreach_set("vector", uv[loops].reshape(-1).astype(np.float32))

    return snapped


def paint(obj, texture):
    """One material, named after the texture. The name is the whole binding."""
    obj.data.materials.clear()
    material = bpy.data.materials.get(texture) or bpy.data.materials.new(name=texture)
    material.use_nodes = False
    obj.data.materials.append(material)


# --------------------------------------------------------------------------------------
# The pass
# --------------------------------------------------------------------------------------

def carve(statue, workspace, budget):
    model, texture = statue["model"], statue["texture"]

    sculpt = workspace / "enhanced" / "statues" / texture / "sculpt.glb"
    card = workspace / "normalized" / "models" / f"{model}.glb"
    picture = workspace / "enhanced" / "textures" / f"{texture}.PNG"
    scene = workspace / "normalized" / "scenes" / statue["room"] / f"{statue['room']}.SIF"

    for needed in (sculpt, card, picture, scene):
        if not needed.exists():
            return {"model": model, "skipped": f"no {needed}"}

    clear()

    # The card first, for the rectangle it occupies.
    quad = load(card)

    if quad is None:
        return {"model": model, "skipped": "the card has no mesh"}

    corners = to_game(positions(quad))
    centre = (corners.min(0) + corners.max(0)) / 2.0
    span = corners.max(0) - corners.min(0)

    height = span[1]
    width = max(span[0], span[2])

    if height <= 0 or width <= 0:
        return {"model": model, "skipped": "the card has no extent"}

    cameras = inspect_cameras(scene)
    where = cameras.get(statue["noun"])

    if where is None:
        return {"model": model, "skipped": f"no inspect camera for {statue['noun']}"}

    facing = np.array([where[0] - centre[0], 0.0, where[2] - centre[2]])
    reach = np.linalg.norm(facing)

    if reach < 1e-3:
        return {"model": model, "skipped": "the inspect camera stands on the statue"}

    facing /= reach
    across = np.array([facing[2], 0.0, -facing[0]])          # right-hand across the front
    up = np.array([0.0, 1.0, 0.0])

    clear()

    obj = load(sculpt)

    if obj is None:
        return {"model": model, "skipped": "the sculpt has no mesh"}

    raw = len(obj.data.polygons)
    welded = weld(obj)
    specks = drop_specks(obj)
    kept = decimate(obj, budget)
    orphans = loose(obj)
    smooth(obj)

    points = to_game(positions(obj))
    low, high = points.min(0), points.max(0)
    size = high - low

    if min(size) <= 0:
        return {"model": model, "skipped": "the sculpt is flat"}

    # Across and up to the card; through with the same factor as across, so the figure
    # keeps its own depth rather than being squashed to whatever the niche is.
    scale = np.array([width / size[0], height / size[1], width / size[0]])
    points = (points - (low + high) / 2.0) * scale

    turn = yaw_to(facing)
    cos, sin = math.cos(turn), math.sin(turn)
    spun = np.stack([
        points[:, 0] * cos + points[:, 2] * sin,
        points[:, 1],
        -points[:, 0] * sin + points[:, 2] * cos,
    ], axis=1)

    placed = spun + centre
    replace(obj, placed)

    depth = size[2] * scale[2]
    snapped = project(obj, placed, centre, across, up, width, height, alpha_mask(picture))
    paint(obj, texture)

    return {
        "model": model, "texture": texture, "room": statue["room"],
        "at": [round(float(n), 3) for n in centre],
        "facing": [round(float(n), 4) for n in facing],
        "size": [round(float(width), 2), round(float(height), 2), round(float(depth), 2)],
        "triangles": kept, "from": raw, "vertices": len(obj.data.vertices),
        "welded": welded, "specks": specks, "orphans": orphans, "snapped": snapped,
        "object": obj,
    }


def export(obj, path):
    path.parent.mkdir(parents=True, exist_ok=True)

    for other in bpy.data.objects:
        other.select_set(other is obj)

    bpy.context.view_layer.objects.active = obj

    bpy.ops.export_scene.gltf(
        filepath=str(path),
        export_format="GLB",
        use_selection=True,
        export_yup=False,
        export_apply=True,
        export_materials="EXPORT",
        export_image_format="NONE",
        export_normals=True,
        export_texcoords=True,
    )


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--only", nargs="*", default=None)
    parser.add_argument("--faces", type=int, default=FACE_BUDGET)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    workspace = pathlib.Path(args.workspace)
    wanted = {name.upper() for name in args.only} if args.only else None

    records = []

    for statue in STATUES:
        if wanted and statue["model"] not in wanted and statue["texture"] not in wanted:
            continue

        if args.dry_run:
            print(f"  {statue['model']}: would carve from {statue['texture']}")
            continue

        record = carve(statue, workspace, args.faces)

        if "skipped" in record:
            print(f"  {statue['model']}: skipped, {record['skipped']}")
            records.append(record)
            continue

        obj = record.pop("object")
        out = workspace / "enhanced" / "models" / f"{statue['model']}.glb"
        export(obj, out)

        print(f"  {statue['model']}: {record['triangles']} triangles from {record['from']}, "
              f"{record['size'][0]:.0f}x{record['size'][1]:.0f}x{record['size'][2]:.0f} units "
              f"at {record['at']}, facing {record['facing'][0]:+.2f},{record['facing'][2]:+.2f}, "
              f"{record['snapped']} of {record['vertices']} rim UVs snapped")

        records.append(record)

    if not args.dry_run:
        manifest = workspace / "manifests" / "statues.json"
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps({
            "schemaVersion": 1,
            "stage": "carve-statues",
            "faceBudget": args.faces,
            "statues": records,
        }, indent=1) + "\n")
        print(f"\nmanifest {manifest}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
