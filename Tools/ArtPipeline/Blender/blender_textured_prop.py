"""Generate one textured, material-merged Blender-to-Unity prop probe.

The output is deterministic at the semantic and texture-byte level. Blender
and FBX binaries are evidence artifacts, not byte-for-byte reproducibility
contracts. The script only uses Blender's bundled Python and the standard
library so a new machine does not need Pillow, NumPy, or a Blender extension.
"""

from __future__ import annotations

import argparse
import binascii
import hashlib
import json
import math
import re
import struct
import sys
import zlib
from datetime import datetime, timezone
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector


HARNESS_VERSION = "0.3.0"
DEFAULT_ASSET_ID = "NW_WaterRecycler_01"
ASSET_ID_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9_]+$")
MIN_TEXTURE_SIZE = 256
MAX_TEXTURE_SIZE = 2048
EXPECTED_MESH_COUNT = 3

MATERIAL_SPECS = (
    {
        "name": "M_WornTeal",
        "slug": "WornTeal",
        "seed": 1103,
        "baseColorSrgb": (0.075, 0.285, 0.305),
        "wornColorSrgb": (0.055, 0.065, 0.072),
        "dustColorSrgb": (0.30, 0.205, 0.115),
        # The intact coat is dielectric; only scratches expose conductive metal.
        "metallic": 0.02,
        "wornMetallic": 0.82,
        "smoothness": 0.30,
        "wornSmoothness": 0.38,
        "normalScale": 0.48,
        "occlusionStrength": 0.78,
        "tiling": (1.35, 1.35),
    },
    {
        "name": "M_DarkMetal",
        "slug": "DarkMetal",
        "seed": 2207,
        "baseColorSrgb": (0.085, 0.10, 0.115),
        "wornColorSrgb": (0.18, 0.19, 0.20),
        "dustColorSrgb": (0.20, 0.135, 0.078),
        "metallic": 0.78,
        "wornMetallic": 0.92,
        "smoothness": 0.42,
        "wornSmoothness": 0.29,
        "normalScale": 0.40,
        "occlusionStrength": 0.74,
        "tiling": (1.80, 1.80),
    },
    {
        "name": "M_SafetyOrange",
        "slug": "SafetyOrange",
        "seed": 3301,
        "baseColorSrgb": (0.62, 0.115, 0.02),
        "wornColorSrgb": (0.08, 0.085, 0.09),
        "dustColorSrgb": (0.34, 0.22, 0.10),
        "metallic": 0.02,
        "wornMetallic": 0.86,
        "smoothness": 0.27,
        "wornSmoothness": 0.36,
        "normalScale": 0.45,
        "occlusionStrength": 0.76,
        "tiling": (1.50, 1.50),
    },
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--asset-id", default=DEFAULT_ASSET_ID)
    parser.add_argument("--texture-size", type=int, default=512)
    argv = []
    if "--" in sys.argv:
        argv = sys.argv[sys.argv.index("--") + 1 :]
    args = parser.parse_args(argv)
    if ASSET_ID_PATTERN.fullmatch(args.asset_id) is None:
        parser.error("--asset-id must start with a letter and contain only ASCII letters, digits, or underscores")
    if (
        args.texture_size < MIN_TEXTURE_SIZE
        or args.texture_size > MAX_TEXTURE_SIZE
        or args.texture_size & (args.texture_size - 1)
    ):
        parser.error("--texture-size must be a power of two from 256 through 2048")
    return args


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def clamp01(value: float) -> float:
    return min(1.0, max(0.0, value))


def lerp(left: float, right: float, amount: float) -> float:
    return left + (right - left) * amount


def smoothstep(value: float) -> float:
    value = clamp01(value)
    return value * value * (3.0 - 2.0 * value)


def hash01(x: int, y: int, seed: int) -> float:
    value = (
        (x * 0x1F123BB5)
        ^ (y * 0x5F356495)
        ^ (seed * 0x6C8E9CF5)
    ) & 0xFFFFFFFF
    value ^= value >> 15
    value = (value * 0x2C1B3C6D) & 0xFFFFFFFF
    value ^= value >> 12
    value = (value * 0x297A2D39) & 0xFFFFFFFF
    value ^= value >> 15
    return value / 0xFFFFFFFF


def tileable_value_noise(u: float, v: float, cells: int, seed: int) -> float:
    x = u * cells
    y = v * cells
    x0 = math.floor(x)
    y0 = math.floor(y)
    tx = smoothstep(x - x0)
    ty = smoothstep(y - y0)
    x1 = (x0 + 1) % cells
    y1 = (y0 + 1) % cells
    x0 %= cells
    y0 %= cells
    bottom = lerp(hash01(x0, y0, seed), hash01(x1, y0, seed), tx)
    top = lerp(hash01(x0, y1, seed), hash01(x1, y1, seed), tx)
    return lerp(bottom, top, ty)


class Lcg:
    def __init__(self, seed: int):
        self.state = seed & 0xFFFFFFFF

    def next(self) -> float:
        self.state = (1664525 * self.state + 1013904223) & 0xFFFFFFFF
        return self.state / 0xFFFFFFFF


def distance_to_segment(
    px: float,
    py: float,
    x0: float,
    y0: float,
    x1: float,
    y1: float,
) -> float:
    dx = x1 - x0
    dy = y1 - y0
    length_squared = dx * dx + dy * dy
    if length_squared <= 1e-12:
        return math.hypot(px - x0, py - y0)
    amount = clamp01(((px - x0) * dx + (py - y0) * dy) / length_squared)
    return math.hypot(px - (x0 + amount * dx), py - (y0 + amount * dy))


def build_surface_features(seed: int):
    random = Lcg(seed)
    scratches = []
    for _ in range(13):
        center_x = 0.12 + random.next() * 0.76
        center_y = 0.12 + random.next() * 0.76
        length = 0.08 + random.next() * 0.22
        angle = random.next() * math.tau
        half_x = math.cos(angle) * length * 0.5
        half_y = math.sin(angle) * length * 0.5
        scratches.append(
            (
                center_x - half_x,
                center_y - half_y,
                center_x + half_x,
                center_y + half_y,
                0.0012 + random.next() * 0.0024,
                0.035 + random.next() * 0.055,
            )
        )

    pits = []
    for _ in range(18):
        pits.append(
            (
                0.06 + random.next() * 0.88,
                0.06 + random.next() * 0.88,
                0.0025 + random.next() * 0.006,
                0.025 + random.next() * 0.05,
            )
        )
    return scratches, pits


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    checksum = binascii.crc32(kind + payload) & 0xFFFFFFFF
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", checksum)


def write_png_rgba(
    path: Path,
    width: int,
    height: int,
    pixels: bytearray,
    *,
    srgb: bool,
) -> None:
    expected = width * height * 4
    if len(pixels) != expected:
        raise ValueError(f"PNG pixel count mismatch: {len(pixels)} != {expected}")
    stride = width * 4
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        start = y * stride
        rows.extend(pixels[start : start + stride])
    payload = bytearray(b"\x89PNG\r\n\x1a\n")
    payload.extend(png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)))
    if srgb:
        payload.extend(png_chunk(b"sRGB", b"\x00"))
    payload.extend(png_chunk(b"IDAT", zlib.compress(bytes(rows), level=9)))
    payload.extend(png_chunk(b"IEND", b""))
    path.write_bytes(payload)


