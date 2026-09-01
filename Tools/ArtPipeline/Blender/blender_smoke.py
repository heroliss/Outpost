"""Generate and validate one repeatable Blender-to-FBX smoke asset.

This script is intentionally self-contained and uses only Blender's bundled
Python API. It is a production-pipeline probe, not a final art generator.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

import bpy
from mathutils import Vector


HARNESS_VERSION = "0.1.0"
DEFAULT_ASSET_ID = "NW_StorageCrate_01"
ASSET_ID_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9_]+$")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--asset-id", default=DEFAULT_ASSET_ID)
    argv = []
    if "--" in sys.argv:
        argv = sys.argv[sys.argv.index("--") + 1 :]
    args = parser.parse_args(argv)
    if ASSET_ID_PATTERN.fullmatch(args.asset_id) is None:
        parser.error("--asset-id must start with a letter and contain only ASCII letters, digits, or underscores")
    return args


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def reset_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.unit_settings.length_unit = "METERS"
    scene.render.resolution_x = 512
    scene.render.resolution_y = 512
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    # The smoke output is disposable and versioned by its manifest. Blender's
    # automatic .blend1 backup would leave stale, unreported evidence behind.
    bpy.context.preferences.filepaths.save_version = 0
    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_EEVEE"


def make_material(name: str, color: tuple[float, float, float, float], metallic: float, roughness: float):
    material = bpy.data.materials.new(name=name)
    material.diffuse_color = color
    shader = material.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = color
    shader.inputs["Metallic"].default_value = metallic
    shader.inputs["Roughness"].default_value = roughness
    return material


def add_cube(
    name: str,
    dimensions: tuple[float, float, float],
    location: tuple[float, float, float],
    material,
    *,
    rotation_y_degrees: float = 0.0,
    bevel: float = 0.025,
):
    bpy.ops.mesh.primitive_cube_add(location=location)
    obj = bpy.context.object
    obj.name = name
    obj.data.name = f"{name}_Mesh"
    obj.dimensions = dimensions
    obj.rotation_euler.y = math.radians(rotation_y_degrees)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel > 0:
        modifier = obj.modifiers.new(name="EdgeSoftening", type="BEVEL")
        modifier.width = bevel
        modifier.segments = 2
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    obj.data.materials.append(material)
    return obj


def build_asset(asset_id: str):
    teal = make_material("M_WornTeal", (0.075, 0.245, 0.265, 1.0), 0.35, 0.48)
    dark = make_material("M_DarkMetal", (0.045, 0.055, 0.060, 1.0), 0.72, 0.32)
    orange = make_material("M_SafetyOrange", (0.78, 0.19, 0.035, 1.0), 0.22, 0.42)

    root = bpy.data.objects.new(asset_id, None)
    root.empty_display_type = "CUBE"
    root.empty_display_size = 0.15
    bpy.context.collection.objects.link(root)

    parts = []

    def part(*args, **kwargs):
        obj = add_cube(*args, **kwargs)
        obj.parent = root
        parts.append(obj)
        return obj

    part(f"{asset_id}_Base", (1.20, 0.82, 0.12), (0.0, 0.0, 0.08), dark, bevel=0.035)
    part(f"{asset_id}_Lid", (1.20, 0.82, 0.11), (0.0, 0.0, 0.91), dark, bevel=0.035)
    part(f"{asset_id}_FrontPanel", (0.92, 0.065, 0.56), (0.0, -0.395, 0.49), teal)
    part(f"{asset_id}_BackPanel", (0.92, 0.065, 0.56), (0.0, 0.395, 0.49), teal)
    part(f"{asset_id}_LeftPanel", (0.065, 0.62, 0.56), (-0.585, 0.0, 0.49), teal)
    part(f"{asset_id}_RightPanel", (0.065, 0.62, 0.56), (0.585, 0.0, 0.49), teal)

    for x in (-0.55, 0.55):
        for y in (-0.36, 0.36):
            suffix = f"{'L' if x < 0 else 'R'}{'F' if y < 0 else 'B'}"
            part(f"{asset_id}_Post_{suffix}", (0.105, 0.105, 0.78), (x, y, 0.49), dark, bevel=0.018)

    part(
        f"{asset_id}_FrontBrace",
        (0.075, 0.075, 0.93),
        (0.0, -0.438, 0.49),
        dark,
        rotation_y_degrees=-52.0,
        bevel=0.012,
    )
    part(
        f"{asset_id}_BackBrace",
        (0.075, 0.075, 0.93),
        (0.0, 0.438, 0.49),
        dark,
        rotation_y_degrees=52.0,
        bevel=0.012,
    )
    part(f"{asset_id}_Label", (0.28, 0.022, 0.13), (0.24, -0.443, 0.55), orange, bevel=0.01)
    part(f"{asset_id}_Latch", (0.10, 0.055, 0.18), (0.0, -0.455, 0.80), orange, bevel=0.015)

    return root, parts


def configure_preview(asset_parts, preview_path: Path) -> None:
    ground_material = make_material("M_PreviewGround", (0.10, 0.075, 0.055, 1.0), 0.0, 0.82)
    bpy.ops.mesh.primitive_plane_add(size=8.0, location=(0.0, 0.0, 0.0))
    ground = bpy.context.object
    ground.name = "__PREVIEW_Ground"
    ground.data.materials.append(ground_material)

    bpy.ops.object.camera_add(location=(2.7, -3.5, 2.35))
    camera = bpy.context.object
    camera.name = "__PREVIEW_Camera"
    camera.data.lens = 57
    camera.data.sensor_width = 36
    target = Vector((0.0, 0.0, 0.48))
    camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = camera

    bpy.ops.object.light_add(type="AREA", location=(2.0, -2.2, 3.4))
    key = bpy.context.object
    key.name = "__PREVIEW_Key"
    key.data.energy = 900
    key.data.shape = "DISK"
    key.data.size = 3.0
    key.rotation_euler = (target - key.location).to_track_quat("-Z", "Y").to_euler()

    bpy.ops.object.light_add(type="AREA", location=(-2.4, -0.4, 1.9))
    fill = bpy.context.object
    fill.name = "__PREVIEW_Fill"
    fill.data.energy = 520
    fill.data.size = 2.5
    fill.rotation_euler = (target - fill.location).to_track_quat("-Z", "Y").to_euler()

    bpy.ops.object.light_add(type="AREA", location=(0.7, 2.6, 2.8))
    rim = bpy.context.object
    rim.name = "__PREVIEW_Rim"
    rim.data.energy = 760
    rim.data.size = 2.0
    rim.rotation_euler = (target - rim.location).to_track_quat("-Z", "Y").to_euler()

    world = bpy.context.scene.world or bpy.data.worlds.new("PreviewWorld")
    bpy.context.scene.world = world
    background = world.node_tree.nodes.get("Background")
    background.inputs["Color"].default_value = (0.018, 0.022, 0.026, 1.0)
    background.inputs["Strength"].default_value = 0.32

    for obj in asset_parts:
        obj.select_set(False)
    bpy.context.scene.render.filepath = str(preview_path)
    bpy.ops.render.render(write_still=True)


def export_fbx(root, parts, fbx_path: Path) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    for obj in parts:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(
        filepath=str(fbx_path),
        use_selection=True,
        object_types={"EMPTY", "MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        add_leaf_bones=False,
        bake_anim=False,
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        path_mode="AUTO",
    )


def geometry_report(parts) -> dict:
    vertex_count = 0
    polygon_count = 0
    triangle_count = 0
    world_points = []
    for obj in parts:
        mesh = obj.data
        mesh.calc_loop_triangles()
        vertex_count += len(mesh.vertices)
        polygon_count += len(mesh.polygons)
        triangle_count += len(mesh.loop_triangles)
        world_points.extend(obj.matrix_world @ Vector(corner) for corner in obj.bound_box)

    minimum = Vector((min(p.x for p in world_points), min(p.y for p in world_points), min(p.z for p in world_points)))
    maximum = Vector((max(p.x for p in world_points), max(p.y for p in world_points), max(p.z for p in world_points)))
    size = maximum - minimum
    return {
        "meshObjectCount": len(parts),
        "vertexCount": vertex_count,
        "polygonCount": polygon_count,
        "triangleCount": triangle_count,
        "boundsMeters": {
            "min": [round(v, 6) for v in minimum],
            "max": [round(v, 6) for v in maximum],
            "size": [round(v, 6) for v in size],
        },
        "suggestedBoxCollider": {
            "center": [round(v, 6) for v in ((minimum + maximum) * 0.5)],
            "size": [round(v, 6) for v in size],
        },
        "objects": [obj.name for obj in parts],
    }


def main() -> None:
    args = parse_args()
    output_dir = Path(args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    asset_id = args.asset_id
    blend_path = output_dir / f"{asset_id}.blend"
    fbx_path = output_dir / f"{asset_id}.fbx"
    preview_path = output_dir / f"{asset_id}_preview.png"
    manifest_path = output_dir / "manifest.json"

    reset_scene()
    root, parts = build_asset(asset_id)
    configure_preview(parts, preview_path)
    export_fbx(root, parts, fbx_path)
    bpy.ops.wm.save_as_mainfile(filepath=str(blend_path), compress=True)

    generated_files = [blend_path, fbx_path, preview_path]
    manifest = {
        "schemaVersion": 1,
        "harnessVersion": HARNESS_VERSION,
        "status": "passed",
        "asset": {
            "id": asset_id,
            "purpose": "Versioned Blender-to-FBX production-pipeline smoke probe",
            "sourceKind": "procedural-bpy",
            "intendedUse": "Pipeline validation only; not approved production art",
        },
        "toolchain": {
            "blenderVersion": bpy.app.version_string,
            "blenderVersionCycle": bpy.app.version_cycle,
            "pythonVersion": sys.version.split()[0],
            "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
            "sourceScriptSha256": sha256(Path(__file__).resolve()),
        },
        "coordinateContract": {
            "authoringUnits": "meters",
            "blenderUpAxis": "+Z",
            "fbxForwardAxis": "-Z",
            "fbxUpAxis": "+Y",
            "rootTransform": "identity",
        },
        "geometry": geometry_report(parts),
        "acceptance": {
            "previewRendered": preview_path.exists() and preview_path.stat().st_size > 0,
            "fbxExported": fbx_path.exists() and fbx_path.stat().st_size > 0,
            "blendSaved": blend_path.exists() and blend_path.stat().st_size > 0,
            "manualReviewStillRequired": True,
        },
        "files": [
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in generated_files
        ],
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"SSFRAMEWORK_BLENDER_SMOKE_MANIFEST={manifest_path}")


if __name__ == "__main__":
    main()
