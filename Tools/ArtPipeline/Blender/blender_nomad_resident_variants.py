"""Curated offline residents; shared outfit rigs and independently tintable cloth.

This is a fixed, versioned Quaternius recipe, not arbitrary garment fitting.
Exports neutral game assets before arranging the actual Blender preview.
"""
import argparse
import hashlib
import json
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Matrix, Vector


def arguments():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output-dir', required=True)
    parser.add_argument('--outfit-dir', required=True)
    parser.add_argument('--base-dir', required=True)
    parser.add_argument('--recipe', default=str(Path(__file__).with_name('nomad_resident_variants.json')))
    return parser.parse_args(sys.argv[sys.argv.index('--') + 1:])


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def import_fbx(path):
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=str(path))
    objects = set(bpy.context.scene.objects) - before
    rigs = [obj for obj in objects if obj.type == 'ARMATURE']
    if len(rigs) != 1:
        raise RuntimeError(f'{path.name}: expected one armature')
    return rigs[0], [obj for obj in objects if obj.type == 'MESH']


def attach(rig, source_rig, meshes):
    # Only the head/neck and single-head-bone hairstyles are attached this way.
    # Clothing retains its own rig; matching bone names alone cannot prove fitting.
    for bone_name in ('Head', 'neck_01'):
        target = rig.matrix_world @ rig.data.bones[bone_name].head_local
        source = source_rig.matrix_world @ source_rig.data.bones[bone_name].head_local
        if (target - source).length > .002:
            raise RuntimeError(f'Incompatible head attachment: {bone_name}')
    for obj in meshes:
        world = obj.matrix_world.copy()
        obj.parent = rig
        obj.matrix_world = world
        for modifier in obj.modifiers:
            if modifier.type == 'ARMATURE':
                modifier.object = rig
    bpy.data.objects.remove(source_rig, do_unlink=True)


def cut_vertices(obj, predicate):
    mesh = bmesh.new()
    try:
        mesh.from_mesh(obj.data)
        selected = [vertex for vertex in mesh.verts if predicate(obj.matrix_world @ vertex.co)]
        if not selected:
            raise RuntimeError(f'{obj.name}: expected covered geometry')
        bmesh.ops.delete(mesh, geom=selected, context='VERTS')
        mesh.to_mesh(obj.data)
    finally:
        mesh.free()


def material(name, color, texture=None, normal=None):
    existing = bpy.data.materials.get(name)
    if existing is not None:
        return existing
    result = bpy.data.materials.new(name)
    result.diffuse_color = (*color, 1)
    result.use_nodes = True
    nodes, links = result.node_tree.nodes, result.node_tree.links
    shader = nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = (*color, 1)
    shader.inputs['Roughness'].default_value = .8
    if texture:
        image = nodes.new('ShaderNodeTexImage')
        image.image = bpy.data.images.load(str(texture), check_existing=True)
        tint = nodes.new('ShaderNodeMixRGB')
        tint.blend_type = 'MULTIPLY'
        tint.inputs[0].default_value = 1
        tint.inputs[2].default_value = (*color, 1)
        links.new(image.outputs['Color'], tint.inputs[1])
        links.new(tint.outputs['Color'], shader.inputs['Base Color'])
    if normal:
        image = nodes.new('ShaderNodeTexImage')
        image.image = bpy.data.images.load(str(normal), check_existing=True)
        image.image.colorspace_settings.name = 'Non-Color'
        mapped = nodes.new('ShaderNodeNormalMap')
        mapped.inputs['Strength'].default_value = .6
        links.new(image.outputs['Color'], mapped.inputs['Color'])
        links.new(mapped.outputs['Normal'], shader.inputs['Normal'])
    return result