def to_byte(value: float) -> int:
    return int(round(clamp01(value) * 255.0))


def generate_texture_set(
    output_dir: Path,
    asset_id: str,
    spec: dict,
    size: int,
) -> dict:
    scratches, pits = build_surface_features(spec["seed"])
    heights = [0.0] * (size * size)
    wear = [0.0] * (size * size)
    dust = [0.0] * (size * size)

    for y in range(size):
        v = y / size
        for x in range(size):
            u = x / size
            macro = tileable_value_noise(u, v, 5, spec["seed"] + 11)
            medium = tileable_value_noise(u, v, 17, spec["seed"] + 23)
            fine = tileable_value_noise(u, v, 53, spec["seed"] + 47)
            # Broad value breakup belongs mainly in albedo/roughness. The normal
            # map should describe subtle surface relief, not turn sheet metal
            # into hammered rock.
            height = 0.5 + (macro - 0.5) * 0.035 + (medium - 0.5) * 0.012 + (fine - 0.5) * 0.003
            wear_amount = max(0.0, (fine - 0.74) * 0.22)

            for x0, y0, x1, y1, width, depth in scratches:
                distance = distance_to_segment(u, v, x0, y0, x1, y1)
                if distance >= width:
                    continue
                influence = (1.0 - distance / width) ** 2
                height -= depth * influence
                wear_amount = max(wear_amount, influence * 0.92)

            for center_x, center_y, radius, depth in pits:
                distance = math.hypot(u - center_x, v - center_y)
                if distance >= radius:
                    continue
                influence = (1.0 - distance / radius) ** 2
                height -= depth * influence
                wear_amount = max(wear_amount, influence * 0.72)

            index = y * size + x
            heights[index] = height
            wear[index] = clamp01(wear_amount)
            dust[index] = clamp01((tileable_value_noise(u, v, 7, spec["seed"] + 71) - 0.58) * 0.46)

    base_pixels = bytearray(size * size * 4)
    normal_pixels = bytearray(size * size * 4)
    mask_pixels = bytearray(size * size * 4)
    occlusion_pixels = bytearray(size * size * 4)
    normal_strength = size * 0.055

    for y in range(size):
        previous_y = (y - 1) % size
        next_y = (y + 1) % size
        for x in range(size):
            previous_x = (x - 1) % size
            next_x = (x + 1) % size
            index = y * size + x
            pixel = index * 4
            height = heights[index]
            worn = smoothstep(wear[index])
            dusty = smoothstep(dust[index]) * (1.0 - worn * 0.65)

            macro = tileable_value_noise(x / size, y / size, 9, spec["seed"] + 101)
            color_variation = 0.93 + macro * 0.11
            for channel in range(3):
                painted = spec["baseColorSrgb"][channel] * color_variation
                exposed = spec["wornColorSrgb"][channel]
                colored = lerp(painted, exposed, worn)
                colored = lerp(colored, spec["dustColorSrgb"][channel], dusty * 0.28)
                base_pixels[pixel + channel] = to_byte(colored)
            base_pixels[pixel + 3] = 255

            height_left = heights[y * size + previous_x]
            height_right = heights[y * size + next_x]
            height_down = heights[previous_y * size + x]
            height_up = heights[next_y * size + x]
            dx = (height_right - height_left) * normal_strength
            dy = (height_up - height_down) * normal_strength
            normal = Vector((-dx, -dy, 1.0)).normalized()
            normal_pixels[pixel] = to_byte(normal.x * 0.5 + 0.5)
            normal_pixels[pixel + 1] = to_byte(normal.y * 0.5 + 0.5)
            normal_pixels[pixel + 2] = to_byte(normal.z * 0.5 + 0.5)
            normal_pixels[pixel + 3] = 255

            metallic = lerp(spec["metallic"], spec["wornMetallic"], worn)
            smoothness = lerp(spec["smoothness"], spec["wornSmoothness"], worn)
            smoothness = clamp01(smoothness + (macro - 0.5) * 0.08 - dusty * 0.10)
            mask_pixels[pixel] = to_byte(metallic)
            mask_pixels[pixel + 1] = 0
            mask_pixels[pixel + 2] = 0
            mask_pixels[pixel + 3] = to_byte(smoothness)

            neighbor_average = (height_left + height_right + height_down + height_up) * 0.25
            cavity = max(0.0, neighbor_average - height)
            occlusion = clamp01(1.0 - cavity * 22.0 - worn * 0.08)
            value = to_byte(occlusion)
            occlusion_pixels[pixel] = value
            occlusion_pixels[pixel + 1] = value
            occlusion_pixels[pixel + 2] = value
            occlusion_pixels[pixel + 3] = 255

    prefix = f"T_{asset_id}_{spec['slug']}"
    paths = {
        "baseColor": output_dir / f"{prefix}_BaseColor.png",
        "normal": output_dir / f"{prefix}_Normal.png",
        "metallicSmoothness": output_dir / f"{prefix}_MetallicSmoothness.png",
        "occlusion": output_dir / f"{prefix}_Occlusion.png",
    }
    write_png_rgba(paths["baseColor"], size, size, base_pixels, srgb=True)
    write_png_rgba(paths["normal"], size, size, normal_pixels, srgb=False)
    write_png_rgba(paths["metallicSmoothness"], size, size, mask_pixels, srgb=False)
    write_png_rgba(paths["occlusion"], size, size, occlusion_pixels, srgb=False)
    return paths


