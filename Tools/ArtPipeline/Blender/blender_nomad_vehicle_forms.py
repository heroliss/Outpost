"""Bake reference-form shell and export a playable vehicle candidate, without overwriting NW1.
Blender --background --factory-startup --python-exit-code 1 --python this.py -- --output DIR
"""
import argparse
import hashlib
import importlib.util
import json
import math
import sys
from pathlib import Path

import bpy
import bmesh

STUDY_PATH = Path(__file__).with_name('blender_nomad_form_study.py')
SPEC = importlib.util.spec_from_file_location('nomad_study', STUDY_PATH)
STUDY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STUDY)
BASE = STUDY.BASE


def select(objects):
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objects: obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def bake(shell, out):
    select([shell])
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=1.15192,island_margin=.012)
    bpy.ops.object.mode_set(mode='OBJECT')
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 16
    scene.render.bake.margin = 12
    scene.render.bake.use_pass_direct = False
    scene.render.bake.use_pass_indirect = False
    scene.render.bake.use_pass_color = True
    materials = list(shell.data.materials)
    images = {}
    for channel in ('Color','Normal','Metallic','Smoothness'):
        image = bpy.data.images.new('NW5_Shell_'+channel,2048,2048,alpha=True)
        image.colorspace_settings.name = 'sRGB' if channel == 'Color' else 'Non-Color'
        images[channel] = image
        changes = []
        for mat in materials:
            nodes,links = mat.node_tree.nodes,mat.node_tree.links
            target = nodes.new('ShaderNodeTexImage')
            target.image = image
            nodes.active = target
            for node in nodes: node.select = node == target
            if channel in ('Metallic','Smoothness'):
                bsdf = nodes.get('Principled BSDF')
                output = next(n for n in nodes if n.type == 'OUTPUT_MATERIAL')
                original = output.inputs['Surface'].links[0].from_socket
                value = bsdf.inputs['Metallic'].default_value if channel == 'Metallic' else 1-bsdf.inputs['Roughness'].default_value
                emission = nodes.new('ShaderNodeEmission')
                emission.inputs['Color'].default_value = (value,value,value,1)
                links.new(emission.outputs[0],output.inputs['Surface'])
                changes.append((mat,output,original,emission))
        bpy.ops.object.bake(type='DIFFUSE' if channel == 'Color' else 'NORMAL' if channel == 'Normal' else 'EMIT',normal_space='TANGENT')
        for mat,output,original,emission in changes:
            mat.node_tree.links.new(original,output.inputs['Surface'])
            mat.node_tree.nodes.remove(emission)
        if channel in ('Color','Normal'):
            image.filepath_raw = str(out/('NW5_Shell_'+channel+'.png'))
            image.file_format = 'PNG'
            image.save()
        print('BAKED '+channel,flush=True)
    # URP Lit expects metallic in R and smoothness in A, both linear.
    import numpy as np
    metal = np.asarray(images['Metallic'].pixels[:],dtype=np.float32).reshape(-1,4)
    smooth = np.asarray(images['Smoothness'].pixels[:],dtype=np.float32).reshape(-1,4)
    pixels = np.zeros_like(metal)
    pixels[:,0] = metal[:,0]
    pixels[:,3] = smooth[:,0]
    packed = bpy.data.images.new('NW5_Shell_Surface',2048,2048,alpha=True)
    packed.colorspace_settings.name = 'Non-Color'
    packed.pixels.foreach_set(pixels.ravel())
    packed.filepath_raw = str(out/'NW5_Shell_Surface.png')
    packed.file_format = 'PNG'
    packed.save()
    atlas = bpy.data.materials.new('NW5_ShellAtlas')
    atlas.use_nodes = True
    nodes,links = atlas.node_tree.nodes,atlas.node_tree.links
    bsdf = nodes.get('Principled BSDF')
    color = nodes.new('ShaderNodeTexImage'); color.image = images['Color']
    links.new(color.outputs['Color'],bsdf.inputs['Base Color'])
    normal = nodes.new('ShaderNodeTexImage'); normal.image = images['Normal']
    convert = nodes.new('ShaderNodeNormalMap')
    links.new(normal.outputs['Color'],convert.inputs['Color'])
    links.new(convert.outputs['Normal'],bsdf.inputs['Normal'])
    surface = nodes.new('ShaderNodeTexImage'); surface.image = packed
    separate = nodes.new('ShaderNodeSeparateColor')
    links.new(surface.outputs['Color'],separate.inputs['Color'])
    links.new(separate.outputs['Red'],bsdf.inputs['Metallic'])
    invert = nodes.new('ShaderNodeMath'); invert.operation = 'SUBTRACT'; invert.inputs[0].default_value = 1
    links.new(surface.outputs['Alpha'],invert.inputs[1])
    links.new(invert.outputs[0],bsdf.inputs['Roughness'])
    shell.data.materials.clear(); shell.data.materials.append(atlas)
    for face in shell.data.polygons: face.material_index = 0


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--output',required=True)
    out = Path(parser.parse_args(sys.argv[sys.argv.index('--')+1:]).output).resolve()
    out.mkdir(parents=True,exist_ok=True)
    (out/'manifest.json').write_text('{"status":"running"}',encoding='utf-8')
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    bpy.context.scene.unit_settings.system = 'METRIC'
    for args in [('Hull',(.38,.37,.315),.48,.55),('Dark',(.072,.079,.077),.55,.63),
                 ('Steel',(.38,.39,.355),.72,.38),('Deck',(.185,.171,.142),.40,.72),
                 ('DeckPatch',(.215,.195,.158),.38,.70),('Teal',(.155,.225,.207),.27,.64),
                 ('Ochre',(.65,.365,.085),.20,.63)]: STUDY.material(*args)
    template = BASE.empty('shell-template')
    STUDY.edge_module(template)
    for obj in list(template.children_recursive):
        if obj.name.startswith(('Interlocking deck plate','Tie down socket','Recessed tie down ring')):
            bpy.data.objects.remove(obj,do_unlink=True)
    objects = [o for o in template.children_recursive if o.type == 'MESH']
    for obj in objects:
        select([obj])
        for mod in list(obj.modifiers):
            if mod.type != 'WEIGHTED_NORMAL': bpy.ops.object.modifier_apply(modifier=mod.name)
        # The 12 mm inset plates have meeting bevels. Weld coincident vertices before
        # UV/bake so their zero-area seam faces cannot enter the exported mesh.
        bm = bmesh.new(); bm.from_mesh(obj.data)
        bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
        bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=.000001)
        bm.to_mesh(obj.data); bm.free()
        for mod in list(obj.modifiers): bpy.ops.object.modifier_apply(modifier=mod.name)
    select(objects); bpy.ops.object.join()
    shell = bpy.context.object; shell.name = 'Baked shell module'
    bake(shell,out)
    shell.parent = None
    bpy.data.objects.remove(template,do_unlink=True)
    shell.hide_set(True)
    for name,(color,metal,rough) in BASE.PALETTE.items():
        mat = bpy.data.materials.new('NW1_'+name); mat.use_nodes = True
        bsdf = mat.node_tree.nodes.get('Principled BSDF')
        bsdf.inputs['Base Color'].default_value = (*color,1)
        bsdf.inputs['Metallic'].default_value = metal
        bsdf.inputs['Roughness'].default_value = rough
        BASE.MATS[name] = mat
    vehicle = BASE.empty('NW5_Vehicle')
    BASE.vehicle(vehicle)
    for obj in list(vehicle.children_recursive):
        if obj.name.startswith(('Rear locker','Locker pull')): bpy.data.objects.remove(obj,do_unlink=True)
    BASE.prepare_uv_and_merge(vehicle)
    # Atlas UVs are retained after the legacy metre/material UV preparation.
    # The right-side apron is the real offboard route. Keep that opening; front
    # shell stays left of the driver. Above-deck shell vertices must stay outside.
    mounts = [(-2.10,-.10,-4.60,0),(2.10,-.10,-4.60,0),(-5.80,-.10,0,90),(-1.50,-.10,4.60,180)]
    for i,(x,y,z,yaw) in enumerate(mounts):
        mount = BASE.empty('NW5_ShellMount_'+str(i),vehicle,(x,y,z))
        mount.rotation_euler.z = -math.radians(yaw)
        obj = shell.copy(); obj.data = shell.data
        bpy.context.collection.objects.link(obj); obj.parent = mount; obj.hide_set(False)
        obj.name = 'NW5_ShellPanel_'+str(i)
    bpy.context.view_layer.update()
    for obj in vehicle.children_recursive:
        if not obj.name.startswith('NW5_ShellPanel_'): continue
        for vertex in obj.data.vertices:
            p = obj.matrix_world @ vertex.co
            assert p.z <= .05 or abs(p.x) >= 5.4 or abs(p.y) >= 4.2,('shell-in-playable-deck',list(p))
    bpy.data.objects.remove(shell,do_unlink=True)
    for axis,p in [('Right',(1,0,0)),('Up',(0,1,0)),('Forward',(0,0,1))]: BASE.empty('NW5_Axis'+axis,vehicle,p)
    objects = [vehicle]+list(vehicle.children_recursive)
    source = BASE.stats(objects)
    assert source['degenerateTriangles'] == 0 and source['uvComplete'],('invalid-geometry',source['degenerateTriangles'],source['uvComplete'])
    select(objects)
    fbx = out/'NW5_Vehicle.fbx'
    bpy.ops.export_scene.fbx(filepath=str(fbx),use_selection=True,object_types={'EMPTY','MESH'},
        apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',axis_forward='-Z',axis_up='Y',
        use_space_transform=True,bake_space_transform=False,add_leaf_bones=False,bake_anim=False,
        use_mesh_modifiers=True,mesh_smooth_type='FACE',path_mode='AUTO',use_custom_props=True)
    before = set(bpy.data.objects); bpy.ops.import_scene.fbx(filepath=str(fbx))
    imported = set(bpy.data.objects)-before; readback = BASE.stats(imported)
    assert source['triangles'] == readback['triangles'] and readback['uvComplete']
    for key in ('minimumBlender','maximumBlender'):
        assert all(abs(a-b)<.002 for a,b in zip(source[key],readback[key])),(key,source,readback)
    assert source['pivots'].keys() == readback['pivots'].keys()
    imported_shells = [o for o in imported if o.name.startswith('NW5_ShellPanel_')]
    assert len(imported_shells) == 4
    # Same-process readback keeps the source atlas alive; Blender assigns .001 to
    # the imported material. A fresh import must use the exact un-suffixed name.
    assert all(len(o.data.materials) == 1 and o.data.materials[0].name in ('NW5_ShellAtlas','NW5_ShellAtlas.001')
               and all(p.material_index == 0 for p in o.data.polygons) for o in imported_shells)
    for key,position in source['pivots'].items():
        assert all(abs(a-b)<.002 for a,b in zip(position,readback['pivots'][key])),key
    for obj in imported: bpy.data.objects.remove(obj,do_unlink=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(out/'NW5_Vehicle.blend'))
    paths = [Path(__file__),STUDY_PATH,STUDY.BASE_PATH]
    files = [fbx]+[out/('NW5_Shell_'+channel+'.png') for channel in ('Color','Normal','Surface')]
    report = {'version':'0.1.0','status':'passed-export','sources':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in paths],
              'files':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files],
              'source':source,'roundTrip':readback,'shellMounts':mounts,'atlasSize':2048,
              'normalConvention':'OpenGL +Y tangent','surfaceChannels':'R metallic, A smoothness; linear',
              'unityValidated':False,'manualVisualReviewRequired':True}
    (out/'manifest.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print('EXPORT_PASSED '+str(source['triangles']),flush=True)


if __name__ == '__main__': main()
