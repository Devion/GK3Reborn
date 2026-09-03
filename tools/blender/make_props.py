"""Builds the props for objects GK3 wrote lines for and never modelled.

    blender --background --factory-startup --python tools/blender/make_props.py -- \
        --workspace D:/Dev/GK3Reborn/ContentWorkspace [--only NAME ...] [--dry-run]

Writes one GLB per prop into ``enhanced/models``, which is where
``GK3Reborn.Content.ModelLibrary`` looks for a model a scene names and the 1999 archives
have no ``.MOD`` for. See ``GK3Reborn/docs/cut-content.md`` for what each of these is and
which noun it answers to.

Two rules, and both of them are about not being noticed
------------------------------------------------------

**Every prop is textured with the game's own bitmaps and nothing else.** The rooms were
lit and baked against those colours in 1999; an object painted with anything else reads as
an object from another game standing in this one. So Madeline's suitcase is skinned in the
leather off Lady Howard's trunk, the letter in the hotel's own stationery, and the
magazines in the covers the game already carries. The material's *name* is the texture's
name — that is the whole binding, and it is what ``GlbReader`` reads.

**Every prop is built at the origin, standing on the XZ plane**, and the scene file says
where it goes with ``pos={x,y,z}``. Nothing here knows which room it is for. That is the
difference between this and a 1999 ``.MOD``, whose vertices are in the coordinates of the
one room it was made for and which therefore cannot be reused anywhere.

Units are GK3's: roughly two and a half to the inch, so a 40-unit case is about knee high.

Everything is modelled in the frame the engine reads -- glTF's, with **Y up** -- and
exported with the axis conversion switched off. Blender is Z-up and its exporter will
convert, but which way it converts is a setting whose effect is invisible until a letter
is standing on its edge on somebody's desk. Building in the destination frame and telling
the exporter to leave the axes alone removes the question. So a part's size is
(across, up, through) and it stands on y=0.
"""

import argparse
import math
import os
import sys

import bpy


# --- the props ------------------------------------------------------------------------
#
# Each is a list of boxes and sheets. Kept declarative and kept small: these are objects
# seen from across a room, at most leaned in on by an inspect camera, and a suitcase with
# eight hundred triangles would cost more to look at than the room it stands in.