def reset_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.unit_settings.length_unit = "METERS"
    scene.render.resolution_x = 640
    scene.render.resolution_y = 640
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    bpy.context.preferences.filepaths.save_version = 0
    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_EEVEE"


def load_image(path: Path, *, non_color: bool):
    image = bpy.data.images.load(str(path), check_existing=True)
    if non_color:
        image.colorspace_settings.name = "Non-Color"
    else:
        image.colorspace_settings.name = "sRGB"
    return image


def make_textured_material(spec: dict, paths: dict):
    material = bpy.data.materials.new(name=spec["name"])
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    for node in list(nodes):
        nodes.remove(node)

    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (760, 80)
    principled = nodes.new("ShaderNodeBsdfPrincipled")
    principled.location = (480, 80)
    links.new(principled.outputs["BSDF"], output.inputs["Surface"])

    coordinates = nodes.new("ShaderNodeTexCoord")
    coordinates.location = (-1120, 100)
    mapping = nodes.new("ShaderNodeMapping")
    mapping.location = (-930, 100)
    mapping.inputs["Scale"].default_value = (
        spec["tiling"][0],
        spec["tiling"][1],
        1.0,
    )
    links.new(coordinates.outputs["UV"], mapping.inputs["Vector"])

    base = nodes.new("ShaderNodeTexImage")
    base.name = "BaseColor"
    base.location = (-700, 330)
    base.image = load_image(paths["baseColor"], non_color=False)
    base.extension = "REPEAT"
    links.new(mapping.outputs["Vector"], base.inputs["Vector"])

    occlusion = nodes.new("ShaderNodeTexImage")
    occlusion.name = "Occlusion"
    occlusion.location = (-700, 80)
    occlusion.image = load_image(paths["occlusion"], non_color=True)
    occlusion.extension = "REPEAT"
    links.new(mapping.outputs["Vector"], occlusion.inputs["Vector"])

    multiply = nodes.new("ShaderNodeMixRGB")
    multiply.blend_type = "MULTIPLY"
    multiply.inputs[0].default_value = 1.0
    multiply.location = (170, 330)
    links.new(base.outputs["Color"], multiply.inputs[1])
    links.new(occlusion.outputs["Color"], multiply.inputs[2])
    links.new(multiply.outputs["Color"], principled.inputs["Base Color"])

    mask = nodes.new("ShaderNodeTexImage")
    mask.name = "MetallicSmoothness"
    mask.location = (-700, -170)
    mask.image = load_image(paths["metallicSmoothness"], non_color=True)
    mask.extension = "REPEAT"
    links.new(mapping.outputs["Vector"], mask.inputs["Vector"])

    separate = nodes.new("ShaderNodeSeparateColor")
    separate.location = (-180, -150)
    links.new(mask.outputs["Color"], separate.inputs["Color"])
    links.new(separate.outputs["Red"], principled.inputs["Metallic"])

    invert_smoothness = nodes.new("ShaderNodeMath")
    invert_smoothness.operation = "SUBTRACT"
    invert_smoothness.inputs[0].default_value = 1.0
    invert_smoothness.location = (160, -120)
    links.new(mask.outputs["Alpha"], invert_smoothness.inputs[1])
    links.new(invert_smoothness.outputs[0], principled.inputs["Roughness"])

    normal_texture = nodes.new("ShaderNodeTexImage")
    normal_texture.name = "Normal"
    normal_texture.location = (-700, -430)
    normal_texture.image = load_image(paths["normal"], non_color=True)
    normal_texture.extension = "REPEAT"
    links.new(mapping.outputs["Vector"], normal_texture.inputs["Vector"])

    normal_map = nodes.new("ShaderNodeNormalMap")
    normal_map.space = "TANGENT"
    normal_map.inputs["Strength"].default_value = spec["normalScale"]
    normal_map.location = (160, -390)
    links.new(normal_texture.outputs["Color"], normal_map.inputs["Color"])
    links.new(normal_map.outputs["Normal"], principled.inputs["Normal"])
    return material


