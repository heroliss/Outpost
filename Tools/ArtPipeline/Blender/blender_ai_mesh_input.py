"""Render a neutral reference image from existing Blender geometry.

The source .blend remains unchanged. This script hides preview-only geometry,
uses neutral lighting and a transparent film, then records the exact PNG and
source hashes in a small experiment manifest. The result is useful for rough
blockout refinement or reconstruction regression, but regenerating an already
production-usable source mesh is not the default asset-production workflow.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

import bpy
from mathutils import Vector


HARNESS_VERSION = "0.2.0"
SCHEMA_VERSION = 1
DEFAULT_ASSET_ID = "NW_WaterRecycler_01"
ASSET_ID_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9_]+$")


def parse_args() -> argparse.Namespace:
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--asset-id", default=DEFAULT_ASSET_ID)
    parser.add_argument("--resolution", type=int, default=640, choices=(512, 640, 1024))
    parsed = parser.parse_args(arguments)
    if not ASSET_ID_PATTERN.fullmatch(parsed.asset_id):
        raise ValueError(f"Invalid asset id: {parsed.asset_id}")
    return parsed


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def remove_preview_lights() -> None:
    for obj in list(bpy.data.objects):
        if obj.type == "LIGHT" and obj.name.startswith("__PREVIEW_"):
            data = obj.data
            bpy.data.objects.remove(obj, do_unlink=True)
            if data.users == 0:
                bpy.data.lights.remove(data)


def add_area_light(name: str, location, energy: float, size: float, target: Vector) -> None:
    bpy.ops.object.light_add(type="AREA", location=location)
    light = bpy.context.object
    light.name = name
    light.data.energy = energy
    light.data.shape = "DISK"
    light.data.size = size
    light.data.color = (1.0, 1.0, 1.0)
    light.rotation_euler = (target - light.location).to_track_quat("-Z", "Y").to_euler()


def configure_scene(asset_id: str, output_path: Path, resolution: int) -> None:
    scene = bpy.context.scene
    camera = bpy.data.objects.get("__PREVIEW_Camera")
    if camera is None or camera.type != "CAMERA":
        raise RuntimeError("Source blend does not contain the expected __PREVIEW_Camera")

    asset_root = bpy.data.objects.get(asset_id)
    if asset_root is None:
        raise RuntimeError(f"Source blend does not contain the expected asset root: {asset_id}")
    asset_meshes = [obj for obj in asset_root.children_recursive if obj.type == "MESH"]
    if not asset_meshes:
        raise RuntimeError(f"Asset root has no mesh children: {asset_id}")

    for obj in bpy.data.objects:
        if obj.name.startswith("__PREVIEW_Ground"):
            obj.hide_render = True
    remove_preview_lights()

    target = Vector((0.0, 0.0, 0.74))
    add_area_light("__AI_INPUT_Key", (2.4, -2.8, 3.3), 1050, 3.0, target)
    add_area_light("__AI_INPUT_Fill", (-2.5, -1.0, 2.3), 700, 3.0, target)
    add_area_light("__AI_INPUT_Rim", (0.8, 2.6, 3.0), 650, 2.5, target)

    world = scene.world or bpy.data.worlds.new("AIInputWorld")
    scene.world = world
    if world.node_tree is None:
        raise RuntimeError("Source blend world does not provide a node tree")
    background = world.node_tree.nodes.get("Background")
    if background is None:
        raise RuntimeError("World node tree does not contain a Background node")
    background.inputs["Color"].default_value = (0.18, 0.18, 0.18, 1.0)
    background.inputs["Strength"].default_value = 0.35

    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_EEVEE"
    scene.camera = camera
    scene.render.resolution_x = resolution
    scene.render.resolution_y = resolution
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"
    scene.render.film_transparent = True
    scene.render.filepath = str(output_path)
    bpy.ops.render.render(write_still=True)


def alpha_report(output_path: Path) -> dict:
    image = bpy.data.images.load(str(output_path), check_existing=False)
    try:
        pixels = image.pixels[:]
        alphas = pixels[3::4]
        if not alphas:
            raise RuntimeError("Rendered PNG has no alpha samples")
        opaque_pixels = sum(1 for alpha in alphas if alpha >= 0.99)
        visible_pixels = sum(1 for alpha in alphas if alpha > 0.01)
        total_pixels = len(alphas)
        return {
            "min": round(min(alphas), 6),
            "max": round(max(alphas), 6),
            "visiblePixelRatio": round(visible_pixels / total_pixels, 6),
            "opaquePixelRatio": round(opaque_pixels / total_pixels, 6),
        }
    finally:
        bpy.data.images.remove(image)


def main() -> None:
    args = parse_args()
    source_blend = Path(bpy.data.filepath).resolve()
    if not source_blend.is_file():
        raise RuntimeError("Run Blender with an existing source .blend file")

    output_dir = Path(args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    output_path = output_dir / f"{args.asset_id}_ai_mesh_input.png"
    manifest_path = output_dir / "manifest.json"

    configure_scene(args.asset_id, output_path, args.resolution)
    if not output_path.is_file() or output_path.stat().st_size <= 0:
        raise RuntimeError(f"AI Mesh input was not rendered: {output_path}")
    alpha = alpha_report(output_path)
    if alpha["min"] > 0.01 or alpha["max"] < 0.99:
        raise RuntimeError(f"Rendered alpha does not contain background and subject: {alpha}")

    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "harnessVersion": HARNESS_VERSION,
        "status": "passed",
        "assetId": args.asset_id,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "toolchain": {
            "blenderVersion": bpy.app.version_string,
            "pythonVersion": sys.version.split()[0],
            "sourceScriptSha256": sha256(Path(__file__).resolve()),
        },
        "sourceBlend": {
            "file": source_blend.name,
            "bytes": source_blend.stat().st_size,
            "sha256": sha256(source_blend),
        },
        "input": {
            "file": output_path.name,
            "bytes": output_path.stat().st_size,
            "sha256": sha256(output_path),
            "width": args.resolution,
            "height": args.resolution,
            "format": "PNG RGBA8",
            "transparentBackground": True,
            "groundVisible": False,
            "lighting": "neutral-three-point",
            "camera": "source-preview-perspective",
            "alpha": alpha,
        },
        "sourceKind": "rendered-existing-geometry",
        "productionDefault": False,
        "intendedUse": (
            "Optional rough-blockout refinement or reconstruction-regression reference; "
            "not a runtime asset and not evidence that regenerating a production-usable "
            "source mesh reduces asset-production cost"
        ),
    }
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(f"SSFRAMEWORK_AI_MESH_INPUT_MANIFEST={manifest_path}")


if __name__ == "__main__":
    main()