def cloth_islands(obj, cloth):
    """Assign main fabric independently from the belt/metal in the shared atlas."""
    neighbors = {v.index: set() for v in obj.data.vertices}
    for edge in obj.data.edges:
        a, b = edge.vertices
        neighbors[a].add(b)
        neighbors[b].add(a)
    components, unseen = [], set(neighbors)
    while unseen:
        todo = [min(unseen)]
        found = set()
        while todo:
            index = todo.pop()
            if index in found:
                continue
            found.add(index)
            unseen.discard(index)
            todo.extend(neighbors[index] - found)
        components.append(found)
    main = max(components, key=len)
    obj.data.materials.append(cloth)
    for polygon in obj.data.polygons:
        if all(index in main for index in polygon.vertices):
            polygon.material_index = len(obj.data.materials) - 1
    # Tiny metallic upper-arm badges are ornamental; remove those separate islands
    # from this known male shirt, leaving the main shell, waist belt and buckle.
    badges = {i for c in components if len(c) < 50 and
              all((obj.matrix_world @ obj.data.vertices[v].co).z > 1.28 and
                  abs((obj.matrix_world @ obj.data.vertices[v].co).x) > .12 for v in c) for i in c}
    if badges:
        mesh = bmesh.new()
        try:
            mesh.from_mesh(obj.data)
            mesh.verts.ensure_lookup_table()
            bmesh.ops.delete(mesh, geom=[mesh.verts[i] for i in badges], context='VERTS')
            mesh.to_mesh(obj.data)
        finally:
            mesh.free()


def assemble(config, outfit_dir, base_dir, output):
    gender, identity = config['body'], config['id']
    inputs = [outfit_dir / f'{gender}_Peasant.fbx', base_dir / f'Superhero_{gender}_FullBody.fbx']
    inputs += [base_dir / (hair + '.fbx') for hair in config['hair']]
    rig, meshes = import_fbx(inputs[0])
    rig.name = 'WorkshopSkeleton'
    if gender == 'Male':
        cut_vertices(next(obj for obj in meshes if obj.name.endswith('_Arms')), lambda v: abs(v.x) < .34)
    head_rig, head_meshes = import_fbx(inputs[1])
    head = next(obj for obj in head_meshes if obj.name.lower().startswith('superhero'))
    if gender == 'Male':
        cut_vertices(head, lambda v: v.z < 1.525 or (v.z < 1.6 and abs(v.x) > .11))
    else:
        # This lower vest neckline needs the upper chest below the neck. Cutting
        # at the male head boundary leaves visibly jagged holes above its collar.
        cut_vertices(head, lambda v: v.z < 1.4 or (v.z < 1.56 and abs(v.x) > .09))
    head.name = 'ResidentHead'
    attach(rig, head_rig, head_meshes)
    meshes += head_meshes
    for path in inputs[2:]:
        hair_rig, hair_meshes = import_fbx(path)
        attach(rig, hair_rig, hair_meshes)
        meshes += hair_meshes

    atlas = outfit_dir / 'T_Peasant_BaseColor.png'
    atlas_normal = outfit_dir / 'T_Peasant_Normal.png'
    skin_name = 'T_Superhero_Male_Ligh.png' if gender == 'Male' else 'T_Superhero_Female_Light_BaseColor.png'
    skin_normal = base_dir / f'T_Superhero_{gender}_Normal.png'
    inputs += [atlas, atlas_normal, base_dir / skin_name, skin_normal, base_dir / 'T_Eye_Brown.png']
    shared = material('NW3_Outfit', (1, 1, 1), atlas, atlas_normal)
    shirt = material(f'NW3_Shirt_{identity}', config['shirtColor'], atlas, atlas_normal)
    skin = material(f'NW3_Skin_{gender}', (1, 1, 1), base_dir / skin_name, skin_normal)
    eyes = material('NW3_Eyes', (1, 1, 1), base_dir / 'T_Eye_Brown.png')
    hair = material(f'NW3_Hair_{identity}', config['hairColor'])
    for obj in meshes:
        original_names = [m.name for m in obj.data.materials]
        for index, name in enumerate(original_names):
            obj.data.materials[index] = shared if name.startswith('MI_Peasant') else eyes if name.startswith('MI_Eyes') else hair if name.startswith('MI_Hair') else skin
        if obj.name.endswith('_Body'):
            cloth_islands(obj, shirt)
        for polygon in obj.data.polygons:
            polygon.use_smooth = True

    rig.scale *= config['scale']
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action='DESELECT')
    for obj in [rig] + meshes:
        obj.select_set(True)
    destination = output / f'NW3_{identity}.fbx'
    bpy.ops.export_scene.fbx(filepath=str(destination), use_selection=True, add_leaf_bones=False,
                            bake_anim=False, axis_forward='-Z', axis_up='Y', use_mesh_modifiers=True)
    report = {'id': identity, 'recipe': config, 'fbx': destination.name, 'sha256': digest(destination),
              'inputs': [{'file': str(p), 'sha256': digest(p)} for p in inputs],
              'meshes': [{'name': o.name, 'triangles': sum(len(p.vertices) - 2 for p in o.data.polygons)} for o in meshes]}
    return rig, report