def apply_bevel(obj, width: float, segments: int = 2) -> None:
    if width <= 0.0:
        return
    modifier = obj.modifiers.new(name="EdgeSoftening", type="BEVEL")
    modifier.width = width
    modifier.segments = segments
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=modifier.name)


def freeze_object(obj) -> None:
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    obj.select_set(False)


def add_cube(name, dimensions, location, material, *, rotation=(0.0, 0.0, 0.0), bevel=0.02):
    bpy.ops.mesh.primitive_cube_add(location=location, rotation=rotation)
    obj = bpy.context.object
    obj.name = name
    obj.data.name = f"{name}_Mesh"
    obj.dimensions = dimensions
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    apply_bevel(obj, bevel)
    obj.data.materials.append(material)
    freeze_object(obj)
    return obj


def add_cylinder(
    name,
    radius,
    depth,
    location,
    material,
    *,
    rotation=(0.0, 0.0, 0.0),
    vertices=32,
    bevel=0.015,
):
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices,
        radius=radius,
        depth=depth,
        end_fill_type="NGON",
        location=location,
        rotation=rotation,
    )
    obj = bpy.context.object
    obj.name = name
    obj.data.name = f"{name}_Mesh"
    apply_bevel(obj, bevel)
    obj.data.materials.append(material)
    for polygon in obj.data.polygons:
        polygon.use_smooth = abs(polygon.normal.z) < 0.9
    freeze_object(obj)
    return obj


