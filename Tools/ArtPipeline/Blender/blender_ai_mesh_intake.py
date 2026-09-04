"""Inspect and archive an externally generated Blender mesh candidate.

The intake is deliberately evidence-oriented: it preserves the source, records
geometry/material/texture facts, creates portable exchange files, and keeps the
result marked as a candidate.  It does not decimate, retopologize, split parts,
or silently declare AI output game-ready.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import shutil
import sys
from array import array
from datetime import datetime, timezone
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector


HARNESS_VERSION = "0.1.0"
SCHEMA_VERSION = 1
ASSET_ID_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9_]+$")
PBR_INPUTS = ("Base Color", "Metallic", "Roughness", "Normal", "Alpha", "Emission Color")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--asset-id", required=True)
    parser.add_argument("--source-object", default="model")
    parser.add_argument(
        "--preserve-hierarchy",
        action="store_true",
        help="Preserve Empty/Mesh descendants and their parent links for articulated props.",
    )
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
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


def file_record(path: Path, output_dir: Path, *, kind: str) -> dict:
    return {
        "path": path.relative_to(output_dir).as_posix(),
        "kind": kind,
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def descendants(root) -> list:
    result = []
    stack = list(root.children)
    while stack:
        current = stack.pop()
        result.append(current)
        stack.extend(current.children)
    return result


def resolve_target(source_object: str) -> tuple[object, list]:
    root = bpy.data.objects.get(source_object)
    if root is None:
        available = ", ".join(sorted(obj.name for obj in bpy.data.objects))
        raise RuntimeError(f"Source object '{source_object}' was not found. Available: {available}")
    if root.type == "MESH":
        meshes = [root]
    else:
        meshes = [obj for obj in descendants(root) if obj.type == "MESH"]
    if not meshes:
        raise RuntimeError(f"Source object '{source_object}' contains no Mesh objects")
    return root, sorted(meshes, key=lambda obj: obj.name)


def triangle_statistics(mesh) -> tuple[int, int]:
    mesh.calc_loop_triangles()
    degenerate = 0
    for triangle in mesh.loop_triangles:
        first, second, third = (mesh.vertices[index].co for index in triangle.vertices)
        if (second - first).cross(third - first).length <= 1e-10:
            degenerate += 1
    return len(mesh.loop_triangles), degenerate


def connected_component_count(mesh) -> int:
    if not mesh.vertices:
        return 0
    adjacency = [[] for _ in mesh.vertices]
    for edge in mesh.edges:
        first, second = edge.vertices
        adjacency[first].append(second)
        adjacency[second].append(first)
    visited = bytearray(len(mesh.vertices))
    components = 0
    for start in range(len(mesh.vertices)):
        if visited[start]:
            continue
        components += 1
        visited[start] = 1
        stack = [start]
        while stack:
            current = stack.pop()
            for neighbor in adjacency[current]:
                if not visited[neighbor]:
                    visited[neighbor] = 1
                    stack.append(neighbor)
    return components


def vector_values(value) -> list[float]:
    return [round(float(component), 6) for component in value]


def inspect_mesh(obj) -> dict:
    mesh = obj.data
    mesh.calc_loop_triangles()
    triangle_count, degenerate_triangle_count = triangle_statistics(mesh)
    face_keys = set()
    duplicate_face_count = 0
    for polygon in mesh.polygons:
        key = tuple(sorted(polygon.vertices))
        if key in face_keys:
            duplicate_face_count += 1
        else:
            face_keys.add(key)
    validation_copy = mesh.copy()
    try:
        mesh_validation_would_change = validation_copy.validate(
            verbose=False,
            clean_customdata=False,
        )
        validation_copy.calc_loop_triangles()
        validated_polygon_count = len(validation_copy.polygons)
        validated_triangle_count = len(validation_copy.loop_triangles)
    finally:
        bpy.data.meshes.remove(validation_copy)
    editable = bmesh.new()
    try:
        editable.from_mesh(mesh)
        loose_vertex_count = sum(1 for vertex in editable.verts if not vertex.link_edges)
        loose_edge_count = sum(1 for edge in editable.edges if not edge.link_faces)
        boundary_edge_count = sum(1 for edge in editable.edges if len(edge.link_faces) == 1)
        non_manifold_edge_count = sum(1 for edge in editable.edges if len(edge.link_faces) != 2)
    finally:
        editable.free()

    uv_layer = mesh.uv_layers.active
    uv_minimum = Vector((math.inf, math.inf))
    uv_maximum = Vector((-math.inf, -math.inf))
    out_of_unit_range_loop_count = 0
    if uv_layer is not None:
        for loop in uv_layer.data:
            uv_minimum.x = min(uv_minimum.x, loop.uv.x)
            uv_minimum.y = min(uv_minimum.y, loop.uv.y)
            uv_maximum.x = max(uv_maximum.x, loop.uv.x)
            uv_maximum.y = max(uv_maximum.y, loop.uv.y)
            if (
                loop.uv.x < -1e-6
                or loop.uv.x > 1.0 + 1e-6
                or loop.uv.y < -1e-6
                or loop.uv.y > 1.0 + 1e-6
            ):
                out_of_unit_range_loop_count += 1

    surface_area = 0.0
    uv_area = 0.0
    degenerate_uv_triangle_count = 0
    for triangle in mesh.loop_triangles:
        positions = [obj.matrix_world @ mesh.vertices[index].co for index in triangle.vertices]
        world_area = (positions[1] - positions[0]).cross(positions[2] - positions[0]).length * 0.5
        surface_area += world_area
        if uv_layer is None:
            continue
        uvs = [uv_layer.data[loop_index].uv for loop_index in triangle.loops]
        first = uvs[1] - uvs[0]
        second = uvs[2] - uvs[0]
        triangle_uv_area = abs(first.x * second.y - first.y * second.x) * 0.5
        uv_area += triangle_uv_area
        if world_area > 1e-10 and triangle_uv_area <= 1e-12:
            degenerate_uv_triangle_count += 1

    return {
        "name": obj.name,
        "dataName": mesh.name,
        "vertexCount": len(mesh.vertices),
        "edgeCount": len(mesh.edges),
        "polygonCount": len(mesh.polygons),
        "triangleCount": triangle_count,
        "degenerateTriangleCount": degenerate_triangle_count,
        "duplicateFaceCount": duplicate_face_count,
        "meshValidationWouldChange": bool(mesh_validation_would_change),
        "validatedPolygonCount": validated_polygon_count,
        "validatedTriangleCount": validated_triangle_count,
        "quadCount": sum(1 for polygon in mesh.polygons if len(polygon.vertices) == 4),
        "ngonCount": sum(1 for polygon in mesh.polygons if len(polygon.vertices) > 4),
        "connectedComponentCount": connected_component_count(mesh),
        "topology": {
            "looseVertexCount": loose_vertex_count,
            "looseEdgeCount": loose_edge_count,
            "boundaryEdgeCount": boundary_edge_count,
            "nonManifoldEdgeCount": non_manifold_edge_count,
        },
        "uv": {
            "layers": [layer.name for layer in mesh.uv_layers],
            "activeLayer": uv_layer.name if uv_layer is not None else "",
            "bounds": {
                "min": vector_values(uv_minimum) if uv_layer is not None else [],
                "max": vector_values(uv_maximum) if uv_layer is not None else [],
            },
            "areaSum": round(uv_area, 6),
            "degenerateTriangleCount": degenerate_uv_triangle_count,
            "outOfUnitRangeLoopCount": out_of_unit_range_loop_count,
        },
        "surfaceAreaSquareMetersAtCurrentScale": round(surface_area, 6),
        "materialSlots": [material.name if material else "" for material in mesh.materials],
        "modifiers": [modifier.type for modifier in obj.modifiers],
        "transform": {
            "location": vector_values(obj.location),
            "rotationEulerRadians": vector_values(obj.rotation_euler),
            "scale": vector_values(obj.scale),
        },
    }


def world_bounds(meshes: list) -> dict:
    minimum = Vector((math.inf, math.inf, math.inf))
    maximum = Vector((-math.inf, -math.inf, -math.inf))
    for obj in meshes:
        for corner in obj.bound_box:
            world = obj.matrix_world @ Vector(corner)
            minimum.x = min(minimum.x, world.x)
            minimum.y = min(minimum.y, world.y)
            minimum.z = min(minimum.z, world.z)
            maximum.x = max(maximum.x, world.x)
            maximum.y = max(maximum.y, world.y)
            maximum.z = max(maximum.z, world.z)
    return {
        "min": vector_values(minimum),
        "max": vector_values(maximum),
        "size": vector_values(maximum - minimum),
        "center": vector_values((minimum + maximum) * 0.5),
    }


def linked_images(socket) -> list:
    images = []
    stack = [link.from_node for link in socket.links]
    visited = set()
    while stack:
        node = stack.pop()
        pointer = node.as_pointer()
        if pointer in visited:
            continue
        visited.add(pointer)
        if node.type == "TEX_IMAGE" and node.image is not None:
            images.append(node.image)
        for node_input in node.inputs:
            stack.extend(link.from_node for link in node_input.links)
    return images


def inspect_materials(meshes: list) -> tuple[list[dict], dict[int, set[str]], list]:
    materials = []
    image_usages: dict[int, set[str]] = {}
    images_by_pointer = {}
    seen = set()
    for obj in meshes:
        for material in obj.data.materials:
            if material is None or material.as_pointer() in seen:
                continue
            seen.add(material.as_pointer())
            entry = {
                "name": material.name,
                "useNodes": bool(material.use_nodes),
                "blendMethod": getattr(material, "surface_render_method", ""),
                "principledInputs": {},
                "nodes": [],
            }
            if material.use_nodes and material.node_tree is not None:
                entry["nodes"] = [node.type for node in material.node_tree.nodes]
                principled_nodes = [node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED"]
                for principled in principled_nodes:
                    for input_name in PBR_INPUTS:
                        socket = principled.inputs.get(input_name)
                        if socket is None:
                            continue
                        sources = linked_images(socket)
                        entry["principledInputs"][input_name] = {
                            "linked": bool(socket.is_linked),
                            "images": [image.name for image in sources],
                        }
                        for image in sources:
                            pointer = image.as_pointer()
                            image_usages.setdefault(pointer, set()).add(input_name)
                            images_by_pointer[pointer] = image
            materials.append(entry)
    return materials, image_usages, list(images_by_pointer.values())


def packed_bytes(image) -> bytes | None:
    packed = getattr(image, "packed_file", None)
    if packed is not None and getattr(packed, "data", None) is not None:
        return bytes(packed.data)
    packed_files = getattr(image, "packed_files", None)
    if packed_files:
        first = packed_files[0]
        if getattr(first, "data", None) is not None:
            return bytes(first.data)
    return None


def safe_filename(name: str, fallback: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "_", Path(name).name).strip("._")
    return cleaned or fallback


def archive_images(images: list, usages: dict[int, set[str]], texture_dir: Path) -> tuple[list[dict], dict[int, Path]]:
    records = []
    destinations = {}
    used_names = set()
    for index, image in enumerate(sorted(images, key=lambda item: item.name)):
        resolved = Path(bpy.path.abspath(image.filepath)) if image.filepath else None
        filename = safe_filename(
            resolved.name if resolved is not None and resolved.name else image.name,
            f"image_{index:02d}.png",
        )
        stem = Path(filename).stem
        suffix = Path(filename).suffix or ".png"
        candidate = filename
        serial = 2
        while candidate.lower() in used_names:
            candidate = f"{stem}_{serial}{suffix}"
            serial += 1
        used_names.add(candidate.lower())
        destination = texture_dir / candidate
        source_kind = "missing"
        if resolved is not None and resolved.is_file():
            shutil.copy2(resolved, destination)
            source_kind = "external-file"
        else:
            data = packed_bytes(image)
            if data:
                destination.write_bytes(data)
                source_kind = "packed-bytes"
        exists = destination.is_file() and destination.stat().st_size > 0
        destinations[image.as_pointer()] = destination
        records.append(
            {
                "name": image.name,
                "usages": sorted(usages.get(image.as_pointer(), set())),
                "originalFilepath": image.filepath,
                "resolvedOriginalPath": str(resolved) if resolved is not None else "",
                "originalExists": bool(resolved is not None and resolved.is_file()),
                "packedInSource": packed_bytes(image) is not None,
                "archiveSource": source_kind,
                "archivedPath": destination.relative_to(texture_dir.parent).as_posix() if exists else "",
                "archived": exists,
                "width": int(image.size[0]),
                "height": int(image.size[1]),
                "channels": int(image.channels),
                "colorspace": image.colorspace_settings.name,
                "alphaMode": image.alpha_mode,
                "fileFormat": image.file_format,
                "bytes": destination.stat().st_size if exists else 0,
                "sha256": sha256(destination) if exists else "",
            }
        )
    return records, destinations


def first_image_for_usage(images: list, usages: dict[int, set[str]], usage: str):
    return next((image for image in images if usage in usages.get(image.as_pointer(), set())), None)


def build_metallic_smoothness(
    asset_id: str,
    images: list,
    usages: dict[int, set[str]],
    texture_dir: Path,
) -> dict | None:
    metallic = first_image_for_usage(images, usages, "Metallic")
    roughness = first_image_for_usage(images, usages, "Roughness")
    if metallic is None or roughness is None or tuple(metallic.size) != tuple(roughness.size):
        return None
    width, height = int(metallic.size[0]), int(metallic.size[1])
    if width <= 0 or height <= 0:
        return None
    count = width * height * 4
    metallic_pixels = array("f", [0.0]) * count
    roughness_pixels = array("f", [0.0]) * count
    metallic.pixels.foreach_get(metallic_pixels)
    roughness.pixels.foreach_get(roughness_pixels)
    output_pixels = array("f", [0.0]) * count
    for offset in range(0, count, 4):
        metal_value = min(1.0, max(0.0, metallic_pixels[offset]))
        smoothness = 1.0 - min(1.0, max(0.0, roughness_pixels[offset]))
        output_pixels[offset] = metal_value
        output_pixels[offset + 1] = metal_value
        output_pixels[offset + 2] = metal_value
        output_pixels[offset + 3] = smoothness
    output = bpy.data.images.new(
        name=f"{asset_id}_MetallicSmoothness",
        width=width,
        height=height,
        alpha=True,
        float_buffer=False,
    )
    try:
        output.colorspace_settings.name = "Non-Color"
        output.pixels.foreach_set(output_pixels)
        output.file_format = "PNG"
        output.filepath_raw = str(texture_dir / f"{asset_id}_MetallicSmoothness.png")
        output.save()
        path = Path(output.filepath_raw)
    finally:
        bpy.data.images.remove(output)
    if not path.is_file() or path.stat().st_size <= 0:
        return None
    return {
        "path": path.relative_to(texture_dir.parent).as_posix(),
        "width": width,
        "height": height,
        "colorspace": "linear",
        "packing": "R=metallic, A=1-roughness (URP smoothness)",
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def exportable_target_objects(root, meshes: list, preserve_hierarchy: bool) -> list:
    if not preserve_hierarchy:
        return [root, *meshes] if root.type != "MESH" else meshes
    return [
        obj
        for obj in [root, *descendants(root)]
        if obj.type in {"EMPTY", "MESH"}
    ]


def keep_only_target(root, meshes: list, preserve_hierarchy: bool) -> list:
    targets = exportable_target_objects(root, meshes, preserve_hierarchy)
    keep = {obj.as_pointer() for obj in targets}
    for obj in list(bpy.data.objects):
        if obj.as_pointer() not in keep:
            bpy.data.objects.remove(obj, do_unlink=True)
    return targets


def sanitize_working_meshes(meshes: list) -> list[dict]:
    """Apply Blender's minimal validity repair to background-only copies.

    The imported source data block is never saved back.  This removes invalid
    duplicate faces and corrupt custom data that exchange exporters otherwise
    discard inconsistently; it is not retopology or shape optimization.
    """
    results = []
    for obj in meshes:
        source_mesh = obj.data
        source_mesh.calc_loop_triangles()
        working_mesh = source_mesh.copy()
        changed = working_mesh.validate(verbose=False, clean_customdata=False)
        working_mesh.update()
        working_mesh.calc_loop_triangles()
        obj.data = working_mesh
        results.append(
            {
                "object": obj.name,
                "blenderValidationApplied": bool(changed),
                "sourcePolygonCount": len(source_mesh.polygons),
                "workingPolygonCount": len(working_mesh.polygons),
                "removedPolygonCount": len(source_mesh.polygons) - len(working_mesh.polygons),
                "sourceTriangleCount": len(source_mesh.loop_triangles),
                "workingTriangleCount": len(working_mesh.loop_triangles),
                "removedTriangleCount": len(source_mesh.loop_triangles) - len(working_mesh.loop_triangles),
            }
        )
    return results


def evaluated_mesh_statistics(obj) -> dict:
    """Measure the geometry exporters see after validation and modifiers.

    Source mesh statistics remain useful for topology review, while FBX and
    glTF export with modifiers enabled.  Comparing round trips with the raw
    data block otherwise reports valid non-destructive Bevel/Decimate stacks
    as geometry corruption.
    """
    dependency_graph = bpy.context.evaluated_depsgraph_get()
    evaluated_object = obj.evaluated_get(dependency_graph)
    evaluated_mesh = evaluated_object.to_mesh(
        preserve_all_data_layers=False,
        depsgraph=dependency_graph,
    )
    try:
        evaluated_mesh.calc_loop_triangles()
        return {
            "vertexCount": len(evaluated_mesh.vertices),
            "polygonCount": len(evaluated_mesh.polygons),
            "triangleCount": len(evaluated_mesh.loop_triangles),
        }
    finally:
        evaluated_object.to_mesh_clear()


def select_export_objects(targets: list, meshes: list) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in targets:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]


def export_fbx(targets: list, meshes: list, path: Path, preserve_hierarchy: bool) -> None:
    select_export_objects(targets, meshes)
    bpy.ops.export_scene.fbx(
        filepath=str(path),
        use_selection=True,
        object_types={"EMPTY", "MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        use_space_transform=True,
        # Baking object axes is convenient for a static mesh but can invalidate
        # an Empty used as a runtime hinge. Keep authored local transforms for
        # articulated props and let the importer perform the normal axis map.
        bake_space_transform=not preserve_hierarchy,
        add_leaf_bones=False,
        bake_anim=False,
        use_custom_props=preserve_hierarchy,
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        path_mode="AUTO",
    )


def export_glb(targets: list, meshes: list, path: Path, preserve_hierarchy: bool) -> None:
    select_export_objects(targets, meshes)
    options = {
        "filepath": str(path),
        "export_format": "GLB",
        "use_selection": True,
        "export_apply": True,
        "export_yup": True,
        "export_materials": "EXPORT",
        "export_extras": preserve_hierarchy,
    }
    try:
        bpy.ops.export_scene.gltf(**options)
    except TypeError:
        bpy.ops.export_scene.gltf(
            filepath=str(path),
            export_format="GLB",
            use_selection=True,
        )


def round_trip(path: Path, kind: str) -> dict:
    before = {obj.as_pointer() for obj in bpy.data.objects}
    try:
        if kind == "fbx":
            bpy.ops.import_scene.fbx(filepath=str(path))
        else:
            bpy.ops.import_scene.gltf(filepath=str(path))
        imported = [obj for obj in bpy.data.objects if obj.as_pointer() not in before]
        meshes = [obj for obj in imported if obj.type == "MESH"]
        empties = [obj for obj in imported if obj.type == "EMPTY"]
        triangles = 0
        vertices = 0
        for obj in meshes:
            obj.data.calc_loop_triangles()
            triangles += len(obj.data.loop_triangles)
            vertices += len(obj.data.vertices)
        return {
            "succeeded": True,
            "meshObjectCount": len(meshes),
            "emptyObjectCount": len(empties),
            "parentLinkCount": sum(1 for obj in imported if obj.parent in imported),
            "vertexCount": vertices,
            "triangleCount": triangles,
        }
    except Exception as error:
        return {"succeeded": False, "error": str(error)}
    finally:
        for obj in list(bpy.data.objects):
            if obj.as_pointer() not in before:
                bpy.data.objects.remove(obj, do_unlink=True)


def main() -> None:
    args = parse_args()
    output_dir = Path(args.output_dir).resolve()
    texture_dir = output_dir / "textures"
    output_dir.mkdir(parents=True, exist_ok=True)
    texture_dir.mkdir(parents=True, exist_ok=True)
    source_blend = Path(bpy.data.filepath).resolve()
    if not source_blend.is_file():
        raise RuntimeError("The intake must be launched with a saved source .blend")

    root, meshes = resolve_target(args.source_object)
    mesh_reports = [inspect_mesh(obj) for obj in meshes]
    bounds = world_bounds(meshes)
    materials, usages, images = inspect_materials(meshes)
    image_records, image_destinations = archive_images(images, usages, texture_dir)
    derived_metallic_smoothness = build_metallic_smoothness(
        args.asset_id,
        images,
        usages,
        texture_dir,
    )
    sanitization = sanitize_working_meshes(meshes)
    bpy.context.view_layer.update()
    evaluated_reports = [evaluated_mesh_statistics(obj) for obj in meshes]
    for report, evaluated in zip(mesh_reports, evaluated_reports):
        report["evaluatedAfterSanitization"] = evaluated

    total_triangles = sum(report["triangleCount"] for report in mesh_reports)
    total_vertices = sum(report["vertexCount"] for report in mesh_reports)
    total_evaluated_triangles = sum(report["triangleCount"] for report in evaluated_reports)
    total_evaluated_vertices = sum(report["vertexCount"] for report in evaluated_reports)
    all_have_uv = all(bool(report["uv"]["activeLayer"]) for report in mesh_reports)
    all_textures_archived = bool(image_records) and all(record["archived"] for record in image_records)
    pbr_usages = {usage for values in usages.values() for usage in values}
    has_pbr_core = {"Base Color", "Metallic", "Roughness", "Normal"}.issubset(pbr_usages)

    for image in images:
        destination = image_destinations.get(image.as_pointer())
        if destination is not None and destination.is_file():
            image.filepath = str(destination)

    targets = keep_only_target(root, meshes, args.preserve_hierarchy)
    expected_empty_count = sum(1 for obj in targets if obj.type == "EMPTY")
    expected_parent_link_count = sum(1 for obj in targets if obj.parent in targets)
    bpy.ops.file.pack_all()
    packed_blend = output_dir / f"{args.asset_id}_packed.blend"
    bpy.ops.wm.save_as_mainfile(filepath=str(packed_blend), copy=True)

    fbx_path = output_dir / f"{args.asset_id}.fbx"
    glb_path = output_dir / f"{args.asset_id}.glb"
    export_fbx(targets, meshes, fbx_path, args.preserve_hierarchy)
    export_glb(targets, meshes, glb_path, args.preserve_hierarchy)
    round_trips = {
        "fbx": round_trip(fbx_path, "fbx"),
        "glb": round_trip(glb_path, "glb"),
    }

    warnings = []
    if len(meshes) == 1:
        warnings.append("The candidate is one combined Mesh; gameplay parts are not independently editable or animatable.")
    if sum(report["connectedComponentCount"] for report in mesh_reports) > len(meshes):
        warnings.append("At least one Mesh contains disconnected geometry islands; review fused/floating details manually.")
    if any(report["topology"]["nonManifoldEdgeCount"] for report in mesh_reports):
        warnings.append("Non-manifold or boundary topology exists; inspect before collision, deformation, or destructive editing.")
    duplicate_faces = sum(report["duplicateFaceCount"] for report in mesh_reports)
    if duplicate_faces:
        warnings.append(
            f"Blender validation found {duplicate_faces} duplicate faces in the source; portable outputs use a recorded background-only validation copy."
        )
    if not all_have_uv:
        warnings.append("At least one Mesh has no active UV layer.")
    if not has_pbr_core:
        warnings.append("The material does not expose the full Base Color/Metallic/Roughness/Normal core set.")
    if any(record["width"] > 0 and record["width"] < 4096 for record in image_records):
        warnings.append("Archived textures are below 4K; provider generation settings may differ from DCC delivery resolution.")
    expected_validated_triangles = sum(
        report["validatedTriangleCount"] for report in mesh_reports
    )
    expected_export_triangles = total_evaluated_triangles
    for exchange_kind, result in round_trips.items():
        if result["succeeded"] and result["triangleCount"] != expected_export_triangles:
            warnings.append(
                f"{exchange_kind.upper()} round trip contains {result['triangleCount']} triangles versus {expected_export_triangles} after Blender validation and modifier evaluation."
            )
        if args.preserve_hierarchy and result["succeeded"]:
            if result["emptyObjectCount"] != expected_empty_count:
                warnings.append(
                    f"{exchange_kind.upper()} round trip contains {result['emptyObjectCount']} Empty nodes versus {expected_empty_count} in the authored hierarchy."
                )
            if result["parentLinkCount"] != expected_parent_link_count:
                warnings.append(
                    f"{exchange_kind.upper()} round trip contains {result['parentLinkCount']} parent links versus {expected_parent_link_count} in the authored hierarchy."
                )

    generated_files = [packed_blend, fbx_path, glb_path]
    generated_files.extend(
        texture_dir / record["archivedPath"].split("/", 1)[-1]
        for record in image_records
        if record["archived"]
    )
    if derived_metallic_smoothness is not None:
        generated_files.append(output_dir / derived_metallic_smoothness["path"])

    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "harnessVersion": HARNESS_VERSION,
        "status": "inspected",
        "asset": {
            "id": args.asset_id,
            "sourceObject": args.source_object,
            "sourceKind": "external-ai-mesh-candidate",
            "approval": "candidate-only",
            "gameReady": False,
            "manualArtReviewRequired": True,
            "preserveHierarchy": args.preserve_hierarchy,
        },
        "source": {
            "blendPath": str(source_blend),
            "bytes": source_blend.stat().st_size,
            "sha256": sha256(source_blend),
        },
        "toolchain": {
            "blenderVersion": bpy.app.version_string,
            "pythonVersion": sys.version.split()[0],
            "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
            "sourceScriptSha256": sha256(Path(__file__).resolve()),
        },
        "coordinateContract": {
            "authoringUnits": "Blender scene units; dimensions reported without automatic normalization",
            "blenderUpAxis": "+Z",
            "fbxForwardAxis": "-Z",
            "fbxUpAxis": "+Y",
            "glbUpAxis": "+Y",
            "objectAxisBake": "disabled to preserve articulated local transforms"
            if args.preserve_hierarchy
            else "enabled for static mesh portability",
        },
        "hierarchy": {
            "preserved": args.preserve_hierarchy,
            "nodeCount": len(targets),
            "emptyObjectCount": expected_empty_count,
            "meshObjectCount": len(meshes),
            "parentLinkCount": expected_parent_link_count,
            "nodes": [
                {
                    "name": obj.name,
                    "type": obj.type,
                    "parent": obj.parent.name if obj.parent in targets else "",
                }
                for obj in targets
            ],
            "animationExported": False,
        },
        "geometry": {
            "meshObjectCount": len(meshes),
            "vertexCount": total_vertices,
            "triangleCount": total_triangles,
            "evaluatedVertexCount": total_evaluated_vertices,
            "evaluatedTriangleCount": total_evaluated_triangles,
            "worldBounds": bounds,
            "objects": mesh_reports,
        },
        "sanitization": {
            "policy": "Blender Mesh.validate on copied data blocks; source .blend is never overwritten",
            "objects": sanitization,
        },
        "materials": materials,
        "textures": image_records,
        "derivedTextures": {
            "urpMetallicSmoothness": derived_metallic_smoothness,
            "normalConvention": "Source normal is preserved; Unity import must verify/flip the green channel when required.",
        },
        "exports": {
            "packedBlend": packed_blend.name,
            "fbx": fbx_path.name,
            "glb": glb_path.name,
            "roundTrips": round_trips,
        },
        "acceptance": {
            "sourcePreserved": True,
            "allReferencedTexturesArchived": all_textures_archived,
            "packedBlendSaved": packed_blend.is_file() and packed_blend.stat().st_size > 0,
            "fbxExported": fbx_path.is_file() and fbx_path.stat().st_size > 0,
            "glbExported": glb_path.is_file() and glb_path.stat().st_size > 0,
            "fbxRoundTripRead": round_trips["fbx"]["succeeded"],
            "glbRoundTripRead": round_trips["glb"]["succeeded"],
            "fbxHierarchyMatchesSource": (
                not args.preserve_hierarchy
                or (
                    round_trips["fbx"]["succeeded"]
                    and round_trips["fbx"]["emptyObjectCount"] == expected_empty_count
                    and round_trips["fbx"]["parentLinkCount"] == expected_parent_link_count
                )
            ),
            "glbHierarchyMatchesSource": (
                not args.preserve_hierarchy
                or (
                    round_trips["glb"]["succeeded"]
                    and round_trips["glb"]["emptyObjectCount"] == expected_empty_count
                    and round_trips["glb"]["parentLinkCount"] == expected_parent_link_count
                )
            ),
            "sourceMeshValidationClean": not any(
                report["meshValidationWouldChange"] for report in mesh_reports
            ),
            "fbxTriangleCountMatchesValidatedSource": (
                round_trips["fbx"]["succeeded"]
                and round_trips["fbx"]["triangleCount"] == expected_validated_triangles
            ),
            "glbTriangleCountMatchesValidatedSource": (
                round_trips["glb"]["succeeded"]
                and round_trips["glb"]["triangleCount"] == expected_validated_triangles
            ),
            "fbxTriangleCountMatchesEvaluatedSource": (
                round_trips["fbx"]["succeeded"]
                and round_trips["fbx"]["triangleCount"] == expected_export_triangles
            ),
            "glbTriangleCountMatchesEvaluatedSource": (
                round_trips["glb"]["succeeded"]
                and round_trips["glb"]["triangleCount"] == expected_export_triangles
            ),
            "allMeshesHaveActiveUv": all_have_uv,
            "pbrCoreMapped": has_pbr_core,
            "urpMetallicSmoothnessGenerated": derived_metallic_smoothness is not None,
            "manualArtReviewStillRequired": True,
            "productionApproved": False,
        },
        "warnings": warnings,
        "files": [
            file_record(path, output_dir, kind=("texture" if path.parent == texture_dir else "artifact"))
            for path in dict.fromkeys(generated_files)
            if path.is_file()
        ],
    }
    manifest_path = output_dir / "intake-report.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(f"SSFRAMEWORK_AI_MESH_INTAKE_REPORT={manifest_path}")


if __name__ == "__main__":
    main()
