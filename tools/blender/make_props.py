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


def tube(major, minor, sides=6):
    """The cross-section of a round tube bent into a ring of radius ``major``.

    Revolved, this is a torus, which is what one turn of a coiled hose is. Point zero is
    the *bottom* of the tube, so the ring rests on y=0 and -- because a revolved part's
    texture runs V around the cross-section -- the bright band halfway up ``HOSEPIECE``
    comes out along the top of the hose, where the sun is.
    """
    return [
        (major + minor * math.cos(-math.pi / 2 + 2 * math.pi * k / sides),
         minor + minor * math.sin(-math.pi / 2 + 2 * math.pi * k / sides))
        for k in range(sides)
    ]


# --- the props ------------------------------------------------------------------------
#
# Each is a list of boxes, sheets and -- where a shape is round -- solids of revolution.
# Kept declarative and kept small: these are objects seen from across a room, at most
# leaned in on by an inspect camera, and a suitcase with eight hundred triangles would
# cost more to look at than the room it stands in.
#
# Round costs more and is worth it where the shape is the whole point. The nest is 224
# triangles and the hose 580, against a dozen for a magazine; both were boxes first and
# both read as something else -- a crate up a tree, and a stack of plates.

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
    # A nest is the one thing here that cannot be boxes. Everything else in this file is
    # something an artist built out of flat panels in 1999 and would build the same way
    # again -- a suitcase, a packet, a magazine -- but a nest is round, and two stacked
    # cuboids in a tree read as a crate somebody left up there. So it is a solid of
    # revolution: the cross-section below is the woven wall, taken round in sixteen steps.
    #
    # Twenty-six units across and eleven and a half high, which is the footprint the boxes
    # had; the cup is twenty across at the rim and seven deep, so the crow sits in it.
    "rc2_birdsnest": {
        "note": "the crow's nest, high in the museum tree",
        "parts": [
            {"revolve": [
                (0.0, 0.8),    # the underside, coming to a point on the axis
                (7.0, 0.0),    # where it sits on the branch
                (12.0, 4.0),
                (13.0, 10.0),  # widest, just under the rim
                (10.0, 11.5),  # over the rim
                (7.0, 8.0),    # and down the inside
                (3.5, 5.0),
                (0.0, 4.6),    # the floor of the cup
            ], "at": (0, 0, 0), "around": 16, "faces": {"all": "BarkOld"}},
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

    # The hose, coiled by the museum steps. HOSEPIECE and HOSEPIECEBACK are the game's own
    # textures and were almost certainly made for this: nothing in the shipped game uses
    # either.
    #
    # **The texture says what the shape has to be.** HOSEPIECE is 128x16 -- a length of
    # hose seen side-on, dark at both edges with a highlight down the middle, which is a
    # skin for a tube and nothing else. Lying it flat on the top of a box put that
    # highlight across a plate, and three of those stacked read as a pile of crockery,
    # which is exactly how it was reported. So the coil is three turns of round tube, with
    # the strip running along each one and once round it.
    #
    # The top turn stops short of closing, because a hose has an end. The two faces that
    # leaves are capped with HOSEPIECEBACK, which is the cut end -- a brass ferrule and the
    # green -- and the gap is put at 170 degrees, which is where FR_MS2 stands: the camera
    # the player is on when they are at the steps looking at this.
    #
    # Each turn is offset a little from the one under it. A hose that has been dropped is
    # not concentric.
    "rc2_gardenhose": {
        "note": "the garden hose, coiled by the museum steps",
        "parts": [
            {"revolve": tube(20.0, 2.0, 8), "at": (0, 0.0, 0), "around": 12,
             "wrap": (1.0, 1.0),
             "faces": {"all": "HOSEPIECE", "cap": "HOSEPIECEBACK"}},
            {"revolve": tube(16.2, 2.0, 8), "at": (2.0, 3.0, -1.5), "around": 12,
             "wrap": (1.0, 1.0),
             "faces": {"all": "HOSEPIECE", "cap": "HOSEPIECEBACK"}},
            {"revolve": tube(12.6, 2.0, 8), "at": (-1.5, 6.0, 2.0), "around": 12,
             "wrap": (1.0, 1.0), "arc": (200.0, 300.0),
             "faces": {"all": "HOSEPIECE", "cap": "HOSEPIECEBACK"}},
        ],
    },

    # The rug somebody is airing, which is where the crow got its black fibres.
    #   "It's a black rug."  /  "Someone's airing their rug."  /  "I can't get up there."
    # RUGTILE rather than RUG1: the line calls it black and RUG1 is cream.
    #
    # Hung out of a window and not over a railing, because the noun says which --
    # BLACK_RUG_IN_WINDOW -- and because the third line is the PICKUP: a rug Gabriel could
    # walk up to and take would not be one he cannot get up to. So it is a fold over the
    # sill and a drop down the wall outside, sized to the window it hangs from: 52 across
    # for a 57-wide opening, and 38 down, which is as far as it can hang before it reaches
    # the string course under it.
    #
    # Built hanging along +z and turned to the wall by the placement's heading. Note that
    # its lowest vertex is the bottom of the drop, in mid-air, so the pos in
    # Assets/Story/CutContent.txt is the sill less the drop rather than a surface.
    "rc2_blackrug": {
        "note": "the black rug aired out of an upstairs window",
        "parts": [
            # the fold over the sill: half of it inside the window, half of it out
            {"box": (52.0, 4.0, 18.0), "at": (0, 0, 0), "faces": {"all": "RUGTILE"}},
            # and the drop, down the face of the wall
            {"box": (52.0, 38.0, 3.0), "at": (0, -38.0, 7.5), "faces": {"all": "RUGTILE"}},
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


def revolve(profile, at, around, faces, arc=None, wrap=None):
    """A solid of revolution about the Y axis, from a closed cross-section.

    ``profile`` is a closed loop of (radius, height) points in the half-plane, taken round
    in ``around`` steps. A point at radius nought is a single vertex on the axis and the
    ring beside it closes as a fan of triangles, which is how a shape gets a bottom without
    a separate cap; two such points in a row have nothing between them and are skipped.

    Heights are measured from ``at``, so a profile whose lowest point is nought stands on
    the ground plane like every box in this file. ``at``'s X and Z are where the axis is,
    which is how three turns of a hose sit off-centre from one another.

    ``arc`` is (start, sweep) in degrees for a shape that does not go all the way round --
    a hose with an end. The two cross-sections that leaves are capped, with ``faces["cap"]``
    where the dict names one. A profile that touches the axis cannot be swept partly, since
    a fan closing on the axis has no cap to give.

    ``wrap`` is (across, around) texture repeats. Given, the part is mapped
    parametrically -- U along the sweep, V around the cross-section -- rather than by
    :func:`unwrap`, which puts the whole picture on every face. That is what a texture
    drawn as a *length* of something wants, and the difference between a hose and a stack
    of plates.
    """
    start, sweep = (0.0, 360.0) if arc is None else arc
    whole = abs(sweep) >= 359.999
    steps = around if whole else around + 1
    axial = any(radius <= 0.0 for radius, _ in profile)

    if wrap is not None and axial:
        raise ValueError("a parametric map wants a profile that stays off the axis")

    rings = []

    for radius, height in profile:
        y = at[1] + height

        if radius <= 0.0:
            rings.append([(at[0], y, at[2])])
            continue

        rings.append([
            (at[0] + radius * math.cos(math.radians(start + sweep * s / around)),
             y,
             at[2] + radius * math.sin(math.radians(start + sweep * s / around)))
            for s in range(steps)
        ])

    vertices = []
    starts = []

    for ring in rings:
        starts.append(len(vertices))
        vertices.extend(ring)

    polygons = []
    corners = []

    for i, lower in enumerate(rings):
        j = (i + 1) % len(rings)
        upper = rings[j]
        a, b = starts[i], starts[j]

        if len(lower) == 1 and len(upper) == 1:
            continue

        for s in range(around):
            t = (s + 1) % steps

            if len(lower) == 1:
                polygons.append((a, b + t, b + s))
                corners.append(None)
            elif len(upper) == 1:
                polygons.append((a + s, a + t, b))
                corners.append(None)
            else:
                polygons.append((a + s, a + t, b + t, b + s))

                # Keyed by vertex, not by position: the recalculation below reverses the
                # loops of a face it flips, and where the sweep closes the last step and
                # the first are the same ring of vertices -- so the *logical* step, which
                # is what the picture is laid out against, is recorded here and s + 1 is
                # allowed to run past the end.
                corners.append({
                    a + s: (i, s), a + t: (i, s + 1),
                    b + t: (i + 1, s + 1), b + s: (i + 1, s),
                })

    caps = set()

    if not whole and not axial:
        for s, turn in ((0, True), (steps - 1, False)):
            face = [starts[i] + s for i in range(len(rings))]
            caps.add(len(polygons))
            polygons.append(tuple(face if turn else reversed(face)))
            corners.append(None)

    mesh = bpy.data.meshes.new("revolved")
    mesh.from_pydata(vertices, [], polygons)
    mesh.validate()

    obj = bpy.data.objects.new("revolved", mesh)
    bpy.context.collection.objects.link(obj)

    # The winding above is whatever the profile's direction made it, and a nest with its
    # faces inside out is lit from the wrong side. Blender knows which way is out of a
    # closed surface, so it is asked rather than reasoned about. It reverses a face's
    # loops where it flips one, which is why the map below is written from each loop's own
    # vertex rather than in the order the corners were listed.
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode="OBJECT")

    assign(obj, faces)

    if faces.get("cap") is not None and caps:
        index = [m.name for m in obj.data.materials].index(faces["cap"])

        for polygon in obj.data.polygons:
            if polygon.index in caps:
                polygon.material_index = index

    if wrap is not None:
        parametric(obj, corners, len(profile), around, wrap)

    return obj


def parametric(obj, corners, sections, around, wrap):
    """Maps a revolved part U along its sweep and V around its cross-section.

    Held per face rather than per vertex, and that is the whole of why this is not two
    lines. Where the sweep closes, the last step and the first are one ring of vertices,
    so a vertex has no single U: it is nought on one side of the seam and a whole repeat
    on the other. The face knows which it wants -- the step recorded for it runs past the
    end rather than wrapping -- and a per-vertex map would draw the picture backwards
    across the seam face instead.

    The caps carry no corners and take the picture once, round the square's inscribed
    circle, as any other face of an odd number of sides does.
    """
    across, round_ = wrap
    mesh = obj.data

    # Named as every other part's layer is. Joining two meshes matches their UV layers by
    # name, so a prop of a mapped part and an unmapped one would otherwise come out with
    # two layers and half the faces at nought in each; the flag is what tells
    # :func:`unwrap` to leave this one alone.
    layer = mesh.uv_layers.new(name="UVMap")
    obj["_parametric"] = True

    for polygon in mesh.polygons:
        known = corners[polygon.index]

        if known is None:
            sides = len(polygon.loop_indices)

            for step, loop in enumerate(polygon.loop_indices):
                angle = 2.0 * math.pi * step / sides
                layer.data[loop].uv = (
                    0.5 + 0.5 * math.cos(angle), 0.5 + 0.5 * math.sin(angle))

            continue

        for loop in polygon.loop_indices:
            i, s = known[mesh.loops[loop].vertex_index]
            layer.data[loop].uv = (across * s / around, round_ * i / sections)


def assign(obj, faces):
    """Puts a material on every face, by which way the face points.

    ``cap`` is the one key no direction chooses: it belongs to the two cross-sections a
    partly swept solid of revolution is closed with, which point along the sweep rather
    than any way in particular. It is taken here so that the material exists on the
    object, and :func:`revolve` puts it on the faces it made.
    """
    named = {}

    for key in ("all", "top", "bottom", "front", "back", "left", "right", "cap"):
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
    Nearly every face here is one quad, and what each face wants is the picture it is
    named after -- so the corners are written straight out.

    A revolved shape has faces that are not quads: the fans that close it on the axis are
    triangles. Those take the same square, walked round its inscribed circle, so a face of
    any number of sides gets the whole texture once and none of it repeats.

    A part that asked to be mapped along itself has already been, and is left alone --
    otherwise the hose's length of tube would be laid on every one of its faces, which is
    what it looked like before.
    """
    if obj.get("_parametric"):
        return

    mesh = obj.data
    layer = mesh.uv_layers.active or mesh.uv_layers.new(name="UVMap")
    corners = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]

    for polygon in mesh.polygons:
        sides = len(polygon.loop_indices)

        for step, loop in enumerate(polygon.loop_indices):
            if sides == 4:
                layer.data[loop].uv = corners[step]
                continue

            angle = 2.0 * math.pi * step / sides
            layer.data[loop].uv = (
                0.5 + 0.5 * math.cos(angle), 0.5 + 0.5 * math.sin(angle))


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
        elif "revolve" in part:
            made.append(
                revolve(part["revolve"], part["at"], part.get("around", 16),
                        part["faces"], part.get("arc"), part.get("wrap")))
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
