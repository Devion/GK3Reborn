"""Builds the temple's cut second room, from what survives of it.

    blender --background --factory-startup --python tools/blender/build_te2.py -- \
        --workspace D:/Dev/GK3Reborn/ContentWorkspace [--dry-run]

Writes ``enhanced/rooms/Te2.glb``, which ``GK3Reborn.Content.RoomLibrary`` turns into a
room. See ``GK3Reborn/docs/cut-content.md``.

What this is, and what it is not
--------------------------------

TE2 was cut before release and its geometry did not survive. What did survive is a great
deal: ``TE2A.SCN`` and ``TE2B.SCN`` name the room's BSP, list its thirty-eight objects and
carry two full light rigs of sixty and a hundred and forty-eight lights; four textures and
the fire animations are in the archives; and sixty-two lines of dialogue under the location
code ``1SE`` are on the disc, with their captions.

So the *specification* is unusually complete and the *shape* is gone. This is a blockout
built to that specification: the room is the size the light rig implies, the objects carry
the names the scene file lists -- which is what lets a noun be bound to one -- and the
elemental nooks are in the four corners the lights cluster in. Everything else is invention.
It is new art wearing a dead room's name, and nothing here should be mistaken for recovery.

Two things are guesses and are worth knowing as guesses. Which element goes in which corner
is not recorded anywhere; and the floor height is taken as zero, because the rig's lights
sit between -84 and 183 and the great majority of the fills are at 76, which reads as
waist height in a room about two hundred units tall.

It is skinned in the temple's own textures -- TE3's dark floor brick and walls, TE1's black
marble -- plus the four that are TE2's own and survived: TE2LOWERDOOR, TE2WRNLTHR and the
fire sheets.
"""

import argparse
import math
import os
import sys

import bpy


# The room, from the light rig's extent in TE2A.SCN: 1417 across by 1404 deep.
HALF_X = 700.0
HALF_Z = 690.0
FLOOR = 0.0
CEILING = 205.0
WALL = 30.0

# Which element sits in which corner. Not recorded; see the note above.
NOOKS = [
    ("fire", 1, 1, "TE2FIREHI1T"),
    ("water", -1, 1, "te3innrwall"),
    ("air", -1, -1, "te3innrwall"),
    ("earth", 1, -1, "te3innrwall"),
]

FLOOR_TEX = "te3dkfloorbrk"
WALL_TEX = "TE3WALL"
INNER_TEX = "te3innrwall"
MARBLE = "te1blkmrbl"
LEATHER = "TE2WRNLTHR"
DOOR_TEX = "TE2LOWERDOOR"


def material(name):
    existing = bpy.data.materials.get(name)

    if existing is not None:
        return existing

    made = bpy.data.materials.new(name=name)
    made.use_nodes = False

    return made


def slab(name, size, at, texture, spin=0.0):
    """One named box. Its node name is what a scene file binds a noun to."""
    across, up, through = size

    bpy.ops.mesh.primitive_cube_add(size=1.0)
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = (across, up, through)
    obj.rotation_euler = (0.0, math.radians(spin), 0.0)
    obj.location = (at[0], at[1] + up / 2.0, at[2])
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    obj.data.materials.append(material(texture))

    layer = obj.data.uv_layers.active or obj.data.uv_layers.new(name="UVMap")
    corners = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]

    for polygon in obj.data.polygons:
        for step, loop in enumerate(polygon.loop_indices):
            layer.data[loop].uv = corners[step % 4]

    return obj


