"""Compact dry toilet: hollow service body, oval seat, hinged lid and pleated flax screens.

Writes only --output. Shared Blender helpers do not modify existing source or Unity assets.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import sys
from pathlib import Path

import bpy

KITCHEN_PATH = Path(__file__).with_name('blender_nomad_kitchen.py')
SPEC = importlib.util.spec_from_file_location('sanitation_bake_recipe', KITCHEN_PATH)
KITCHEN = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(KITCHEN)
WATER, HELPER, STUDY, BASE = KITCHEN.WATER, KITCHEN.HELPER, KITCHEN.STUDY, KITCHEN.BASE
VERSION, STEM = '0.1.0', 'NW14_Toilet'


def materials():
    KITCHEN.materials()
    flax = STUDY.material('Flax', (.46, .365, .25), 0, .90)
    nodes, links = flax.node_tree.nodes, flax.node_tree.links
    tex = nodes.new('ShaderNodeTexCoord')
    weave = nodes.new('ShaderNodeTexNoise'); weave.inputs['Scale'].default_value = 290
    weave.inputs['Detail'].default_value = 1
    links.new(tex.outputs['Object'], weave.inputs['Vector'])
    bump = nodes.new('ShaderNodeBump'); bump.inputs['Strength'].default_value = .065
    bump.inputs['Distance'].default_value = .001
    links.new(weave.outputs['Fac'], bump.inputs['Height'])
    links.new(bump.outputs['Normal'], nodes.get('Principled BSDF').inputs['Normal'])
    STUDY.material('Paper', (.66, .635, .55), 0, .93)
    STUDY.material('Seat', (.55, .53, .43), .03, .34)


def oval(name, profile, center, material, parent, segments=48):
    """Closed radial cross-section, including its inner wall; a real opening, not a dark disc."""
    vertices = [(center[0]+rx*math.cos(i*math.tau/segments), center[1]+y,
                 center[2]+rz*math.sin(i*math.tau/segments))
                for rx, rz, y in profile for i in range(segments)]
    faces = []
    for row in range(len(profile)):
        next_row = (row+1) % len(profile)
        for i in range(segments):
            j = (i+1) % segments
            faces.append((row*segments+i,row*segments+j,next_row*segments+j,next_row*segments+i))
    obj = STUDY.mesh(name, vertices, faces, material, parent, 0)
    for polygon in obj.data.polygons: polygon.use_smooth = True
    return obj


def fine_pipe(name, points, radius, material, parent):
    """Keep an editable low-resolution curve until the explicit mesh conversion boundary."""
    curve=bpy.data.curves.new(name,'CURVE'); curve.dimensions='3D'; curve.resolution_u=2
    curve.bevel_depth=radius; curve.bevel_resolution=1
    spline=curve.splines.new('BEZIER'); spline.bezier_points.add(len(points)-1)
    for bp,p in zip(spline.bezier_points,points):
        bp.co=BASE.point(p); bp.handle_left_type=bp.handle_right_type='AUTO'
    obj=bpy.data.objects.new(name,curve); bpy.context.collection.objects.link(obj)
    obj.parent=parent; curve.materials.append(BASE.MATS[material]); return obj


def panel(name, side, parent):
    """Sewn side screens have actual folds and a gently hanging lower hem."""
    columns, rows = 40, 3
    vertices = []
    for j in range(rows+1):
        v=j/rows
        for i in range(columns+1):
            u=i/columns
            z=-.275+u*.65
            x=side*(.36+.010*math.sin(u*math.tau*5.5)*(0.75+v*.25))
            y=.77+v*.95-.022*(1-v)*math.sin(math.pi*u)**2
            vertices.append((x,y,z))
    faces = [(j*(columns+1)+i,j*(columns+1)+i+1,(j+1)*(columns+1)+i+1,(j+1)*(columns+1)+i)
             for j in range(rows) for i in range(columns)]
    obj=STUDY.mesh(name,vertices,faces,'Flax',parent,0)
    solid=obj.modifiers.new('Sewn fabric thickness','SOLIDIFY'); solid.thickness=.003
    for polygon in obj.data.polygons: polygon.use_smooth=True
    for i in range(6):
        z=-.23+i*.104
        fine_pipe('Screen hanging loop',[(side*.356,1.712,z),(side*.350,1.802,z),
                  (side*.369,1.825,z),(side*.387,1.8,z),(side*.383,1.717,z)],.0035,'Steel',parent)
    fine_pipe('Weighted lower hem',[(side*(.36+.010*math.sin(i/40*math.tau*5.5)*.75),
              .77-.022*math.sin(math.pi*i/40)**2,-.275+i/40*.65) for i in range(41)],.003,'Flax',parent)


def toilet(root):
    body=BASE.empty(STEM+'_ServiceBody',root)
    seat=BASE.empty(STEM+'_Seat',root)
    screens=BASE.empty(STEM+'_PrivacyScreens',root)
    # The existing removable bucket starts 20 mm above the facility origin.
    # Keep the tray below that plane; raised perimeter folds carry the side shell.
    BASE.box('Floor tray',(0,.009,0),(.76,.018,.87),'Teal',body,.005)
    for side in (-1,1):
        BASE.box('Tray side fold',(side*.362,.026,0),(.022,.04,.82),'Teal',body,.004)
        x0,x1=sorted((side*.375,side*.347))
        STUDY.extrude_profile('Folded side shell',x0,x1,
            [(.045,-.405),(.09,-.422),(.52,-.38),(.603,-.29),(.603,.36),(.045,.41)],'Hull',body,.008)
        BASE.box('Front service jamb',(side*.255,.275,-.366),(.06,.46,.045),'Teal',body,.012)
        BASE.box('Rubber mounting shoe',(side*.29,.014,.31),(.13,.025,.13),'Rubber',body,.01)
        BASE.box('Front mounting shoe',(side*.29,.014,-.31),(.13,.025,.13),'Rubber',body,.01)
        for z in (-.31,.31):
            BASE.cylinder('Tray bolt',(side*.327,.054,z),.014,.01,'Steel',body,sides=12)
    BASE.box('Rear service wall',(0,.27,.386),(.71,.47,.036),'Teal',body,.012)
    BASE.box('Service lintel',(0,.521,-.337),(.67,.055,.07),'Teal',body,.012)
    # Bowl and seat preserve the empty centre all the way to the removable waste container.
    oval('Moulded bowl shell',[(.32,.37,.52),(.335,.38,.57),(.319,.37,.625),
        (.235,.283,.628),(.18,.219,.566),(.166,.191,.505),(.192,.222,.504),(.249,.302,.55)],
        (0,0,-.002),'Seat',seat)
    oval('Rounded oval seat',[(.302,.344,.632),(.307,.35,.65),(.299,.34,.679),
        (.216,.255,.68),(.203,.243,.66),(.211,.252,.634)],(0,0,-.018),'Seat',seat)
    oval('Seat soft seal',[(.304,.346,.625),(.304,.346,.633),(.21,.25,.633),(.21,.25,.625)],
        (0,0,-.018),'Rubber',seat)
    lid=BASE.empty(STEM+'_SeatLid',root,(0,.666,.304))
    rings=[(.295,.335,.001),(.306,.346,.010),(.306,.346,.022),(.294,.334,.031)]
    vertices=[(rx*math.cos(i*math.tau/48),y,-.29+rz*math.sin(i*math.tau/48)) for rx,rz,y in rings for i in range(48)]
    faces=[tuple(reversed(range(48))),tuple(range(144,192))]
    faces += [(row*48+i,row*48+(i+1)%48,(row+1)*48+(i+1)%48,(row+1)*48+i) for row in range(3) for i in range(48)]
    cover=STUDY.mesh('Pressed oval lid',vertices,faces,'Teal',lid,0)
    for polygon in cover.data.polygons: polygon.use_smooth = len(polygon.vertices)==4
    lid.rotation_euler.x=math.radians(-83)
    for x in (-.17,.17):
        BASE.cylinder('Seat hinge',(x,.671,.30),.025,.085,'Steel',body,axis='x',sides=20)
    for side in (-1,1):
        points=[(side*.369,.055,.35),(side*.369,1.74,.35)]
        points += [(side*.369,1.74+.05*math.sin(i*math.pi/16),.30+.05*math.cos(i*math.pi/16)) for i in range(1,9)]
        points += [(side*.369,1.79,-.245)]
        points += [(side*.369,1.74+.05*math.sin(math.pi/2+i*math.pi/16),-.245+.05*math.cos(math.pi/2+i*math.pi/16)) for i in range(1,9)]
        curve=bpy.data.curves.new('Bent privacy frame','CURVE'); curve.dimensions='3D'
        curve.bevel_depth=.014; curve.bevel_resolution=2
        spline=curve.splines.new('POLY'); spline.points.add(len(points)-1)
        for vertex,p in zip(spline.points,points): vertex.co=(*BASE.point(p),1)
        obj=bpy.data.objects.new('Bent privacy frame',curve); bpy.context.collection.objects.link(obj)
        obj.parent=screens; obj.data.materials.append(BASE.MATS['Dark'])
        panel('Pleated side canvas',side,screens)
    fine_pipe('Rear top rail',[(-.369,1.75,.353),(.369,1.75,.353)],.013,'Dark',screens)
    # Back wall is also sewn cloth, with a broad soft sag rather than a rigid rectangle.
    vertices=[]
    for j in range(4):
        v=j/3
        for i in range(25):
            u=i/24
            vertices.append((-.345+u*.69,.78+.95*v-.025*math.sin(u*math.pi)*(1-v),
                             .358+.008*math.sin(u*math.tau*4)))
    obj=STUDY.mesh('Rear pleated canvas',vertices,[(j*25+i,j*25+i+1,(j+1)*25+i+1,(j+1)*25+i)
        for j in range(3) for i in range(24)],'Flax',screens,0)
    solid=obj.modifiers.new('Rear fabric thickness','SOLIDIFY'); solid.thickness=.003
    for p in obj.data.polygons: p.use_smooth=True
    details=BASE.empty(STEM+'_DomesticDetails',root)
    BASE.box('Small wooden shelf',(0,1.325,.278),(.56,.035,.15),'Wood',details,.009)
    BASE.cylinder('Charcoal jar',(.195,1.395,.278),.042,.10,'Enamel',details,sides=20)
    BASE.cylinder('Jar lid',(.195,1.448,.278),.046,.014,'Wood',details,sides=20)
    fine_pipe('Paper holder',[(-.329,.97,-.22),(-.26,.97,-.22),(-.26,.97,-.13)],.008,'Steel',details)
    BASE.cylinder('Paper roll',(-.25,.956,-.18),.045,.11,'Paper',details,axis='z',sides=24)
    paper=STUDY.mesh('Loose paper tail',[(-.268,.927,-.23),(-.268,.927,-.13),
        (-.263,.829,-.13),(-.263,.829,-.23)],[(0,1,2,3)],'Paper',details,0)
    solid=paper.modifiers.new('Paper thickness','SOLIDIFY'); solid.thickness=.001
    BASE.box('Task lamp housing',(0,1.624,.276),(.23,.032,.075),'Dark',details,.006)
    lamp=BASE.empty(STEM+'_TaskLamp',root,(0,1.603,.259))
    BASE.box('Warm task lens',(0,0,0),(.19,.012,.049),'Lamp',lamp,.003)
    BASE.empty(STEM+'_BucketEnvelope',root,(0,.262,-.082))


def preview(root,out):
    scene=bpy.context.scene; scene.render.engine='CYCLES'; scene.cycles.samples=32; scene.cycles.use_denoising=True
    STUDY.material('PreviewFloor',(.20,.19,.16),0,.85)
    BASE.box('Preview floor',(0,-.055,0),(200,.1,200),'PreviewFloor',None,0)
    world=bpy.data.worlds.new('Warm canvas studio'); scene.world=world; world.use_nodes=True
    world.node_tree.nodes.get('Background').inputs['Color'].default_value=(.48,.51,.54,1)
    world.node_tree.nodes.get('Background').inputs['Strength'].default_value=.4
    for name,p,power,size,color in [('Key',(-3,4,-4),650,4,(1,.86,.7)),('Fill',(3,3,-1),320,3,(.82,.9,1))]:
        data=bpy.data.lights.new(name,'AREA'); data.energy=power; data.shape='DISK'; data.size=size; data.color=color
        obj=bpy.data.objects.new(name,data); scene.collection.objects.link(obj); obj.location=BASE.point(p)
        obj.rotation_euler=(BASE.point((0,.9,0))-obj.location).to_track_quat('-Z','Y').to_euler()
    data=bpy.data.cameras.new('Sanitation overview'); camera=bpy.data.objects.new(data.name,data); scene.collection.objects.link(camera)
    camera.location=BASE.point((2.3,2.6,-4)); camera.rotation_euler=(BASE.point((0,.9,0))-camera.location).to_track_quat('-Z','Y').to_euler()
    data.type='ORTHO'; data.ortho_scale=2.35; scene.camera=camera
    scene.render.resolution_x=1200; scene.render.resolution_y=1400; scene.render.resolution_percentage=100
    scene.view_settings.view_transform='AgX'; scene.render.image_settings.file_format='PNG'
    scene.render.filepath=str(out/'sanitation-preview.png')
    bpy.ops.wm.save_as_mainfile(filepath=str(out/(STEM+'.blend'))); bpy.ops.render.render(write_still=True)


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--output',required=True); parser.add_argument('--preview-only',action='store_true')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:]); out=Path(args.output).resolve(); out.mkdir(parents=True,exist_ok=True)
    (out/'manifest.json').write_text(json.dumps({'version':VERSION,'status':'running'}),encoding='utf-8')
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    scene=bpy.context.scene; scene.unit_settings.system='METRIC'; scene.render.threads_mode='FIXED'; scene.render.threads=8
    materials(); root=BASE.empty(STEM); toilet(root)
    # Fine stitched hems and hooks are centimetre details in the game view, not high-poly tubing.
    for obj in root.children_recursive:
        if obj.type=='CURVE': obj.data.resolution_u=2; obj.data.bevel_resolution=1
    for axis,p in [('Right',(1,0,0)),('Up',(0,1,0)),('Forward',(0,0,1))]: BASE.empty(STEM+'_Axis'+axis,root,p)
    bpy.ops.wm.save_as_mainfile(filepath=str(out/(STEM+'_Source.blend')))
    curves=[o for o in root.children_recursive if o.type=='CURVE']
    if curves:
        HELPER.select(curves); bpy.ops.object.convert(target='MESH')
    assert not any(o.type=='CURVE' for o in root.children_recursive), 'unconverted-curve-would-be-lost-in-FBX'
    records=[]
    if args.preview_only: HELPER.prepare_meshes([o for o in root.children_recursive if o.type=='MESH'])
    else:
        # The imported recipe module is private to this Blender process. Its atlas partitioner
        # retains owning pivots and UVs; selecting its stem does not alter any recipe on disk.
        KITCHEN.STEM=STEM; KITCHEN.bake_parts(root,out); records=[WATER.export(root,out)]
    preview(root,out)
    sources=[Path(__file__),KITCHEN_PATH,KITCHEN.WATER_PATH,WATER.HELPER_PATH,HELPER.STUDY_PATH,
             STUDY.BASE_PATH,HELPER.DETAIL_PATH,HELPER.CANOPY_PATH]
    files=[p for p in out.iterdir() if p.suffix in ('.fbx','.png')]
    manifest={'version':VERSION,'status':'preview-only' if args.preview_only else 'passed-export',
        'blenderVersion':bpy.app.version_string,'atlasSize':2048,'assets':records,
        'sources':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sources],
        'files':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files]}
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')


if __name__=='__main__': main()
