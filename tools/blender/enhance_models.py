"""Mesh enhancement pass for GK3Reborn, run inside Blender.

    blender --background --factory-startup --python enhance_models.py -- \
        --workspace D:/Dev/GK3Reborn/ContentWorkspace \
        [--only NAME ...] [--limit N] [--include-review] [--dry-run]

Reads `manifests/model-roles.json`, processes the models that should be processed,
and writes enhanced GLB plus LOD chain to `enhanced/models/`.

What this can and cannot do
---------------------------

The originals average 123 vertices. Subdivision and beveling round silhouettes and
give normals something to work with; they cannot invent detail that was never
modelled. Treat the output as a better base mesh, not a finished asset — the plan's
Tier 0 and Tier 1 assets still need a human in a DCC.

GK3 has no skeletons. Characters animate by vertex animation in ACT files, so nothing
imported here is skinned, and "organic" versus "hard surface" is decided from the
model's declared role rather than from rig data.

Texture work is deliberately absent. Upscaling and PBR channel generation are an image
pipeline, not a mesh one, and putting them here would couple two stages that fail for
unrelated reasons.

`--bake` will bake the enhanced geometry down onto the original UVs, but it is off by
default because the result was measured and it is nearly empty: 74 of 76 textures over a
mixed sample of props and characters came back perfectly flat. GK3's meshes already carry
welded, smooth vertex normals, and a subdivided surface converges to exactly those
normals - so a tangent-space map has no difference left to record. What the modifiers
change is position and silhouette, and neither of those fits in a normal map. The code is
kept, gated and instrumented, so the next person does not have to find this out again.
"""

import argparse
import json
import math
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

import bpy


# Dispositions come from the classifier, which reads the scene files rather than
# guessing from names. See tools/GK3Reborn.Tools/Stages/ModelRoleStage.cs.
ORGANIC = {"character"}
HARD_SURFACE = {"prop"}
CONSERVATIVE = {"scene-geometry"}
NEVER = {"collision"}
AMBIGUOUS = {"review"}


def parse_args(argv):
    parser = argparse.ArgumentParser(description="Enhance GK3 models in Blender.")
    parser.add_argument("--workspace", required=True, help="Content workspace root.")
    parser.add_argument("--only", nargs="*", default=None, help="Process just these models.")
    parser.add_argument("--limit", type=int, default=0, help="Stop after N models.")
    parser.add_argument("--include-review", action="store_true",
                        help="Also process models the classifier flagged as ambiguous.")
    parser.add_argument("--lods", type=int, default=2, help="Number of reduced LODs to emit.")
    parser.add_argument("--bake", action="store_true",
                        help="Bake the enhanced geometry into normal maps. Off by "
                             "default: measured, it records almost nothing. See "
                             "docs/mesh-enhancement.md.")
    parser.add_argument("--dry-run", action="store_true", help="Report the plan and stop.")
    return parser.parse_args(argv)


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    """Imports a glTF and returns the mesh objects it added, in file order.

    Only the new ones: the bake needs the model twice over in one scene - once enhanced
    and once as it shipped - and a second import has to come back as its own list rather
    than as everything present.
    """
    before = {o.name for o in bpy.context.scene.objects}
    bpy.ops.import_scene.gltf(filepath=str(path))
    return [o for o in bpy.context.scene.objects
            if o.type == "MESH" and o.name not in before]


def triangle_count(objects):
    total = 0
    for obj in objects:
        mesh = obj.data
        for polygon in mesh.polygons:
            total += max(1, len(polygon.vertices) - 2)
    return total


