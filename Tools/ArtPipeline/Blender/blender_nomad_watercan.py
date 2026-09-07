"""Editable carried water container, with independent cap, fluid indicator and semantic markers.

Blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python this.py -- --output DIR
Only writes the supplied output directory; --preview-only skips baking and FBX export.
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
SPEC = importlib.util.spec_from_file_location('nomad_container_helpers', WATER_PATH)
WATER = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(WATER)
HELPER, STUDY, BASE = WATER.HELPER, WATER.STUDY, WATER.BASE
VERSION, STEM = '0.1.1', 'NW11_WaterCan'


def rounded_section(width, depth, radius):
    points = []
    for cx, cz, start in [(width/2-radius, depth/2-radius, 0),
                          (-width/2+radius, depth/2-radius, 90),
                          (-width/2+radius, -depth/2+radius, 180),
                          (width/2-radius, -depth/2+radius, 270)]:
        for step in range(6):
            angle = math.radians(start + step*18)
            points.append((cx + radius*math.cos(angle), cz + radius*math.sin(angle)))
    return points


def shell(root):
    sections = [(0, .263, .154, .032), (.012, .282, .172, .037),
                (.04, .29, .18, .04), (.285, .29, .18, .04),
                (.323, .276, .164, .036), (.365, .231, .131, .03),
                (.374, .224, .124, .029)]
    vertices = [(x,y,z) for y,w,d,r in sections for x,z in rounded_section(w,d,r)]
    n = 24
    faces = [tuple(reversed(range(n))), tuple(range((len(sections)-1)*n,len(sections)*n))]
    for level in range(len(sections)-1):
        for i in range(n):
            j = (i+1)%n
            faces.append((level*n+i,level*n+j,(level+1)*n+j,(level+1)*n+i))
    body = STUDY.mesh('Formed shoulder shell', vertices, faces, 'CanPaint', root, .002)
    # This opening is cut through the shoulder, rather than painting a black disc over a solid top.
    cutter = BASE.cylinder('Temporary neck aperture',(.075,.38,.026),.026,.09,'Dark',root,sides=32)
    HELPER.prepare_meshes([body,cutter]); HELPER.select([body])
    modifier = body.modifiers.new('Real fill aperture','BOOLEAN'); modifier.operation='DIFFERENCE'; modifier.object=cutter
    bpy.ops.object.modifier_apply(modifier=modifier.name); bpy.data.objects.remove(cutter,do_unlink=True)
    for polygon in body.data.polygons: polygon.use_smooth=True
    modifier=body.modifiers.new('Formed shell normals','WEIGHTED_NORMAL'); modifier.keep_sharp=True
    BASE.cylinder('Recessed mouth interior',(.075,.34,.026),.025,.003,'Dark',root,sides=32)
    seam=[(x,y,z) for y,w,d in [(.014,.283,.173),(.021,.287,.177),(.030,.287,.177)]
          for x,z in rounded_section(w,d,.038)]
    seam_faces=[(level*24+i,level*24+(i+1)%24,(level+1)*24+(i+1)%24,(level+1)*24+i)
                for level in range(2) for i in range(24)]
    STUDY.mesh('Rolled base seam',seam,seam_faces,'Inset',root,.001)
    # Shallow formed side fields and two restrained ribs retain readable broad surfaces.
    for side in (-1,1):
        panel = STUDY.plate('Pressed side field',0,.174,side*.083,.21,.235,.004,.032,'Inset',root)
        for x in (-.061,.061):
            BASE.box('Pressed reinforcement rib',(x,.172,side*.087),(.014,.162,.007),'CanPaint',root,.003)
    BASE.box('Label backing',(0,.257,-.090),(.106,.047,.006),'Label',root,.007)
    font = bpy.data.curves.new('Eight litre stencil','FONT'); font.body='8 L'; font.align_x='CENTER'
    font.size=.031; font.extrude=.0002
    text=bpy.data.objects.new(font.name,font); bpy.context.collection.objects.link(text)
    text.parent=root; text.location=BASE.point((0,.247,-.094)); text.rotation_euler.x=math.pi/2
    HELPER.select([text]); bpy.ops.object.convert(target='MESH'); text=bpy.context.object
    text.data.materials.append(BASE.MATS['Dark'])


def ring(root, name, center, outer, inner, height, material):
    n=32; x,y,z=center
    vertices=[(x+r*math.cos(i*math.tau/n), yy, z+r*math.sin(i*math.tau/n))
              for yy,r in [(y,outer),(y+height,outer),(y+height,inner),(y,inner)] for i in range(n)]
    faces=[]
    for layer in range(4):
        for i in range(n):
            j=(i+1)%n; other=(layer+1)%4
            faces.append((layer*n+i,layer*n+j,other*n+j,other*n+i))
    return STUDY.mesh(name,vertices,faces,material,root,.001)


def handle(root):
    points=[(-.086,.349,-.032),(-.086,.463,-.032)]
    for step in range(1,7):
        a=math.pi-step*math.pi/12
        points.append((-.059+.027*math.cos(a),.463+.027*math.sin(a),-.032))
    points.append((.059,.49,-.032))
    for step in range(1,7):
        a=math.pi/2-step*math.pi/12
        points.append((.059+.027*math.cos(a),.463+.027*math.sin(a),-.032))
    points.append((.086,.349,-.032))
    curve=bpy.data.curves.new('Rounded carry handle','CURVE'); curve.dimensions='3D'
    curve.bevel_depth=.022; curve.bevel_resolution=3; curve.use_fill_caps=True
    spline=curve.splines.new('POLY'); spline.points.add(len(points)-1)
    for p,co in zip(spline.points,points): p.co=(*BASE.point(co),1)
    obj=bpy.data.objects.new(curve.name,curve); bpy.context.collection.objects.link(obj)
    HELPER.select([obj]); bpy.ops.object.convert(target='MESH')
    BASE.finish(bpy.context.object,'Rounded carry handle','Grip',root)
    for x in (-.086,.086):
        BASE.box('Handle mounting foot',(x,.356,-.032),(.063,.024,.062),'CanPaint',root,.007)


def build(root):
    shell(root); handle(root)
    ring(root,'Threaded neck',(.075,.369,.026),.034,.026,.029,'Steel')
    cap=BASE.empty(STEM+'_Cap',root,(.075,.398,.026))
    BASE.cylinder('Separate screw cap',(0,.011,0),.04,.022,'Ochre',cap,sides=32)
    ring(cap,'Cap sealing skirt',(0,.003,0),.041,.035,.012,'Grip')
    for i in range(8):
        a=i*math.tau/8
        rib=BASE.box('Cap grip fluting',(.038*math.cos(a),.013,.038*math.sin(a)),(.010,.018,.009),'Ochre',cap,.002)
        rib.rotation_euler.z=-a
    fill=BASE.empty(STEM+'_Fill',root)
    BASE.box('Protected level window',(.129,.185,-.057),(.009,.15,.015),'Dark',root,.002)
    BASE.box('Visible water level',(.132,.16,-.06),(.009,.093,.012),'Water',fill,.002)
    BASE.empty(STEM+'_CarryPivot',root,(0,.49,-.032))
    BASE.empty(STEM+'_RightPalm',root,(0,.512,-.032))
    BASE.empty(STEM+'_Opening',root,(.075,.398,.026))
    BASE.empty(STEM+'_LowerClearance',root,(0,.164,0))
    BASE.empty(STEM+'_ShoulderClearance',root,(0,.351,0))
    for axis,p in [('Right',(1,0,0)),('Up',(0,1,0)),('Forward',(0,0,1))]: BASE.empty(STEM+'_Axis'+axis,root,p)


def bake_parts(root,out):
    meshes=[o for o in root.children_recursive if o.type=='MESH']; HELPER.prepare_meshes(meshes)
    owners=[]
    for obj in meshes:
        if obj.parent not in owners: owners.append(obj.parent)
        group=obj.vertex_groups.new(name='owner_'+str(owners.index(obj.parent)))
        group.add(list(range(len(obj.data.vertices))),1,'REPLACE')
    HELPER.select(meshes); bpy.ops.object.join(); joined=bpy.context.object
    HELPER.bake(joined,out,STEM)
    before=set(bpy.data.objects); HELPER.select([joined]); bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT'); bpy.ops.mesh.separate(type='LOOSE'); bpy.ops.object.mode_set(mode='OBJECT')
    groups={i:[] for i in range(len(owners))}
    for obj in list(set(bpy.data.objects)-before)+[joined]:
        tags={obj.vertex_groups[g.group].name for v in obj.data.vertices for g in v.groups if g.weight>.5}
        assert len(tags)==1,(obj.name,tags)
        groups[int(next(iter(tags)).split('_')[1])].append(obj)
    for index,objects in groups.items():
        HELPER.select(objects)
        if len(objects)>1: bpy.ops.object.join()
        obj=bpy.context.object; matrix=obj.matrix_world.copy(); obj.parent=owners[index]; obj.matrix_world=matrix
        obj.name=owners[index].name+'_Mesh'; obj.vertex_groups.clear()


def preview(root,out):
    scene=bpy.context.scene; scene.render.engine='CYCLES'; scene.cycles.samples=32; scene.cycles.use_denoising=True
    STUDY.material('PreviewFloor',(.24,.22,.185),0,.9)
    BASE.box('Studio ground',(0,-.027,0),(200,.05,200),'PreviewFloor',None,0)
    world=bpy.data.worlds.new('Warm container studio'); scene.world=world; world.use_nodes=True
    world.node_tree.nodes.get('Background').inputs['Color'].default_value=(.48,.51,.54,1)
    world.node_tree.nodes.get('Background').inputs['Strength'].default_value=.4
    for name,p,power,size,color in [('Key',(-1.8,2.5,-2),180,2,(1,.88,.74)),('Fill',(1.7,1,-.5),90,2,(.80,.89,1))]:
        light=bpy.data.lights.new(name,'AREA'); light.energy=power; light.shape='DISK'; light.size=size; light.color=color
        obj=bpy.data.objects.new(name,light); scene.collection.objects.link(obj); obj.location=BASE.point(p)
        obj.rotation_euler=(BASE.point((0,.25,0))-obj.location).to_track_quat('-Z','Y').to_euler()
    data=bpy.data.cameras.new('Container overview'); camera=bpy.data.objects.new(data.name,data); scene.collection.objects.link(camera)
    camera.location=BASE.point((.9,.8,-1.3)); camera.rotation_euler=(BASE.point((0,.25,0))-camera.location).to_track_quat('-Z','Y').to_euler()
    data.type='ORTHO'; data.ortho_scale=.67; scene.camera=camera
    scene.render.resolution_x=1100; scene.render.resolution_y=1300; scene.render.resolution_percentage=100
    scene.view_settings.view_transform='AgX'; scene.render.image_settings.file_format='PNG'
    scene.render.filepath=str(out/'watercan-preview.png')
    bpy.ops.wm.save_as_mainfile(filepath=str(out/(STEM+'.blend'))); bpy.ops.render.render(write_still=True)


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--output',required=True); parser.add_argument('--preview-only',action='store_true')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:]); out=Path(args.output).resolve(); out.mkdir(parents=True,exist_ok=True)
    (out/'manifest.json').write_text(json.dumps({'version':VERSION,'status':'running'}),encoding='utf-8')
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    bpy.context.scene.unit_settings.system='METRIC'; bpy.context.scene.render.threads_mode='FIXED'; bpy.context.scene.render.threads=8
    for name,color,metal,rough in [('CanPaint',(.18,.32,.285),.18,.64),('Inset',(.155,.275,.245),.14,.68),
        ('Grip',(.085,.105,.10),.03,.78),('Dark',(.035,.045,.042),0,.82),('Steel',(.38,.40,.35),.7,.38),
        ('Ochre',(.48,.27,.10),.1,.61),('Label',(.66,.61,.47),0,.78),('Water',(.10,.43,.52),.1,.24)]:
        mat=STUDY.material(name,color,metal,rough)
        for node in mat.node_tree.nodes:
            if node.type=='BUMP': node.inputs['Strength'].default_value=.09; node.inputs['Distance'].default_value=.0007
    mat=BASE.MATS['CanPaint']; nodes,links=mat.node_tree.nodes,mat.node_tree.links
    bsdf=nodes.get('Principled BSDF'); original=bsdf.inputs['Base Color'].links[0].from_socket
    coords=nodes.new('ShaderNodeTexCoord'); xyz=nodes.new('ShaderNodeSeparateXYZ'); links.new(coords.outputs['Object'],xyz.inputs[0])
    height=nodes.new('ShaderNodeMapRange'); height.inputs['From Min'].default_value=.012; height.inputs['From Max'].default_value=.11
    height.inputs['To Min'].default_value=.32; height.inputs['To Max'].default_value=0; links.new(xyz.outputs['Z'],height.inputs[0])
    mix=nodes.new('ShaderNodeMixRGB'); links.new(height.outputs[0],mix.inputs[0]); links.new(original,mix.inputs[1])
    mix.inputs[2].default_value=(.23,.19,.12,1); links.new(mix.outputs[0],bsdf.inputs['Base Color'])
    root=BASE.empty(STEM); build(root)
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
        'files':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files],
        'binding':{'carryPivot':STEM+'_CarryPivot','rightPalm':STEM+'_RightPalm','opening':STEM+'_Opening',
                   'closure':STEM+'_Cap','fillIndicator':STEM+'_Fill','groundReachOffset':[0,-.62,.20],
                   'clearances':[{'marker':STEM+'_LowerClearance','size':[.29,.328,.20]},
                                 {'marker':STEM+'_ShoulderClearance','size':[.27,.046,.16]}]}}
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8'); print('WATERCAN_READY '+manifest['status'],flush=True)


if __name__=='__main__': main()
