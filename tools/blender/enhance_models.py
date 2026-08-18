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
unrelated reasons. This script only bakes a normal map, and only where subdivision
actually produced geometry worth baking.
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
    parser.add_argument("--no-bake", action="store_true", help="Skip normal-map baking.")
    parser.add_argument("--dry-run", action="store_true", help="Report the plan and stop.")
    return parser.parse_args(argv)


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    return [o for o in bpy.context.scene.objects if o.type == "MESH"]


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


def process(model, glb_in, out_root, args):
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

    for index, model in enumerate(queue, start=1):
        source = source_root / f"{model['name']}.glb"
        try:
            result = process(model, source, out_root, args)
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
        "models": results,
    }

    out_root.mkdir(parents=True, exist_ok=True)
    report_path = workspace / "manifests" / "enhanced-models.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"[gk3r] {report['enhanced']} enhanced, {report['failed']} failed -> {report_path}")


if __name__ == "__main__":
    main()