def select_only(objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    if objects:
        bpy.context.view_layer.objects.active = objects[0]


def clean_geometry(objects):
    """Merge coincident vertices, drop degenerate faces, make normals consistent.

    GK3 meshes are split per texture and carry duplicated vertices along those seams.
    Merging them is what lets every later step - beveling, subdivision, smooth shading -
    treat a surface as continuous instead of as unrelated islands.
    """
    for obj in objects:
        select_only([obj])
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.remove_doubles(threshold=0.0001)
        bpy.ops.mesh.dissolve_degenerate(threshold=0.0001)
        bpy.ops.mesh.delete_loose()
        bpy.ops.mesh.normals_make_consistent(inside=False)
        bpy.ops.object.mode_set(mode="OBJECT")


def shade_smooth_by_angle(obj, degrees):
    """Smooth shading with an angle threshold, across Blender versions.

    `use_auto_smooth` was removed in Blender 4.1 in favour of an operator that adds a
    modifier, so try the modern call first and fall back for older builds.
    """
    select_only([obj])
    try:
        bpy.ops.object.shade_smooth_by_angle(angle=math.radians(degrees))
        return
    except (AttributeError, TypeError, RuntimeError):
        pass

    bpy.ops.object.shade_smooth()
    if hasattr(obj.data, "use_auto_smooth"):
        obj.data.use_auto_smooth = True
        obj.data.auto_smooth_angle = math.radians(degrees)


def enhance_organic(obj):
    """Characters: subdivide, then smooth broadly.

    Levels are kept low deliberately. A 1,200-vertex character subdivided twice is
    already 19,000 vertices of the same shape, and going further buys smoother
    silhouettes at a cost that only a real remodel would justify.
    """
    modifier = obj.modifiers.new(name="GK3R Subdivision", type="SUBSURF")
    modifier.levels = 2
    modifier.render_levels = 2
    modifier.use_limit_surface = True
    shade_smooth_by_angle(obj, 60)


def enhance_hard_surface(obj):
    """Props: bevel the sharp edges, keep the flats flat.

    A bevel is what stops 1999-era geometry reading as paper-thin under real lighting:
    edges catch a highlight instead of vanishing. The width is kept small and relative
    so it works across props that differ wildly in scale.
    """
    bevel = obj.modifiers.new(name="GK3R Bevel", type="BEVEL")
    bevel.width = 0.35
    bevel.segments = 2
    bevel.limit_method = "ANGLE"
    bevel.angle_limit = math.radians(35)
    bevel.harden_normals = True

    weighted = obj.modifiers.new(name="GK3R Weighted Normals", type="WEIGHTED_NORMAL")
    weighted.keep_sharp = True

    shade_smooth_by_angle(obj, 35)


def enhance_conservative(obj):
    """Scene geometry: smooth shading only.

    Room-scale geometry is where subdivision and beveling do the most damage. Walls and
    floors have to keep meeting exactly, and rounding an edge that a wall abuts opens a
    visible seam. Improving these properly means re-modelling with the room's collision
    and camera bounds in hand, not a modifier stack.
    """
    shade_smooth_by_angle(obj, 25)


def apply_modifiers(objects):
    for obj in objects:
        select_only([obj])
        for modifier in list(obj.modifiers):
            try:
                bpy.ops.object.modifier_apply(modifier=modifier.name)
            except RuntimeError:
                obj.modifiers.remove(modifier)


def make_lod(objects, ratio):
    """Duplicate the meshes and decimate the copies to `ratio` of their triangles."""
    select_only(objects)
    bpy.ops.object.duplicate()
    duplicates = [o for o in bpy.context.selected_objects]

    for obj in duplicates:
        decimate = obj.modifiers.new(name="GK3R Decimate", type="DECIMATE")
        decimate.ratio = ratio
        select_only([obj])
        try:
            bpy.ops.object.modifier_apply(modifier=decimate.name)
        except RuntimeError:
            obj.modifiers.remove(decimate)

    return duplicates


def export_glb(objects, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    select_only(objects)
    bpy.ops.export_scene.gltf(
        filepath=str(path),
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_yup=True,
    )


def run_gltfpack(path):
    """Optimise vertex and index data if gltfpack is on PATH.

    Optional on purpose: it is a meaningful size win but not a correctness step, and
    requiring it would make the pipeline fail on a machine that simply lacks a tool.
    """
    executable = shutil.which("gltfpack")
    if executable is None:
        return None

    packed = path.with_suffix(".packed.glb")
    result = subprocess.run(
        [executable, "-i", str(path), "-o", str(packed), "-cc"],
        capture_output=True, text=True, check=False)

    if result.returncode != 0 or not packed.exists():
        return None

    packed.replace(path)
    return path.stat().st_size


# The bake is what gets any of this onto the screen. The engine draws the original
# .MOD geometry - there is no glTF reader in it - so a bevelled edge or a subdivided
# cheek exists only in `enhanced/models` until it is baked into a tangent-space map
# the texture path already carries. Where the mesh work shows up at runtime, it shows
# up through here.
BAKE_MARGIN = 8
BAKE_FALLBACK_SIZE = 512

# How far the baked surface has to lean away from the low-poly one before the map is
# worth keeping, in degrees at the 99th percentile. A bake that comes back flatter than
# this recorded nothing the interpolated vertex normals did not already say, and writing
# it costs a texture upload to state the obvious - or, where the image pipeline generated
# a map for the same surface, replaces something informative with something that is not.
BAKE_MIN_TILT = 2.0


def texture_of(material):
    """The texture name a material stands for, or None if it stands for nothing.

    The exporter names a material after the submesh's texture, so the two are the same
    string - which is what lets a baked map be filed where the engine already looks for
    one. `(none)` is the exporter's placeholder for a submesh with no texture at all.
    """
    name = (material.name or "").split(".")[0].strip()
    return None if not name or name == "(none)" else name.upper()


def bake_target(material, image):
    """Points a material's bake at `image` and returns the node, creating what is missing."""
    material.use_nodes = True
    nodes = material.node_tree.nodes
    node = nodes.new("ShaderNodeTexImage")
    node.image = image
    node.select = True
    nodes.active = node
    return node


def source_normal(directory, texture):
    """The generated normal map for a texture, if the image pipeline made one."""
    for suffix in (".PNG", ".png"):
        candidate = directory / f"{texture}{suffix}"
        if candidate.exists():
            return candidate
    return None


def tilt_of(pixels):
    """How far a baked tangent-space map leans off flat, in degrees at p50 and p99."""
    import numpy as np

    n = pixels.reshape(-1, 4)[:, :3] * 2.0 - 1.0
    length = np.sqrt((n * n).sum(axis=1))
    length[length < 1e-8] = 1.0
    degrees = np.degrees(np.arccos(np.clip(n[:, 2] / length, -1.0, 1.0)))
    return float(np.median(degrees)), float(np.percentile(degrees, 99))


def blend_whiteout(base, detail):
    """Whiteout blend of two tangent-space normal maps, both as float RGBA arrays.

    The two maps say different things and both are wanted: the bake carries curvature
    the low-poly mesh does not have, and the generated map carries surface detail that
    was never modelled at any resolution. Whiteout adds the tangents and multiplies the
    heights, which keeps both without either flattening the other.
    """
    import numpy as np

    a = base.reshape(-1, 4)[:, :3] * 2.0 - 1.0
    b = detail.reshape(-1, 4)[:, :3] * 2.0 - 1.0

    out = np.empty_like(a)
    out[:, 0] = a[:, 0] + b[:, 0]
    out[:, 1] = a[:, 1] + b[:, 1]
    out[:, 2] = a[:, 2] * b[:, 2]

    length = np.sqrt((out * out).sum(axis=1))
    length[length < 1e-8] = 1.0
    out /= length[:, None]

    merged = base.reshape(-1, 4).copy()
    merged[:, :3] = out * 0.5 + 0.5
    return merged.reshape(base.shape)


def save_normal(image, path):
    """Writes a normal map as linear data rather than as a picture.

    The colour space is set when the image is created and deliberately not touched here:
    assigning `colorspace_settings.name` reallocates the buffer, so doing it after the
    pixels are written saves a black image and reports success. It cost an afternoon.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    image.file_format = "PNG"
    image.filepath_raw = str(path)
    image.save()


def bake_normal_maps(low, high, out_root, seen):
    """Bakes the enhanced geometry down onto the original UVs, one map per texture.

    `low` and `high` are paired objects - the model as the game ships it and the model
    after beveling or subdivision. The map records the difference, which is the whole
    of what the enhancement produced that the engine can currently draw.

    A texture is baked once per run even when several models share it. GABE_HAIR is
    worn by both GAB and GAG, and a second bake would silently overwrite the first with
    a map made from different geometry.
    """
    import numpy as np

    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 1
    scene.cycles.use_denoising = False
    scene.render.bake.use_selected_to_active = True
    scene.render.bake.margin = BAKE_MARGIN
    scene.render.bake.use_clear = True
    scene.render.bake.normal_space = "TANGENT"

    generated = out_root / "normals"
    kept = out_root / "normals-source"
    baked = []
    flat = []

    for low_object, high_object in zip(low, high):
        if not low_object.data.materials or not low_object.data.uv_layers:
            continue

        # How far the ray looks for the high-poly surface. Proportional to the object,
        # because a model here can be a whole staircase or an alarm clock, and a fixed
        # extrusion either misses the one or wraps around the other.
        size = max(low_object.dimensions)
        scene.render.bake.cage_extrusion = max(0.05, size * 0.02)

        targets = {}
        for material in low_object.data.materials:
            texture = texture_of(material) if material else None
            if texture is None or texture in seen:
                continue

            existing = source_normal(kept, texture) or source_normal(generated, texture)
            width = height = BAKE_FALLBACK_SIZE
            if existing is not None:
                probe = bpy.data.images.load(str(existing), check_existing=False)
                width, height = probe.size
                bpy.data.images.remove(probe)

            image = bpy.data.images.new(
                f"bake_{texture}", width=width, height=height,
                alpha=False, float_buffer=True, is_data=True)
            targets[material.name] = (texture, image, bake_target(material, image))
            seen.add(texture)

        if not targets:
            continue

        select_only([high_object])
        low_object.select_set(True)
        bpy.context.view_layer.objects.active = low_object

        try:
            # Passed to the operator rather than left on the scene. Called from a script
            # the operator takes its own property defaults, not the scene's, and
            # use_selected_to_active defaults to off - which bakes the low mesh onto
            # itself and writes a perfectly flat map that looks like a successful run.
            bpy.ops.object.bake(
                type="NORMAL",
                use_selected_to_active=True,
                cage_extrusion=scene.render.bake.cage_extrusion,
                normal_space="TANGENT",
                margin=BAKE_MARGIN,
                use_clear=True)
        except RuntimeError as error:
            print(f"[gk3r]   bake failed on {low_object.name}: {error}")
            for texture, image, _ in targets.values():
                seen.discard(texture)
                bpy.data.images.remove(image)
            continue

        for texture, image, _ in targets.values():
            pixels = np.array(image.pixels[:], dtype=np.float32)
            middle, edge = tilt_of(pixels)

            # Measured rather than assumed, because the answer was not the expected one:
            # subdividing a mesh whose vertex normals are already smooth bakes flat. The
            # low-poly surface's interpolated normals are what the limit surface converges
            # to, so there is no difference left for a tangent-space map to carry. What
            # subdivision changes is the silhouette, and a normal map cannot hold a
            # silhouette. See docs/mesh-enhancement.md.
            if edge < BAKE_MIN_TILT:
                flat.append({"texture": texture, "p50": middle, "p99": edge})
                bpy.data.images.remove(image)
                continue

            # The generated map is kept aside the first time it is merged into, so that
            # the merge reads the same input every run instead of compounding on its own
            # output. Without it a second pass blends a blended map.
            live = source_normal(generated, texture)
            keep = kept / f"{texture}.PNG"
            if live is not None and not keep.exists():
                keep.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(live, keep)

            base = source_normal(kept, texture)
            if base is not None:
                detail = bpy.data.images.load(str(base), check_existing=False)
                detail.colorspace_settings.name = "Non-Color"
                if tuple(detail.size) == tuple(image.size):
                    pixels = blend_whiteout(
                        np.array(detail.pixels[:], dtype=np.float32), pixels)
                bpy.data.images.remove(detail)

            image.pixels.foreach_set(pixels)
            save_normal(image, generated / f"{texture}.PNG")
            baked.append({"texture": texture, "p50": middle, "p99": edge})
            bpy.data.images.remove(image)

    return baked, flat


def process(model, glb_in, out_root, args, seen):
    reset_scene()
    objects = import_glb(glb_in)
    if not objects:
        return {"name": model["name"], "status": "empty"}

    before = triangle_count(objects)
    clean_geometry(objects)

    disposition = model["disposition"]
    for obj in objects:
        if disposition in ORGANIC:
            enhance_organic(obj)
        elif disposition in HARD_SURFACE:
            enhance_hard_surface(obj)
        else:
            enhance_conservative(obj)

    apply_modifiers(objects)
    after = triangle_count(objects)

    out = out_root / "models" / f"{model['name']}.glb"
    export_glb(objects, out)
    run_gltfpack(out)

    # Only where the enhancement actually produced something. A model the modifiers left
    # alone bakes to a flat map, which is worse than no map: it costs a texture upload and
    # overwrites whatever the image pipeline generated for that surface.
    baked, flat = [], []
    if args.bake and after > before:
        original = import_glb(glb_in)
        try:
            baked, flat = bake_normal_maps(original, objects, out_root, seen)
        finally:
            select_only(original)
            bpy.ops.object.delete()

    lods = []
    for level in range(1, args.lods + 1):
        ratio = 0.5 ** level
        duplicates = make_lod(objects, ratio)
        lod_path = out_root / "models" / f"{model['name']}_LOD{level}.glb"
        export_glb(duplicates, lod_path)
        lods.append({"level": level, "triangles": triangle_count(duplicates)})

        bpy.ops.object.delete()

    return {
        "name": model["name"],
        "status": "enhanced",
        "disposition": disposition,
        "trianglesBefore": before,
        "trianglesAfter": after,
        "lods": lods,
        "bakedNormals": baked,
        "flatNormals": flat,
    }


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = parse_args(argv)

    workspace = Path(args.workspace)
    roles_path = workspace / "manifests" / "model-roles.json"
    if not roles_path.exists():
        sys.exit(f"missing {roles_path}; run 'GK3Reborn.Tools classify-models' first")

    roles = json.loads(roles_path.read_text(encoding="utf-8"))
    source_root = workspace / "normalized" / "models"
    out_root = workspace / "enhanced"

    allowed = set(ORGANIC | HARD_SURFACE | CONSERVATIVE)
    if args.include_review:
        allowed |= AMBIGUOUS

    wanted = {n.upper() for n in args.only} if args.only else None
    queue = []
    skipped = {}

    for model in roles["models"]:
        if wanted is not None and model["name"].upper() not in wanted:
            continue
        if model["disposition"] not in allowed:
            skipped[model["disposition"]] = skipped.get(model["disposition"], 0) + 1
            continue
        if not (source_root / f"{model['name']}.glb").exists():
            skipped["missing-source"] = skipped.get("missing-source", 0) + 1
            continue
        queue.append(model)

    if args.limit:
        queue = queue[:args.limit]

    print(f"[gk3r] {len(queue)} models to process; skipping {skipped}")
    if args.dry_run:
        for model in queue[:20]:
            print(f"  {model['disposition']:15} {model['name']:24} {model['triangleCount']:6} tris")
        return

    results = []
    started = time.time()

    # Textures already baked this run. A texture belongs to whichever model reached it
    # first; see bake_normal_maps.
    seen = set()

    for index, model in enumerate(queue, start=1):
        source = source_root / f"{model['name']}.glb"
        try:
            result = process(model, source, out_root, args, seen)
        except Exception as error:  # noqa: BLE001 - one bad model must not stop the run
            result = {"name": model["name"], "status": "failed", "error": str(error)}
            print(f"[gk3r] FAILED {model['name']}: {error}")

        results.append(result)
        if index % 25 == 0 or index == len(queue):
            print(f"[gk3r] {index}/{len(queue)} ({time.time() - started:.0f}s)")

    report = {
        "schemaVersion": 1,
        "stage": "C5.enhance",
        "processed": len(results),
        "enhanced": sum(1 for r in results if r["status"] == "enhanced"),
        "failed": sum(1 for r in results if r["status"] == "failed"),
        "skippedByDisposition": skipped,
        "bakedNormals": sum(len(r.get("bakedNormals", ())) for r in results),
        "flatNormals": sum(len(r.get("flatNormals", ())) for r in results),
        "models": results,
    }

    out_root.mkdir(parents=True, exist_ok=True)
    report_path = workspace / "manifests" / "enhanced-models.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"[gk3r] {report['enhanced']} enhanced, {report['failed']} failed -> {report_path}")


if __name__ == "__main__":
    main()