def add_torus(
    name,
    major_radius,
    minor_radius,
    location,
    material,
    *,
    rotation=(0.0, 0.0, 0.0),
):
    bpy.ops.mesh.primitive_torus_add(
        align="WORLD",
        major_segments=32,
        minor_segments=8,
        location=location,
        rotation=rotation,
        major_radius=major_radius,
        minor_radius=minor_radius,
    )
    obj = bpy.context.object
    obj.name = name
    obj.data.name = f"{name}_Mesh"
    obj.data.materials.append(material)
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    freeze_object(obj)
    return obj


def add_pipe(name, start, end, radius, material, *, bevel=0.008):
    start_vector = Vector(start)
    end_vector = Vector(end)
    direction = end_vector - start_vector
    midpoint = (start_vector + end_vector) * 0.5
    rotation = Vector((0.0, 0.0, 1.0)).rotation_difference(direction.normalized()).to_euler()
    return add_cylinder(
        name,
        radius,
        direction.length,
        midpoint,
        material,
        rotation=rotation,
        vertices=20,
        bevel=bevel,
    )


def join_material_group(name: str, objects: list, material, root):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    joined = bpy.context.object
    joined.name = name
    joined.data.name = f"{name}_Mesh"
    joined.data.materials.clear()
    joined.data.materials.append(material)
    for polygon in joined.data.polygons:
        polygon.material_index = 0
    joined.parent = root
    joined.select_set(False)
    return joined


def remove_degenerate_geometry(obj, distance: float = 1e-7) -> None:
    """Remove zero-area export artifacts before they reach an engine importer."""
    mesh = obj.data
    editable = bmesh.new()
    try:
        editable.from_mesh(mesh)
        bmesh.ops.dissolve_degenerate(
            editable,
            dist=distance,
            edges=list(editable.edges),
        )
        editable.to_mesh(mesh)
        mesh.update()
    finally:
        editable.free()


def build_asset(asset_id: str, materials: dict):
    teal = materials["M_WornTeal"]
    dark = materials["M_DarkMetal"]
    orange = materials["M_SafetyOrange"]
    groups = {teal: [], dark: [], orange: []}
    source_part_count = 0

    def part(material, obj):
        nonlocal source_part_count
        groups[material].append(obj)
        source_part_count += 1
        return obj

    root = bpy.data.objects.new(asset_id, None)
    root.empty_display_type = "CUBE"
    root.empty_display_size = 0.16
    bpy.context.collection.objects.link(root)

    part(dark, add_cube(f"{asset_id}_Skid", (1.46, 0.78, 0.12), (0.0, 0.0, 0.08), dark, bevel=0.035))
    for x in (-0.66, 0.66):
        for y in (-0.31, 0.31):
            suffix = f"{'L' if x < 0 else 'R'}{'F' if y < 0 else 'B'}"
            part(dark, add_cube(f"{asset_id}_FramePost_{suffix}", (0.07, 0.07, 1.42), (x, y, 0.78), dark, bevel=0.012))
    for y in (-0.31, 0.31):
        part(dark, add_cube(f"{asset_id}_TopRail_{'F' if y < 0 else 'B'}", (1.38, 0.07, 0.07), (0.0, y, 1.46), dark, bevel=0.012))

    part(teal, add_cylinder(f"{asset_id}_Tank", 0.39, 1.20, (-0.24, 0.02, 0.73), teal, vertices=40, bevel=0.025))
    part(teal, add_cylinder(f"{asset_id}_TankTop", 0.31, 0.10, (-0.24, 0.02, 1.38), teal, vertices=40, bevel=0.02))
    for z in (0.23, 0.70, 1.20):
        part(dark, add_torus(f"{asset_id}_TankBand_{int(z * 100)}", 0.395, 0.027, (-0.24, 0.02, z), dark))

    part(teal, add_cube(f"{asset_id}_ControlBox", (0.43, 0.31, 0.52), (0.39, -0.18, 0.91), teal, bevel=0.035))
    part(dark, add_cube(f"{asset_id}_ControlFace", (0.34, 0.035, 0.34), (0.39, -0.355, 0.94), dark, bevel=0.018))
    part(orange, add_cube(f"{asset_id}_WarningPlate", (0.24, 0.018, 0.10), (0.39, -0.382, 0.78), orange, bevel=0.009))

    for index, y in enumerate((0.02, 0.22), start=1):
        part(dark, add_cylinder(f"{asset_id}_Filter_{index}", 0.105, 0.62, (0.44, y, 0.54), dark, vertices=28, bevel=0.015))
        part(orange, add_torus(f"{asset_id}_FilterRing_{index}", 0.108, 0.018, (0.44, y, 0.78), orange))

    part(dark, add_pipe(f"{asset_id}_Pipe_TankToPump", (0.02, 0.02, 0.43), (0.34, 0.02, 0.43), 0.045, dark))
    part(dark, add_pipe(f"{asset_id}_Pipe_PumpRise", (0.55, 0.12, 0.75), (0.55, 0.12, 1.29), 0.042, dark))
    part(orange, add_pipe(f"{asset_id}_Pipe_Bypass", (-0.25, -0.40, 0.35), (0.43, -0.40, 0.35), 0.034, orange))
    part(orange, add_pipe(f"{asset_id}_Pipe_BypassRise", (0.43, -0.40, 0.35), (0.43, -0.40, 0.61), 0.034, orange))

    front_rotation = (math.radians(90.0), 0.0, 0.0)
    part(dark, add_cylinder(f"{asset_id}_GaugeFace", 0.12, 0.035, (0.39, -0.385, 1.10), dark, rotation=front_rotation, vertices=32, bevel=0.008))
    part(orange, add_torus(f"{asset_id}_GaugeRing", 0.12, 0.018, (0.39, -0.408, 1.10), orange, rotation=front_rotation))

    part(orange, add_torus(f"{asset_id}_ValveWheel", 0.13, 0.018, (-0.33, -0.435, 0.45), orange, rotation=front_rotation))
    part(orange, add_cube(f"{asset_id}_ValveSpokeH", (0.22, 0.025, 0.025), (-0.33, -0.435, 0.45), orange, bevel=0.006))
    part(orange, add_cube(f"{asset_id}_ValveSpokeV", (0.025, 0.025, 0.22), (-0.33, -0.435, 0.45), orange, bevel=0.006))

    meshes = [
        join_material_group(f"{asset_id}_Painted", groups[teal], teal, root),
        join_material_group(f"{asset_id}_Hardware", groups[dark], dark, root),
        join_material_group(f"{asset_id}_Accent", groups[orange], orange, root),
    ]
    for mesh in meshes:
        remove_degenerate_geometry(mesh)
    return root, meshes, source_part_count