def pose(rig):
    for side in ('l', 'r'):
        bone = rig.pose.bones['upperarm_' + side]
        direction = Vector((.23 if side == 'l' else -.23, -.03, -.95)).normalized()
        rotation = (bone.tail - bone.head).normalized().rotation_difference(direction).to_matrix().to_4x4()
        bone.matrix = Matrix.Translation(bone.head) @ rotation @ Matrix.Translation(-bone.head) @ bone.matrix


def preview(output):
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 24
    scene.render.resolution_x = 1600
    scene.render.resolution_y = 1100
    scene.render.resolution_percentage = 100
    scene.world = bpy.data.worlds.new('PreviewWorld')
    scene.world.color = (.28, .3, .34)
    scene.view_settings.view_transform = 'AgX'
    bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, -.014))
    bpy.context.object.data.materials.append(material('PreviewSand', (.2, .17, .14)))
    for location, energy, size, color in [((3, 4, 6), 1000, 5, (1, .86, .7)), ((-4, 1, 4), 600, 4, (.8, .87, 1)), ((0, -3, 4), 500, 3, (1, .92, .8))]:
        bpy.ops.object.light_add(type='AREA', location=location)
        light = bpy.context.object
        light.data.energy, light.data.size, light.data.color = energy, size, color
        light.rotation_euler = (Vector((0, 0, 1)) - light.location).to_track_quat('-Z', 'Y').to_euler()
    bpy.ops.object.camera_add(location=(2.4, 6.5, 2.8))
    camera = bpy.context.object
    camera.data.type = 'ORTHO'
    camera.data.ortho_scale = 3.7
    scene.camera = camera
    for name, position in [('front', (2.4, 6.5, 2.8)), ('overhead', (2.4, 5.2, 7))]:
        camera.location = position
        camera.rotation_euler = (Vector((0, 0, .9)) - camera.location).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = str(output / ('residents-' + name + '.png'))
        bpy.ops.render.render(write_still=True)


def main():
    args = arguments()
    output = Path(args.output_dir).resolve()
    output.mkdir(parents=True, exist_ok=True)
    recipe_path = Path(args.recipe).resolve()
    recipe = json.loads(recipe_path.read_text(encoding='utf-8-sig'))
    reports, rigs = [], []
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0
    for config in recipe['variants']:
        rig, report = assemble(config, Path(args.outfit_dir).resolve(), Path(args.base_dir).resolve(), output)
        rigs.append(rig)
        reports.append(report)
        # Names remain canonical inside each exported FBX; prefix only the retained
        # preview objects so the next import does not suffix Eyes/Body names.
        for obj in [rig] + list(rig.children):
            obj.name = config['id'] + '_' + obj.name
    # All FBX exports already contain neutral local models. Only the preview gets
    # spacing/poses; it must never become the game export source by accident.
    for index, rig in enumerate(rigs):
        pose(rig)
        rig.location.x += (index - 1) * 1.05
    bpy.context.view_layer.update()
    bpy.ops.wm.save_as_mainfile(filepath=str(output / 'resident-lineup.blend'))
    (output / 'manifest.json').write_text(json.dumps({'recipeVersion': recipe['recipeVersion'], 'generatorSha256': digest(Path(__file__)), 'recipeSha256': digest(recipe_path), 'blenderVersion': bpy.app.version_string, 'variants': reports}, indent=2), encoding='utf-8')
    preview(output)
    print('RESIDENT_VARIANTS_EXPORTED_AND_RENDERED')


if __name__ == '__main__':
    main()