PROPS = {
    # Madeline's suitcase, R29, noun SUITCASE_IN_CLOSET.
    #   "Madeline's suitcase."  /  "She's already unpacked."
    # Stood on the floor of the hanging compartment, which is 27 units across and 29 deep,
    # so the case is 22 x 13 and stands 40 tall against the back.
    "r29_madsuitcase": {
        "note": "Madeline's suitcase, stood upright in her wardrobe",
        "parts": [
            # body: front and back take the trunk's face, the ends its sides, top its lid
            {"box": (22.0, 38.0, 13.0), "at": (0, 0, 0),
             "faces": {"front": "LHOTRUNKF", "back": "LHOTRUNKF",
                       "left": "LHOTRUNKS", "right": "LHOTRUNKS",
                       "top": "LHOTRUNKT", "bottom": "LHOTRUNKT"}},
            # the lid's lip, a shallower box sitting proud of the seam
            {"box": (22.4, 2.0, 13.4), "at": (0, 28.0, 0),
             "faces": {"front": "LHOTRUNKS", "back": "LHOTRUNKS",
                       "left": "LHOTRUNKS", "right": "LHOTRUNKS",
                       "top": "LHOTRUNKS", "bottom": "LHOTRUNKS"}},
            # handle, on top
            {"box": (7.0, 1.6, 1.6), "at": (0, 38.0, 0),
             "faces": {"all": "LHOTRUNKS"}},
        ],
    },

    # Dr Wen's letter, R31, noun LETTER_FROM_WEN.
    #   "He mentions three documents, but there're only two here."
    # Two sheets, because the line counts them. Lying on the desk beside the stationery,
    # the upper one turned a little as paper on a desk is.
    "r31_wenletter": {
        "note": "the two documents from Dr Wen, on the desk",
        "parts": [
            {"sheet": (15.0, 20.0), "at": (0, 0.0, 0), "spin": -6.0,
             "faces": {"all": "RLCSTATIONARY"}},
            {"sheet": (15.0, 20.0), "at": (1.4, 0.22, -1.1), "spin": 5.0,
             "faces": {"all": "RLCSTATIONARY"}},
        ],
    },

    # The crow's nest puzzle, RC2. The whole of it is in RC2102P.NVC with every line
    # commented out; what it never had is geometry. See docs/cut-content.md.
    #
    #   "He's using fibers from that black rug to line his nest.  *I* could use some of
    #    those."                                                     -- CROW_AT_NEST, LOOK
    "rc2_birdsnest": {
        "note": "the crow's nest, high in the museum tree",
        "parts": [
            {"box": (26.0, 9.0, 26.0), "at": (0, 0, 0), "faces": {"all": "BarkOld"}},
            {"box": (18.0, 4.0, 18.0), "at": (0, 9.0, 0), "faces": {"all": "BarkOld"}},
        ],
    },

    # The bird itself. Small, dark, and seen from the ground at thirty units up, so it is
    # a silhouette and nothing else pretends otherwise.
    "rc2_crow": {
        "note": "the crow, on its nest",
        "parts": [
            {"box": (7.0, 7.0, 16.0), "at": (0, 0, 0), "spin": 20.0,
             "faces": {"all": "BLACK"}},
            {"box": (5.0, 5.0, 5.0), "at": (0, 5.0, -7.0), "spin": 20.0,
             "faces": {"all": "BLACK"}},
        ],
    },

    # The hose, coiled against the museum wall. HOSEPIECE is the game's own texture and
    # was almost certainly made for this: nothing in the shipped game uses it.
    "rc2_gardenhose": {
        "note": "the garden hose, coiled by the museum",
        "parts": [
            {"box": (44.0, 5.0, 44.0), "at": (0, 0, 0), "spin": 0.0,
             "faces": {"top": "HOSEPIECE", "bottom": "HOSEPIECEBACK", "all": "HOSEPIECE"}},
            {"box": (34.0, 5.0, 34.0), "at": (0, 5.0, 0), "spin": 24.0,
             "faces": {"top": "HOSEPIECE", "bottom": "HOSEPIECEBACK", "all": "HOSEPIECE"}},
            {"box": (24.0, 5.0, 24.0), "at": (0, 10.0, 0), "spin": 48.0,
             "faces": {"top": "HOSEPIECE", "bottom": "HOSEPIECEBACK", "all": "HOSEPIECE"}},
        ],
    },

    # The rug somebody is airing, which is where the crow got its black fibres.
    #   "It's a black rug."  /  "Someone's airing their rug."
    # RUGTILE rather than RUG1: the line calls it black and RUG1 is cream.
    "rc2_blackrug": {
        "note": "the black rug aired over the museum railing",
        "parts": [
            {"box": (96.0, 4.0, 60.0), "at": (0, 0, 0), "faces": {"all": "RUGTILE"}},
            {"box": (96.0, 46.0, 5.0), "at": (0, -46.0, 28.0), "faces": {"all": "RUGTILE"}},
        ],
    },

    # The Abbe's cigarette ends on top of Tour Magdala, MA3, noun CIGARETTE_BUTT_PILE.
    #   "Nice.  Somebody should clean up this place."
    # With the packet among them, because the close-up that goes with this pile is about
    # the brand -- "Never heard of that brand.  Must be French." -- and the brand is on
    # the game's own packet texture: FRAIS CIGARETTES.
    "ma3_cigbutts": {
        "note": "the Abbe's cigarette ends and his packet, on the tower floor",
        "parts": [
            # the packet, on its side and open
            {"box": (7.0, 2.2, 4.6), "at": (0, 0, 0), "spin": 24.0,
             "faces": {"top": "CIGPACKFRNT", "bottom": "CIGPACKBOT",
                       "front": "CIGPACKSIDE", "back": "CIGPACKSIDE",
                       "left": "CIGPACKTOP", "right": "CIGPACKTOP"}},
            # the ends, scattered. Small enough that a face is a pixel or two, so they
            # take the packet's own paper rather than a texture of their own.
            {"box": (2.4, 0.7, 0.7), "at": (5.5, 0, 1.8), "spin": 71.0,
             "faces": {"all": "CIGPACKBOT"}},
            {"box": (2.4, 0.7, 0.7), "at": (-4.8, 0, 2.6), "spin": 12.0,
             "faces": {"all": "CIGPACKBOT"}},
            {"box": (2.4, 0.7, 0.7), "at": (3.1, 0, -4.4), "spin": 138.0,
             "faces": {"all": "CIGPACKBOT"}},
            {"box": (2.4, 0.7, 0.7), "at": (-2.2, 0, -5.1), "spin": 96.0,
             "faces": {"all": "CIGPACKBOT"}},
            {"box": (2.4, 0.7, 0.7), "at": (6.9, 0, -1.6), "spin": 43.0,
             "faces": {"all": "CIGPACKBOT"}},
            {"box": (2.4, 0.7, 0.7), "at": (-6.3, 0, -1.2), "spin": 160.0,
             "faces": {"all": "CIGPACKBOT"}},
            {"box": (2.4, 0.7, 0.7), "at": (0.6, 0, 5.4), "spin": 28.0,
             "faces": {"all": "CIGPACKBOT"}},
        ],
    },

    # The lobby magazines, LBY, noun MAGAZINES.
    #   "Magazines.  Nothing that looks interesting, though."  /  "They're outdated.  And
    #   in French."
    # A fanned stack of three on the coffee table.
    "lby_magazines": {
        "note": "outdated French magazines on the lobby table",
        "parts": [
            {"sheet": (17.0, 22.0), "at": (0, 0.0, 0), "spin": 0.0, "thick": 0.7,
             "faces": {"top": "MAGAZINEFRNT", "bottom": "MAGBACK", "all": "MAGBACK"}},
            {"sheet": (17.0, 22.0), "at": (1.8, 0.7, 1.2), "spin": -9.0, "thick": 0.7,
             "faces": {"top": "MAGBACK2", "bottom": "MAGBACK", "all": "MAGBACK"}},
            {"sheet": (17.0, 22.0), "at": (-1.1, 1.4, -0.9), "spin": 7.0, "thick": 0.7,
             "faces": {"top": "MAGAZINEFRNT", "bottom": "MAGBACK", "all": "MAGBACK"}},
        ],
    },
}