def configure_preview(asset_meshes, preview_path: Path) -> None:
    ground_material = bpy.data.materials.new(name="M_PreviewGround")
    ground_material.diffuse_color = (0.065, 0.05, 0.04, 1.0)
    ground_shader = ground_material.node_tree.nodes.get("Principled BSDF")
    ground_shader.inputs["Base Color"].default_value = (0.065, 0.05, 0.04, 1.0)
    ground_shader.inputs["Roughness"].default_value = 0.86

    bpy.ops.mesh.primitive_plane_add(size=7.0, location=(0.0, 0.0, 0.0))
    ground = bpy.context.object
    ground.name = "__PREVIEW_Ground"
    ground.data.materials.append(ground_material)

    target = Vector((0.0, 0.0, 0.74))
    bpy.ops.object.camera_add(location=(2.65, -3.45, 2.15))
    camera = bpy.context.object
    camera.name = "__PREVIEW_Camera"
    camera.data.lens = 58
    camera.data.sensor_width = 36
    camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = camera

    lights = (
        ("__PREVIEW_Key", "AREA", (2.2, -2.2, 3.5), 980, 2.8, (1.0, 0.84, 0.70)),
        ("__PREVIEW_Fill", "AREA", (-2.5, -0.4, 1.9), 560, 2.4, (0.46, 0.62, 1.0)),
        ("__PREVIEW_Rim", "AREA", (0.8, 2.5, 2.8), 760, 2.0, (1.0, 0.46, 0.24)),
    )
    for name, light_type, location, energy, size, color in lights:
        bpy.ops.object.light_add(type=light_type, location=location)
        light = bpy.context.object
        light.name = name
        light.data.energy = energy
        light.data.shape = "DISK"
        light.data.size = size
        light.data.color = color
        light.rotation_euler = (target - light.location).to_track_quat("-Z", "Y").to_euler()

    world = bpy.context.scene.world or bpy.data.worlds.new("PreviewWorld")
    bpy.context.scene.world = world
    background = world.node_tree.nodes.get("Background")
    background.inputs["Color"].default_value = (0.012, 0.018, 0.028, 1.0)
    background.inputs["Strength"].default_value = 0.28

    for obj in asset_meshes:
        obj.select_set(False)
    bpy.context.scene.render.filepath = str(preview_path)
    bpy.ops.render.render(write_still=True)