def build():
    made = []

    # --- the shell ---------------------------------------------------------------------
    made.append(slab("te2_floor", (HALF_X * 2, WALL, HALF_Z * 2),
                     (0, FLOOR - WALL, 0), FLOOR_TEX))
    made.append(slab("te2_celing", (HALF_X * 2, WALL, HALF_Z * 2),
                     (0, CEILING, 0), MARBLE))

    for i, (dx, dz) in enumerate([(1, 0), (-1, 0), (0, 1), (0, -1)]):
        along_x = dx != 0
        made.append(slab(
            "te2_walls" if i == 0 else f"te2_walls{i:02d}",
            (WALL if along_x else HALF_X * 2, CEILING - FLOOR,
             HALF_Z * 2 if along_x else WALL),
            (dx * HALF_X, FLOOR, dz * HALF_Z),
            WALL_TEX))

    # The block in the middle the lines talk about being unable to push.
    made.append(slab("te2_centerwalls", (260, 150, 260), (0, FLOOR, 0), INNER_TEX))
    made.append(slab("te2_innerfloorrim", (330, 6, 330), (0, FLOOR, 0), MARBLE))
    made.append(slab("te2_rockformations", (150, 70, 120), (-430, FLOOR, 300), WALL_TEX))

    # --- the four elemental nooks ------------------------------------------------------
    #
    # Each is a recess in a corner with the fittings the scene file names for it: a basin,
    # a pipe, a plaque, and the one thing that is particular to that element.
    for element, sx, sz, nook_tex in NOOKS:
        cx = sx * (HALF_X - 190)
        cz = sz * (HALF_Z - 190)

        made.append(slab(f"te2_{element}nookwalls", (300, 170, 300),
                         (cx, FLOOR, cz), nook_tex))
        made.append(slab(f"te2_{element}basin", (70, 26, 70),
                         (cx, FLOOR, cz - sz * 90), MARBLE))
        made.append(slab(f"te2_{element}plaque", (54, 54, 6),
                         (cx, FLOOR + 90, cz + sz * 140), LEATHER))

        pipe = "te2_te2_airpipe" if element == "air" else f"te2_{element}pipe"
        made.append(slab(pipe, (14, 120, 14), (cx + 110, FLOOR, cz), MARBLE))

    # the fittings each element has of its own
    made.append(slab("te2_flint", (18, 12, 18), (500, FLOOR + 26, 400), MARBLE))
    made.append(slab("te2_salamander", (40, 16, 22), (510, FLOOR + 26, 470), LEATHER))
    made.append(slab("te2_firepanel", (90, 70, 8), (430, FLOOR + 40, 610), DOOR_TEX))
    made.append(slab("te2_fishhead", (34, 30, 30), (-510, FLOOR + 100, 470), MARBLE))
    made.append(slab("te2_waterspout", (12, 12, 46), (-510, FLOOR + 96, 430), MARBLE))
    made.append(slab("te2_vent", (80, 80, 8), (-560, FLOOR + 60, -610), LEATHER))
    made.append(slab("te2_gauge", (30, 30, 8), (-450, FLOOR + 110, -610), MARBLE))
    made.append(slab("te2_bellhanger", (16, 90, 16), (-510, FLOOR + 115, -480), MARBLE))
    made.append(slab("te2_oilspout", (12, 12, 40), (510, FLOOR + 96, -430), MARBLE))
    made.append(slab("te2_skulltop", (26, 26, 26), (510, FLOOR + 26, -470), LEATHER))
    made.append(slab("te2_leverbase", (40, 20, 40), (430, FLOOR, -560), MARBLE))

    # --- the doors and the elevator ----------------------------------------------------
    made.append(slab("te2_doorwalls", (200, 170, WALL), (0, FLOOR, -HALF_Z + WALL), INNER_TEX))
    made.append(slab("te2_upperdoorl", (60, 150, 10), (-46, FLOOR, -HALF_Z + 44), DOOR_TEX))
    made.append(slab("te2_upperdoorr", (60, 150, 10), (46, FLOOR, -HALF_Z + 44), DOOR_TEX))
    made.append(slab("te2_upperdoorhl", (14, 14, 6), (-20, FLOOR + 80, -HALF_Z + 38), MARBLE))
    made.append(slab("te2_upperdoorhr", (14, 14, 6), (20, FLOOR + 80, -HALF_Z + 38), MARBLE))
    made.append(slab("te2_elevator_walls", (170, 190, 170), (0, FLOOR, HALF_Z - 130), INNER_TEX))

    return made


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)

    made = build()

    faces = sum(len(o.data.polygons) for o in made)
    print(f"Te2: {len(made)} objects, {faces} faces")

    for obj in made:
        print(f"  {obj.name}")

    if args.dry_run:
        return

    out = os.path.join(args.workspace, "enhanced", "rooms")
    os.makedirs(out, exist_ok=True)
    path = os.path.join(out, "Te2.glb")

    # Left alone on the way out, like every other prop here: the room is modelled in the
    # frame the engine reads. See tools/blender/make_props.py.
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        export_yup=False,
        export_apply=True,
        export_materials="EXPORT",
        export_image_format="NONE",
        export_normals=True,
        export_texcoords=True,
        use_selection=False)

    print(f"wrote {path}")

    # The shell that fences the camera in. A separate file because the scene file names it
    # as a model rather than as part of the room, and it goes with the props: the loader
    # asks the model library for it when no .MOD of that name exists.
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)

    shell = slab("te2cambnds", (HALF_X * 2 - 90, CEILING - 30, HALF_Z * 2 - 90),
                 (0, FLOOR + 10, 0), FLOOR_TEX)

    # Wound inward, because a camera shell is a room seen from inside it: the test asks
    # which side of each face the camera is on, and a box wound outward fences it out of
    # everywhere rather than into somewhere.
    bpy.context.view_layer.objects.active = shell
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.flip_normals()
    bpy.ops.object.mode_set(mode="OBJECT")

    models = os.path.join(args.workspace, "enhanced", "models")
    os.makedirs(models, exist_ok=True)
    shell_path = os.path.join(models, "te2cambnds.glb")

    bpy.ops.export_scene.gltf(
        filepath=shell_path,
        export_format="GLB",
        export_yup=False,
        export_apply=True,
        export_materials="EXPORT",
        export_image_format="NONE",
        export_normals=True,
        export_texcoords=True,
        use_selection=False)

    print(f"wrote {shell_path}")


main()
