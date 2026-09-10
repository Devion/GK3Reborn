"""A small glTF binary writer for rooms and props built out of boxes and quads.

The engine reads a room out of a ``.glb`` through ``GlbReader`` and ``SceneFromModel``:
one node per object (the node's name is what a scene file binds a noun to), one primitive
per material inside it, and the material's *name* is the texture the game draws it with.
A material that carries ``KHR_materials_unlit`` is drawn as painted -- a lit lamp shade, the
view through a window -- and once a file marks anything unlit, the rest of it is lit by
the room's authored lights instead of being drawn full bright.

Nothing here needs Blender. The frame is the engine's own: Y up, and the vertex order the
renderer treats as a front face. See ``docs/rendering.md`` for which side that is.
"""

import json
import math
import struct


class Glb:
    """Collects named objects, each a set of textured triangle lists, and writes a GLB."""

    def __init__(self):
        self.objects = {}          # name -> {(texture, unlit): Primitive}
        self.order = []            # object names, in the order they were first used

    # ------------------------------------------------------------------ primitives ---
    def _primitive(self, name, texture, unlit):
        if name not in self.objects:
            self.objects[name] = {}
            self.order.append(name)

        key = (texture.upper(), bool(unlit))
        prims = self.objects[name]

        if key not in prims:
            prims[key] = {"positions": [], "normals": [], "uvs": [], "indices": []}

        return prims[key]

    def quad(self, name, texture, corners, uvs, unlit=False, both=False):
        """One quad. ``corners`` are four points wound so the face looks *toward* the
        viewer who should see it; ``uvs`` are their texture coordinates in step."""
        p0, p1, p2, p3 = (tuple(map(float, c)) for c in corners)
        n = _normal(p0, p1, p2)
        prim = self._primitive(name, texture, unlit)
        base = len(prim["positions"])
        prim["positions"].extend([p0, p1, p2, p3])
        prim["normals"].extend([n] * 4)
        prim["uvs"].extend([tuple(map(float, uv)) for uv in uvs])
        prim["indices"].extend([base, base + 1, base + 2, base, base + 2, base + 3])

        if both:
            self.quad(name, texture, (p0, p3, p2, p1), (uvs[0], uvs[3], uvs[2], uvs[1]), unlit)

    def box(self, name, texture, lo, hi, tile=64.0, faces="all", unlit=False,
            textures=None, origin="world", inward=False, v_offset=0.0, tiles=None):
        """An axis-aligned box.

        ``tile`` is how many world units one repeat of the texture covers, a number for both
        axes or a ``(u, v)`` pair; ``"stretch"`` puts exactly one copy of the texture on each
        face. ``textures`` overrides the texture per face and ``tiles`` the tiling per face:
        keys ``+x -x +y -y +z -z``. ``origin`` is ``"world"`` (texture coordinates read off
        world position, so two boxes sharing a wall tile continuously) or ``"face"`` (each
        face starts at its own top-left corner). ``inward`` winds every face to look into
        the box: a room shell or a camera fence.
        """
        x0, y0, z0 = map(float, lo)
        x1, y1, z1 = map(float, hi)
        wanted = set("+x -x +y -y +z -z".split()) if faces == "all" else set(faces.split())
        textures = textures or {}
        tiles = tiles or {}
        mins = (x0, y0, z0)
        spans = (max(x1 - x0, 1e-6), max(y1 - y0, 1e-6), max(z1 - z0, 1e-6))

        # Each face: its four corners wound to look outward, and which world axes run
        # along its texture's u and v. v runs *down* a vertical face so the top of the
        # picture is at the top of the wall.
        for face, corners, u_axis, v_axis in (
            ("+z", [(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], 0, 1),
            ("-z", [(x1, y0, z0), (x0, y0, z0), (x0, y1, z0), (x1, y1, z0)], 0, 1),
            ("+x", [(x1, y0, z1), (x1, y0, z0), (x1, y1, z0), (x1, y1, z1)], 2, 1),
            ("-x", [(x0, y0, z0), (x0, y0, z1), (x0, y1, z1), (x0, y1, z0)], 2, 1),
            ("+y", [(x0, y1, z1), (x1, y1, z1), (x1, y1, z0), (x0, y1, z0)], 0, 2),
            ("-y", [(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)], 0, 2),
        ):
            if face not in wanted:
                continue

            tex = textures.get(face, texture)
            scale = tiles.get(face, tile)
            vertical = v_axis == 1
            uvs = []

            for c in corners:
                if scale == "stretch":
                    u = (c[u_axis] - mins[u_axis]) / spans[u_axis]
                    v = (y1 - c[1]) / spans[1] if vertical else (c[v_axis] - mins[v_axis]) / spans[v_axis]
                else:
                    tu, tv = (scale, scale) if not isinstance(scale, (tuple, list)) else scale
                    ou = mins[u_axis] if origin == "face" else 0.0
                    u = (c[u_axis] - ou) / tu

                    if vertical:
                        top = y1 if origin == "face" else 0.0
                        v = (top - c[1]) / tv + v_offset
                    else:
                        ov = mins[v_axis] if origin == "face" else 0.0
                        v = (c[v_axis] - ov) / tv + v_offset

                uvs.append((u, v))

            if inward:
                corners = [corners[0], corners[3], corners[2], corners[1]]
                uvs = [uvs[0], uvs[3], uvs[2], uvs[1]]

            self.quad(name, tex, corners, uvs, unlit)

    def cylinder(self, name, texture, centre, radius, y0, y1, sides=12, tile=None,
                 unlit=False, cap=True):
        """An upright cylinder, textured once around unless ``tile`` gives units per repeat."""
        cx, cz = centre
        around = 2 * math.pi * radius
        repeats = 1.0 if tile is None else around / tile
        height_repeats = 1.0 if tile is None else (y1 - y0) / tile

        for i in range(sides):
            a0 = 2 * math.pi * i / sides
            a1 = 2 * math.pi * (i + 1) / sides
            p00 = (cx + radius * math.cos(a0), y0, cz + radius * math.sin(a0))
            p10 = (cx + radius * math.cos(a1), y0, cz + radius * math.sin(a1))
            p11 = (cx + radius * math.cos(a1), y1, cz + radius * math.sin(a1))
            p01 = (cx + radius * math.cos(a0), y1, cz + radius * math.sin(a0))
            u0 = repeats * i / sides
            u1 = repeats * (i + 1) / sides
            # Wound so the outside is the front: walking round with the angle increasing
            # is anticlockwise seen from above (+y), and the face's normal must point out.
            self.quad(name, texture, (p10, p00, p01, p11),
                      ((u1, height_repeats), (u0, height_repeats), (u0, 0.0), (u1, 0.0)),
                      unlit)

        if cap:
            top = [(cx + radius * math.cos(2 * math.pi * i / sides), y1,
                    cz + radius * math.sin(2 * math.pi * i / sides)) for i in range(sides)]
            for i in range(1, sides - 1):
                self.triangle(name, texture, (top[0], top[i + 1], top[i]),
                              ((0.5, 0.5), (0.5, 0.5), (0.5, 0.5)), unlit)

    def triangle(self, name, texture, corners, uvs, unlit=False):
        p0, p1, p2 = (tuple(map(float, c)) for c in corners)
        n = _normal(p0, p1, p2)
        prim = self._primitive(name, texture, unlit)
        base = len(prim["positions"])
        prim["positions"].extend([p0, p1, p2])
        prim["normals"].extend([n] * 3)
        prim["uvs"].extend([tuple(map(float, uv)) for uv in uvs])
        prim["indices"].extend([base, base + 1, base + 2])

    # ----------------------------------------------------------------------- output ---
    def stats(self):
        objects = len(self.order)
        tris = sum(len(p["indices"]) // 3 for prims in self.objects.values() for p in prims.values())
        verts = sum(len(p["positions"]) for prims in self.objects.values() for p in prims.values())
        return objects, tris, verts

    def write(self, path):
        materials = []
        material_index = {}
        buffer = bytearray()
        views = []
        accessors = []
        meshes = []
        nodes = []
        any_unlit = False

        def view(data, target):
            while len(buffer) % 4:
                buffer.append(0)
            views.append({"buffer": 0, "byteOffset": len(buffer), "byteLength": len(data),
                          "target": target})
            buffer.extend(data)
            return len(views) - 1

        for name in self.order:
            primitives = []

            for (texture, unlit), prim in self.objects[name].items():
                key = (texture, unlit)

                if key not in material_index:
                    material = {
                        "name": texture,
                        "pbrMetallicRoughness": {
                            "baseColorFactor": [1.0, 1.0, 1.0, 1.0],
                            "metallicFactor": 0.0,
                            "roughnessFactor": 1.0,
                        },
                        "doubleSided": False,
                    }
                    if unlit:
                        material["extensions"] = {"KHR_materials_unlit": {}}
                        any_unlit = True
                    material_index[key] = len(materials)
                    materials.append(material)

                positions = prim["positions"]
                count = len(positions)

                if count > 65535:
                    raise ValueError(f"{name}/{texture}: {count} vertices, more than 16 bits index")

                pos = b"".join(struct.pack("<fff", *p) for p in positions)
                nrm = b"".join(struct.pack("<fff", *n) for n in prim["normals"])
                uvs = b"".join(struct.pack("<ff", *uv) for uv in prim["uvs"])
                idx = b"".join(struct.pack("<H", i) for i in prim["indices"])

                lo = [min(p[i] for p in positions) for i in range(3)]
                hi = [max(p[i] for p in positions) for i in range(3)]

                accessors.append({"bufferView": view(pos, 34962), "componentType": 5126,
                                  "count": count, "type": "VEC3", "min": lo, "max": hi})
                a_pos = len(accessors) - 1
                accessors.append({"bufferView": view(nrm, 34962), "componentType": 5126,
                                  "count": count, "type": "VEC3"})
                a_nrm = len(accessors) - 1
                accessors.append({"bufferView": view(uvs, 34962), "componentType": 5126,
                                  "count": count, "type": "VEC2"})
                a_uv = len(accessors) - 1
                accessors.append({"bufferView": view(idx, 34963), "componentType": 5123,
                                  "count": len(prim["indices"]), "type": "SCALAR"})
                a_idx = len(accessors) - 1

                primitives.append({
                    "attributes": {"POSITION": a_pos, "NORMAL": a_nrm, "TEXCOORD_0": a_uv},
                    "indices": a_idx,
                    "material": material_index[key],
                    "mode": 4,
                })

            meshes.append({"name": name, "primitives": primitives})
            nodes.append({"name": name, "mesh": len(meshes) - 1})

        while len(buffer) % 4:
            buffer.append(0)

        doc = {
            "asset": {"version": "2.0", "generator": "GK3Reborn tools/rooms/glbwriter.py"},
            "scene": 0,
            "scenes": [{"nodes": list(range(len(nodes)))}],
            "nodes": nodes,
            "meshes": meshes,
            "materials": materials,
            "accessors": accessors,
            "bufferViews": views,
            "buffers": [{"byteLength": len(buffer)}],
        }

        if any_unlit:
            doc["extensionsUsed"] = ["KHR_materials_unlit"]

        text = json.dumps(doc, separators=(",", ":")).encode("utf-8")
        while len(text) % 4:
            text += b" "

        total = 12 + 8 + len(text) + 8 + len(buffer)

        with open(path, "wb") as handle:
            handle.write(struct.pack("<III", 0x46546C67, 2, total))
            handle.write(struct.pack("<II", len(text), 0x4E4F534A))
            handle.write(text)
            handle.write(struct.pack("<II", len(buffer), 0x004E4942))
            handle.write(buffer)


def _normal(p0, p1, p2):
    ax, ay, az = p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2]
    bx, by, bz = p2[0] - p0[0], p2[1] - p0[1], p2[2] - p0[2]
    nx, ny, nz = ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx
    length = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
    return (nx / length, ny / length, nz / length)