def export_fbx(root, meshes, fbx_path: Path) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    for obj in meshes:
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
        use_space_transform=True,
        bake_space_transform=True,
        add_leaf_bones=False,
        bake_anim=False,
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        path_mode="AUTO",
    )


def inspect_exported_fbx(fbx_path: Path) -> dict:
    """Read the exchange file back through Blender's FBX importer.

    Source topology is useful authoring evidence, but Unity consumes the FBX
    boundary. FBX export may normalize or discard degenerate faces, so the
    cross-tool triangle contract must come from this round trip.
    """
    existing_pointers = {obj.as_pointer() for obj in bpy.data.objects}
    bpy.ops.object.select_all(action="DESELECT")
    bpy.ops.import_scene.fbx(filepath=str(fbx_path))
    imported_objects = [
        obj for obj in bpy.data.objects if obj.as_pointer() not in existing_pointers
    ]
    imported_meshes = [obj for obj in imported_objects if obj.type == "MESH"]
    try:
        vertex_count = 0
        polygon_count = 0
        triangle_count = 0
        degenerate_triangle_count = 0
        material_slot_count = 0
        for obj in imported_meshes:
            mesh = obj.data
            mesh.calc_loop_triangles()
            vertex_count += len(mesh.vertices)
            polygon_count += len(mesh.polygons)
            total, degenerate = triangle_statistics(mesh)
            triangle_count += total
            degenerate_triangle_count += degenerate
            material_slot_count += len(mesh.materials)
        return {
            "meshObjectCount": len(imported_meshes),
            "materialSlotCount": material_slot_count,
            "vertexCount": vertex_count,
            "polygonCount": polygon_count,
            "triangleCount": triangle_count,
            "degenerateTriangleCount": degenerate_triangle_count,
            "nonDegenerateTriangleCount": triangle_count - degenerate_triangle_count,
        }
    finally:
        for obj in imported_objects:
            bpy.data.objects.remove(obj, do_unlink=True)


def triangle_statistics(mesh) -> tuple[int, int]:
    mesh.calc_loop_triangles()
    degenerate = 0
    for triangle in mesh.loop_triangles:
        first, second, third = (
            mesh.vertices[index].co for index in triangle.vertices
        )
        area_twice = (second - first).cross(third - first).length
        if area_twice <= 1e-10:
            degenerate += 1
    return len(mesh.loop_triangles), degenerate


def geometry_report(meshes, source_part_count: int) -> dict:
    vertex_count = 0
    polygon_count = 0
    triangle_count = 0
    degenerate_triangle_count = 0
    world_points = []
    material_slot_count = 0
    for obj in meshes:
        mesh = obj.data
        total_triangles, degenerate_triangles = triangle_statistics(mesh)
        vertex_count += len(mesh.vertices)
        polygon_count += len(mesh.polygons)
        triangle_count += total_triangles
        degenerate_triangle_count += degenerate_triangles
        material_slot_count += len(mesh.materials)
        world_points.extend(obj.matrix_world @ Vector(corner) for corner in obj.bound_box)

    minimum = Vector((min(p.x for p in world_points), min(p.y for p in world_points), min(p.z for p in world_points)))
    maximum = Vector((max(p.x for p in world_points), max(p.y for p in world_points), max(p.z for p in world_points)))
    size = maximum - minimum
    return {
        "sourcePartCount": source_part_count,
        "meshObjectCount": len(meshes),
        "materialSlotCount": material_slot_count,
        "vertexCount": vertex_count,
        "polygonCount": polygon_count,
        "triangleCount": triangle_count,
        "degenerateTriangleCount": degenerate_triangle_count,
        "nonDegenerateTriangleCount": triangle_count - degenerate_triangle_count,
        "boundsMeters": {
            "min": [round(value, 6) for value in minimum],
            "max": [round(value, 6) for value in maximum],
            "size": [round(value, 6) for value in size],
        },
        "suggestedBoxCollider": {
            "center": [round(value, 6) for value in ((minimum + maximum) * 0.5)],
            "size": [round(value, 6) for value in size],
        },
        "objects": [obj.name for obj in meshes],
    }


