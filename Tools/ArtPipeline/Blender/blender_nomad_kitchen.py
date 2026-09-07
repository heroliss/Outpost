"""Warm compact kitchen: editable shell, real countertop, sink and separate cabinet doors.

Blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python this.py -- --output DIR
Writes only the supplied output directory. --preview-only skips atlas/export validation.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import sys
from pathlib import Path

import bpy

WATER_PATH = Path(__file__).with_name('blender_nomad_water_facilities.py')
SPEC = importlib.util.spec_from_file_location('nomad_water_recipe', WATER_PATH)
WATER = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(WATER)
HELPER, STUDY, BASE = WATER.HELPER, WATER.STUDY, WATER.BASE
VERSION = '0.1.1'
STEM = 'NW9_Kitchen'


def materials():
    WATER.materials()
    wood = STUDY.material('Wood', (.31, .17, .075), 0, .63)
    nodes, links = wood.node_tree.nodes, wood.node_tree.links
    tex = nodes.new('ShaderNodeTexCoord'); scale = nodes.new('ShaderNodeVectorMath')
    scale.operation = 'MULTIPLY'; scale.inputs[1].default_value = (2, 32, 5)
    links.new(tex.outputs['Object'], scale.inputs[0])
    grain = nodes.new('ShaderNodeTexNoise'); grain.inputs['Scale'].default_value = 6
    grain.inputs['Detail'].default_value = 2; links.new(scale.outputs[0], grain.inputs['Vector'])
    ramp = nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].color = (.16, .068, .026, 1)
    ramp.color_ramp.elements[1].color = (.37, .22, .105, 1)
    links.new(grain.outputs['Fac'], ramp.inputs[0]); links.new(ramp.outputs[0], nodes.get('Principled BSDF').inputs['Base Color'])
    STUDY.material('Cloth', (.40, .31, .19), 0, .9)
    STUDY.material('Jar', (.25, .20, .12), .1, .42)
    STUDY.material('Enamel', (.48, .45, .34), .18, .35)
    for mat in BASE.MATS.values():
        if not mat.use_nodes: continue
        for node in mat.node_tree.nodes:
            if node.type == 'BUMP':
                node.inputs['Strength'].default_value = .08
                node.inputs['Distance'].default_value = .002


def sink_bowl(root):
    # A formed open basin, with a narrowing section and separate visible drain.
    sections = [(.958, .51, .48), (.936, .485, .455), (.84, .37, .34), (.825, .34, .31)]
    vertices = [(x, y, z) for y, w, d in sections for x, z in STUDY.octagon(-.65, -.04, w, d, .065)]
    faces = [(i+j, i+(j+1)%8, i+8+(j+1)%8, i+8+j) for i in range(0,24,8) for j in range(8)]
    faces.append(tuple(range(24,32)))
    STUDY.mesh('Pressed open sink bowl', vertices, faces, 'Steel', root, 0)
    top = BASE.box('Sink surround',(-.65,.947,0),(.73,.025,.84),'Steel',root,.008)
    cut = BASE.box('Temporary basin cut',(-.65,.94,-.04),(.495,.35,.465),'Dark',root,.065)
    HELPER.prepare_meshes([top,cut]); HELPER.select([top])
    boolean = top.modifiers.new('Open basin aperture','BOOLEAN'); boolean.operation='DIFFERENCE'; boolean.object=cut
    bpy.ops.object.modifier_apply(modifier=boolean.name); bpy.data.objects.remove(cut,do_unlink=True)
    BASE.cylinder('Sink drain',(-.65,.831,-.04),.028,.004,'Dark',root,sides=20)
    for x in (-.668,-.65,-.632): BASE.box('Drain slot',(x,.834,-.04),(.005,.003,.03),'Steel',root,.001)
    BASE.pipe('Bent faucet',[(-.64,.96,.31),(-.64,1.24,.31),(-.64,1.28,.23),(-.64,1.24,.10)],.016,'Steel',root)
    BASE.cylinder('Faucet collar',(-.64,.975,.31),.032,.035,'Dark',root,sides=20)
    BASE.pipe('Tap lever',[(-.48,.965,.29),(-.48,1.05,.29),(-.55,1.07,.29)],.012,'Ochre',root)


def door(root, name, x, width, hinge_side):
    pivot = BASE.empty(STEM+'_Door_'+name,root,(x+hinge_side*width/2,.51,-.42))
    center = -hinge_side*width/2
    STUDY.plate('Pressed cabinet door',center,0,0,width,.64,.023,.035,'Teal',pivot)
    STUDY.plate('Door recessed field',center,-.015,-.005,width-.075,.53,.009,.025,'Inset',pivot)
    hx = center-hinge_side*(width/2-.07)
    # The cubic AUTO handles of the general pipe helper overshoot this narrow pull.
    # A straight-segment swept grip stays inside the actual declared footprint.
    curve=bpy.data.curves.new('Compact door pull','CURVE'); curve.dimensions='3D'
    curve.bevel_depth=.009; curve.bevel_resolution=2
    spline=curve.splines.new('POLY'); spline.points.add(5)
    for point,position in zip(spline.points,[(hx,-.075,-.012),(hx,-.075,-.024),(hx,-.065,-.032),
        (hx,.075,-.032),(hx,.085,-.024),(hx,.085,-.012)]): point.co=(*BASE.point(position),1)
    grip=bpy.data.objects.new('Compact door pull',curve); bpy.context.collection.objects.link(grip)
    HELPER.select([grip]); bpy.ops.object.convert(target='MESH'); BASE.finish(bpy.context.object,'Compact door pull','Steel',pivot)
    for y in (-.22,.22): BASE.cylinder('Separate hinge barrel',(0,y,.005),.014,.09,'Steel',pivot,sides=12)
    return pivot


def kitchen(root):
    # Shell is made of folded walls and a real interior. Door opening reveals shelves.
    for x in (-.99,.99):
        STUDY.extrude_profile('Folded cabinet side',x-.023,x+.023,
            [(.13,-.43),(.90,-.43),(.935,-.35),(.935,.43),(.14,.43)],'Hull',root,.006)
        for z in (-.34,.34):
            BASE.box('Foot shoe',(x,.065,z),(.105,.13,.10),'Dark',root,.01)
    BASE.box('Recessed cabinet base',(0,.14,.01),(1.94,.045,.80),'Inset',root,.009)
    BASE.box('Cabinet back',(0,.55,.415),(1.94,.80,.025),'Hull',root,.009)
    for x in (-.33,.33): BASE.box('Internal partition',(x,.535,.01),(.025,.75,.78),'Hull',root,.007)
    for x in (-.655,0,.655): BASE.box('Interior shelf',(x,.46,.02),(.60,.021,.74),'Steel',root,.006)
    BASE.box('Front upper rail',(0,.875,-.397),(1.98,.13,.055),'Hull',root,.01)
    BASE.box('Toe kick',(0,.18,-.37),(1.92,.09,.06),'Dark',root,.007)
    left_door=door(root,'Left',-.66,.59,-1)
    door(root,'Center',0,.61,-1); door(root,'Right',.66,.59,1)
    # The entire advertised center support remains flat at exactly 0.97 m.
    top = BASE.empty(STEM+'_Countertop',root,(0,.97,-.08))
    BASE.box('Solid wooden preparation board',(0,-.0225,.08),(.56,.045,.86),'Wood',top,.01)
    BASE.empty(STEM+'_CookPoint',root,(.65,1.005,.11))
    sink_bowl(root)
    BASE.box('Stove enamel top',(.65,.948,0),(.73,.026,.84),'Enamel',root,.009)
    # Two rings and a shallow pot are legible from the game camera without clutter.
    for z in (-.19,.19):
        BASE.cylinder('Burner bowl',(.65,.968,z),.137,.016,'Dark',root,sides=28)
        WATER.torus('Burner ring',(.65,.983,z),.115,.012,'Steel',root)
        for a in range(4):
            angle=a*math.pi/2
            BASE.pipe('Pan support',[(.65+math.cos(angle)*.06,.997,z+math.sin(angle)*.06),
                (.65+math.cos(angle)*.15,.997,z+math.sin(angle)*.15)],.011,'Dark',root)
    pot = BASE.empty(STEM+'_Pot',root,(.65,1.005,.19))
    WATER.lathe('Cooking pot',(0,0,0),[(0,.085),(.02,.11),(.15,.115),(.155,.105),(.025,.098),(0,.085)],'Steel',pot,32,False)
    lid=BASE.empty(STEM+'_PotLid',pot,(0,.155,0))
    WATER.lathe('Domed pot lid',(0,0,0),[(0,.118),(.012,.118),(.035,.065),(.038,0)],'Steel',lid,32)
    BASE.pipe('Lid grip',[(-.04,.04,0),(-.04,.07,0),(.04,.07,0),(.04,.04,0)],.01,'Dark',lid)
    for side in (-1,1):
        BASE.pipe('Pot side handle',[(side*.10,.11,-.045),(side*.16,.11,-.045),(side*.16,.11,.045),(side*.10,.11,.045)],.011,'Dark',pot)
    for x in (.50,.79):
        BASE.cylinder('Stove control escutcheon',(x,.875,-.436),.032,.013,'Dark',root,'z',20)
        BASE.cylinder('Stove dial',(x,.875,-.45),.023,.025,'Ochre',root,'z',16)
        BASE.box('Dial index',(x,.888,-.464),(.004,.012,.003),'Enamel',root,0)
    # Low backsplash and open rack create a useful silhouette; only a few supplies.
    BASE.box('Back splash',(0,1.12,.433),(1.99,.34,.025),'Hull',root,.008)
    for x in (-.94,.94):
        STUDY.extrude_profile('Rack folded upright',x-.022,x+.022,
            [(.90,.405),(1.65,.405),(1.65,.455),(.90,.455)],'Dark',root,.004)
    BASE.box('Upper open shelf',(0,1.50,.30),(1.94,.025,.30),'Wood',root,.007)
    BASE.pipe('Shelf retaining rail',[(-.97,1.59,.155),(.97,1.59,.155)],.009,'Steel',root)
    for x in (-.94,0,.94): BASE.box('Rail spacer',(x,1.55,.155),(.014,.09,.014),'Steel',root,.002)
    for i,x in enumerate((-.77,-.57,.57,.77)):
        radius=.055 if i%2 else .06; height=.12 if i%2 else .15
        WATER.lathe('Pantry jar',(x,1.514,.30),[(0,radius*.9),(.018,radius),(height,radius),
            (height+.015,radius*.80),(height+.015,0)],'Jar' if i%2 else 'Enamel',root,20)
        BASE.cylinder('Jar cap',(x,1.524+height,.30),radius*.87,.022,'Ochre',root,sides=20)
    BASE.box('Task lamp housing',(0,1.478,.20),(.44,.025,.06),'Dark',root,.004)
    lamp=BASE.empty(STEM+'_TaskLamp',root,(0,1.465,.19))
    BASE.box('Warm task lens',(0,0,0),(.39,.009,.034),'Lamp',lamp,.002)
    BASE.pipe('Utensil rail',[(-.37,1.31,.402),(.35,1.31,.402)],.009,'Steel',root)
    for i,x in enumerate((-.25,-.12,.20)):
        BASE.pipe('Utensil handle',[(x,1.32,.38),(x,1.29,.35),(x,1.13,.35)],.008,'Wood' if i==2 else 'Steel',root)
        STUDY.plate('Utensil head',x,1.09,.342,.043 if i!=1 else .065,.065,.008,.016,'Steel',root)
    # A draped towel is a shaped strip with thickness, outside the central placement area.
    vertices=[]
    for x in (-.88,-.69):
        vertices.extend((x,y,z) for y,z in [(.905,-.407),(.906,-.455),(.87,-.47),(.61,-.472),(.60,-.465)])
    cloth=STUDY.mesh('Hanging tea towel',vertices,[(i,i+1,i+6,i+5) for i in range(4)],'Cloth',root,0)
    solid=cloth.modifiers.new('Cloth thickness','SOLIDIFY'); solid.thickness=.003
    bpy.context.view_layer.update(); matrix=cloth.matrix_world.copy(); cloth.parent=left_door; cloth.matrix_world=matrix


def bake_parts(root,out):
    meshes=[o for o in root.children_recursive if o.type=='MESH']; HELPER.prepare_meshes(meshes)
    opaque=[o for o in meshes if o.data.materials[0].name!='NW1_Lamp']; parents=[]
    for obj in opaque:
        if obj.parent not in parents: parents.append(obj.parent)
        group=obj.vertex_groups.new(name='part_'+str(parents.index(obj.parent)))
        group.add(list(range(len(obj.data.vertices))),1,'REPLACE')
    HELPER.select(opaque); bpy.ops.object.join(); joined=bpy.context.object
    HELPER.bake(joined,out,STEM)
    before=set(bpy.data.objects); HELPER.select([joined]); bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT'); bpy.ops.mesh.separate(type='LOOSE'); bpy.ops.object.mode_set(mode='OBJECT')
    grouped={i:[] for i in range(len(parents))}
    for obj in list(set(bpy.data.objects)-before)+[joined]:
        tags={obj.vertex_groups[g.group].name for v in obj.data.vertices for g in v.groups if g.weight>.5}
        assert len(tags)==1,(obj.name,tags)
        grouped[int(next(iter(tags)).split('_')[1])].append(obj)
    for index,objects in grouped.items():
        HELPER.select(objects)
        if len(objects)>1: bpy.ops.object.join()
        obj=bpy.context.object; matrix=obj.matrix_world.copy(); obj.parent=parents[index]; obj.matrix_world=matrix
        obj.name=parents[index].name+'_Mesh'; obj.vertex_groups.clear()
    lamp=next(o for o in root.children_recursive if o.type=='MESH' and o.data.materials[0].name=='NW1_Lamp')
    HELPER.select([lamp]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(island_margin=.01); bpy.ops.object.mode_set(mode='OBJECT')


def preview(root,out):
    scene=bpy.context.scene; scene.render.engine='CYCLES'; scene.cycles.samples=32; scene.cycles.use_denoising=True
    STUDY.material('PreviewFloor',(.20,.19,.16),.05,.85)
    BASE.box('Preview floor',(0,-.055,0),(200,.10,200),'PreviewFloor',None,0)
    world=bpy.data.worlds.new('Warm workshop studio'); scene.world=world; world.use_nodes=True
    world.node_tree.nodes.get('Background').inputs['Color'].default_value=(.48,.51,.54,1)
    world.node_tree.nodes.get('Background').inputs['Strength'].default_value=.4
    for name,p,power,size,color in [('Key',(-3,5,-4),700,5,(1,.86,.68)),('Fill',(4,3,-1),350,4,(.79,.89,1))]:
        data=bpy.data.lights.new(name,'AREA'); data.energy=power; data.shape='DISK'; data.size=size; data.color=color
        obj=bpy.data.objects.new(name,data); scene.collection.objects.link(obj); obj.location=BASE.point(p)
        obj.rotation_euler=(BASE.point((0,.8,0))-obj.location).to_track_quat('-Z','Y').to_euler()
    data=bpy.data.cameras.new('Kitchen overview'); camera=bpy.data.objects.new(data.name,data); scene.collection.objects.link(camera)
    camera.location=BASE.point((2.8,2.5,-4.7)); camera.rotation_euler=(BASE.point((0,.85,0))-camera.location).to_track_quat('-Z','Y').to_euler()
    data.type='ORTHO'; data.ortho_scale=2.9; scene.camera=camera
    scene.render.resolution_x=1500; scene.render.resolution_y=1250; scene.render.resolution_percentage=100
    scene.view_settings.view_transform='AgX'; scene.render.image_settings.file_format='PNG'
    scene.render.filepath=str(out/'kitchen-preview.png')
    bpy.ops.wm.save_as_mainfile(filepath=str(out/(STEM+'.blend'))); bpy.ops.render.render(write_still=True)


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--output',required=True); parser.add_argument('--preview-only',action='store_true')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:]); out=Path(args.output).resolve(); out.mkdir(parents=True,exist_ok=True)
    (out/'manifest.json').write_text(json.dumps({'version':VERSION,'status':'running'}),encoding='utf-8')
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    bpy.context.scene.unit_settings.system='METRIC'; bpy.context.scene.render.threads_mode='FIXED'; bpy.context.scene.render.threads=8
    materials(); root=BASE.empty(STEM); kitchen(root)
    for axis,p in [('Right',(1,0,0)),('Up',(0,1,0)),('Forward',(0,0,1))]: BASE.empty(STEM+'_Axis'+axis,root,p)
    bpy.ops.wm.save_as_mainfile(filepath=str(out/(STEM+'_Source.blend')))
    records=[]
    if not args.preview_only:
        bake_parts(root,out); records=[WATER.export(root,out)]
    else: HELPER.prepare_meshes([o for o in root.children_recursive if o.type=='MESH'])
    preview(root,out)
    sources=[Path(__file__),WATER_PATH,WATER.HELPER_PATH,HELPER.STUDY_PATH,STUDY.BASE_PATH,HELPER.DETAIL_PATH,HELPER.CANOPY_PATH]
    files=[p for p in out.iterdir() if p.suffix in ('.fbx','.png')]
    manifest={'version':VERSION,'status':'preview-only' if args.preview_only else 'passed-export','blenderVersion':bpy.app.version_string,
        'atlasSize':2048,'assets':records,'sources':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sources],
        'files':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files]}
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8'); print('KITCHEN_READY '+manifest['status'],flush=True)


if __name__=='__main__': main()