def material(name):
    """A flat material whose *name* is the game texture the engine will look up."""
    existing = bpy.data.materials.get(name)

    if existing is not None:
        return existing

    made = bpy.data.materials.new(name=name)
    made.use_nodes = False

    return made


def box(size, at, faces, spin=0.0):
    """A cuboid standing on y=0, its faces textured by name. Sizes are edge lengths."""
    across, up, through = size

    # primitive_cube_add(size=n) spans -n/2..+n/2, so a scale of s gives an edge of s.
    bpy.ops.mesh.primitive_cube_add(size=1.0)
    obj = bpy.context.active_object
    obj.scale = (across, up, through)
    obj.rotation_euler = (0.0, math.radians(spin), 0.0)
    obj.location = (at[0], at[1] + up / 2.0, at[2])
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    assign(obj, faces)

    return obj


def sheet(size, at, spin, thick, faces):
    """A thin slab lying on y=0 — a sheet of paper, a magazine."""
    across, through = size

    return box((across, thick, through), at, faces, spin)


def assign(obj, faces):
    """Puts a material on every face, by which way the face points."""
    named = {}

    for key in ("all", "top", "bottom", "front", "back", "left", "right"):
        if key in faces:
            named[key] = material(faces[key])

    for slot in dict.fromkeys(named.values()):
        obj.data.materials.append(slot)

    order = [m.name for m in obj.data.materials]

    for polygon in obj.data.polygons:
        normal = polygon.normal
        if abs(normal.y) > 0.5:
            side = "top" if normal.y > 0 else "bottom"
        elif abs(normal.z) > 0.5:
            side = "front" if normal.z > 0 else "back"
        else:
            side = "right" if normal.x > 0 else "left"

        chosen = named.get(side) or named.get("all")

        if chosen is not None:
            polygon.material_index = order.index(chosen.name)


def unwrap(obj):
    """The whole texture across each face, once.

    Not a projection. A cube projection is sized in world units, so a 17-unit magazine
    cover came out tiled seventeen times and read as ruled paper rather than as a cover.
    These are all boxes, every face is one quad, and what each face wants is the picture
    it is named after -- so the corners are written straight out.
    """
    mesh = obj.data
    layer = mesh.uv_layers.active or mesh.uv_layers.new(name="UVMap")
    corners = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]

    for polygon in mesh.polygons:
        for step, loop in enumerate(polygon.loop_indices):
            layer.data[loop].uv = corners[step % 4]


def clear():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)

    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.objects):
        for item in list(block):
            block.remove(item)


def build(name, spec, out, dry_run):
    clear()

    made = []

    for part in spec["parts"]:
        if "box" in part:
            made.append(
                box(part["box"], part["at"], part["faces"], part.get("spin", 0.0)))
        else:
            made.append(
                sheet(part["sheet"], part["at"], part.get("spin", 0.0),
                      part.get("thick", 0.2), part["faces"]))

    for obj in made:
        unwrap(obj)

    bpy.ops.object.select_all(action="SELECT")
    bpy.context.view_layer.objects.active = made[0]
    bpy.ops.object.join()

    joined = bpy.context.active_object
    joined.name = name

    lo = [min(v.co[i] for v in joined.data.vertices) for i in range(3)]
    hi = [max(v.co[i] for v in joined.data.vertices) for i in range(3)]

    print(f"{name}: {len(joined.data.polygons)} faces, "
          f"{hi[0] - lo[0]:.1f} x {hi[1] - lo[1]:.1f} x {hi[2] - lo[2]:.1f} units "
          f"-- {spec['note']}")

    if dry_run:
        return

    path = os.path.join(out, name + ".glb")

    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        use_selection=False,
        export_yup=False,
        export_apply=True,
        export_materials="EXPORT",
        export_image_format="NONE",
        export_normals=True,
        export_texcoords=True,
    )

    print(f"  wrote {path}")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--only", nargs="*", default=None)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    out = os.path.join(args.workspace, "enhanced", "models")

    if not args.dry_run:
        os.makedirs(out, exist_ok=True)

    wanted = PROPS if not args.only else {
        k: v for k, v in PROPS.items() if k in set(args.only)
    }

    if not wanted:
        print(f"nothing to build; known props: {', '.join(sorted(PROPS))}")
        return

    for name, spec in wanted.items():
        build(name, spec, out, args.dry_run)


main()