def texture_manifest_entry(path: Path, semantic: str, color_space: str, channels: str) -> dict:
    return {
        "file": path.name,
        "semantic": semantic,
        "colorSpace": color_space,
        "channels": channels,
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
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

    texture_paths = {
        spec["name"]: generate_texture_set(output_dir, asset_id, spec, args.texture_size)
        for spec in MATERIAL_SPECS
    }

    reset_scene()
    materials = {
        spec["name"]: make_textured_material(spec, texture_paths[spec["name"]])
        for spec in MATERIAL_SPECS
    }
    root, meshes, source_part_count = build_asset(asset_id, materials)
    if len(meshes) != EXPECTED_MESH_COUNT:
        raise RuntimeError(f"Expected {EXPECTED_MESH_COUNT} merged meshes, got {len(meshes)}")
    configure_preview(meshes, preview_path)
    geometry = geometry_report(meshes, source_part_count)
    if geometry["degenerateTriangleCount"] != 0:
        raise RuntimeError(
            f"Source mesh contains degenerate triangles after cleanup: {geometry}"
        )
    export_fbx(root, meshes, fbx_path)
    bpy.ops.wm.save_as_mainfile(filepath=str(blend_path), compress=True)
    fbx_round_trip = inspect_exported_fbx(fbx_path)
    if (
        fbx_round_trip["meshObjectCount"] != EXPECTED_MESH_COUNT
        or fbx_round_trip["materialSlotCount"] != len(MATERIAL_SPECS)
        or fbx_round_trip["triangleCount"] <= 0
        or fbx_round_trip["degenerateTriangleCount"] != 0
    ):
        raise RuntimeError(f"FBX round-trip contract failed: {fbx_round_trip}")
    geometry["sourceTriangleCount"] = geometry["triangleCount"]
    geometry["triangleCount"] = fbx_round_trip["triangleCount"]
    geometry["fbxRoundTrip"] = fbx_round_trip

    material_entries = []
    texture_files = []
    for spec in MATERIAL_SPECS:
        paths = texture_paths[spec["name"]]
        texture_files.extend(paths.values())
        material_entries.append(
            {
                "name": spec["name"],
                "shader": "Universal Render Pipeline/Lit",
                "textureTiling": list(spec["tiling"]),
                "normalScale": spec["normalScale"],
                "occlusionStrength": spec["occlusionStrength"],
                "fallback": {
                    "metallic": spec["metallic"],
                    "smoothness": spec["smoothness"],
                },
                "textures": {
                    "baseColor": texture_manifest_entry(paths["baseColor"], "base-color", "sRGB", "RGB"),
                    "normal": texture_manifest_entry(paths["normal"], "tangent-normal-open-gl", "linear", "RGB"),
                    "metallicSmoothness": texture_manifest_entry(
                        paths["metallicSmoothness"],
                        "urp-metallic-smoothness",
                        "linear",
                        "R=metallic,A=smoothness",
                    ),
                    "occlusion": texture_manifest_entry(paths["occlusion"], "ambient-occlusion", "linear", "G=occlusion"),
                },
            }
        )

    generated_files = [blend_path, fbx_path, preview_path, *texture_files]
    manifest = {
        "schemaVersion": 2,
        "harnessVersion": HARNESS_VERSION,
        "status": "passed",
        "asset": {
            "id": asset_id,
            "displayName": "废土水循环设施",
            "purpose": "Second-geometry, textured and material-merged Blender-to-Unity probe",
            "sourceKind": "procedural-bpy-and-deterministic-png",
            "intendedUse": "Pipeline validation and Foundation Prototype candidate; not approved production art",
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
            "fbxSpaceTransformBaked": True,
            "rootTransform": "identity",
        },
        "textureContract": {
            "width": args.texture_size,
            "height": args.texture_size,
            "fileFormat": "PNG RGBA8",
            "normalConvention": "OpenGL tangent-space (+Y)",
            "metallicSmoothnessPacking": "R=metallic, A=smoothness",
            "occlusionPacking": "G=occlusion",
            "tileable": True,
            "deterministicSeededPixels": True,
        },
        "materials": material_entries,
        "geometry": geometry,
        "acceptance": {
            "previewRendered": preview_path.exists() and preview_path.stat().st_size > 0,
            "fbxExported": fbx_path.exists() and fbx_path.stat().st_size > 0,
            "fbxRoundTripVerified": True,
            "blendSaved": blend_path.exists() and blend_path.stat().st_size > 0,
            "meshMergedByMaterial": len(meshes) == EXPECTED_MESH_COUNT,
            "textureSetComplete": len(texture_files) == len(MATERIAL_SPECS) * 4,
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
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(f"SSFRAMEWORK_BLENDER_TEXTURED_PROP_MANIFEST={manifest_path}")


if __name__ == "__main__":
    main()
