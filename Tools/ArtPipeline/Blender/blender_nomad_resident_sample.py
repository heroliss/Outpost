"""Offline visual intake only: preserve outfit armature and attach a compatible head."""
import bpy,bmesh,json,math,argparse,sys,hashlib
from pathlib import Path
from mathutils import Vector,Matrix
parser=argparse.ArgumentParser()
parser.add_argument('--output-dir',required=True)
parser.add_argument('--outfit-dir',required=True)
parser.add_argument('--body-dir',required=True)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
root=Path(args.output_dir).resolve();root.mkdir(parents=True,exist_ok=True)
bodydir=Path(args.body_dir).resolve()
outfitdir=Path(args.outfit_dir).resolve()
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.preferences.filepaths.save_version=0
bpy.ops.import_scene.fbx(filepath=str(outfitdir/'Male_Peasant.fbx'))
rig=next(o for o in bpy.context.scene.objects if o.type=='ARMATURE')
rig.name='WorkshopSkeleton'
# Peasant's inner leather sleeves extend underneath the short-sleeved shirt.
# Hide the covered shoulder section in this asset recipe: the two layers deform
# differently at raised elbows, so extra hidden geometry pierces the outer cloth.
arms=bpy.data.objects['Male_Peasant_Arms']
mesh=bmesh.new();mesh.from_mesh(arms.data)
covered=[v for v in mesh.verts if abs((arms.matrix_world@v.co).x)<.34]
if not covered: raise RuntimeError('Expected covered inner sleeve geometry was not found.')
bmesh.ops.delete(mesh,geom=covered,context='VERTS')
mesh.to_mesh(arms.data);mesh.free()
before=set(bpy.context.scene.objects)
bpy.ops.import_scene.fbx(filepath=str(bodydir/'Superhero_Male_FullBody.fbx'))
added=set(bpy.context.scene.objects)-before
bodyrig=next(o for o in added if o.type=='ARMATURE')
for obj in added:
    if obj.type!='MESH': continue
    if obj.name.startswith('SuperHero'):
        mesh=bmesh.new();mesh.from_mesh(obj.data)
        remove=[v for v in mesh.verts if (obj.matrix_world@v.co).z<1.525 or ((obj.matrix_world@v.co).z<1.60 and abs((obj.matrix_world@v.co).x)>.11)]
        bmesh.ops.delete(mesh,geom=remove,context='VERTS')
        mesh.to_mesh(obj.data);mesh.free();obj.name='ResidentHead'
    world=obj.matrix_world.copy();obj.parent=rig;obj.matrix_world=world
    for mod in obj.modifiers:
        if mod.type=='ARMATURE': mod.object=rig
bpy.data.objects.remove(bodyrig,do_unlink=True)
# Check the optional real cloth hood independently of the character pipeline.
before=set(bpy.context.scene.objects)
bpy.ops.import_scene.fbx(filepath=str(outfitdir/'Male_Ranger_Head_Hood.fbx'))
added=set(bpy.context.scene.objects)-before
hoodrig=next(o for o in added if o.type=='ARMATURE')
for obj in added:
    if obj.type!='MESH':continue
    world=obj.matrix_world.copy();obj.parent=rig;obj.matrix_world=world
    for mod in obj.modifiers:
        if mod.type=='ARMATURE':mod.object=rig
bpy.data.objects.remove(hoodrig,do_unlink=True)
def mat(name,color,tex=None,normal=None):
    m=bpy.data.materials.get(name)
    if not m:return
    m.use_nodes=True;n=m.node_tree.nodes;l=m.node_tree.links
    n.clear();p=n.new('ShaderNodeBsdfPrincipled');out=n.new('ShaderNodeOutputMaterial');l.new(p.outputs['BSDF'],out.inputs['Surface'])
    p.inputs['Base Color'].default_value=(*color,1);p.inputs['Roughness'].default_value=.75
    if tex:
        t=n.new('ShaderNodeTexImage');t.image=bpy.data.images.load(str(tex),check_existing=True);l.new(t.outputs['Color'],p.inputs['Base Color'])
    if normal:
        t=n.new('ShaderNodeTexImage');t.image=bpy.data.images.load(str(normal),check_existing=True);t.image.colorspace_settings.name='Non-Color'
        nm=n.new('ShaderNodeNormalMap');nm.inputs['Strength'].default_value=.6;l.new(t.outputs['Color'],nm.inputs['Color']);l.new(nm.outputs['Normal'],p.inputs['Normal'])
mat('MI_Peasant',(.5,.45,.3),outfitdir/'T_Peasant_BaseColor.png',outfitdir/'T_Peasant_Normal.png')
mat('MI_Ranger',(.45,.42,.3),outfitdir/'T_Ranger_BaseColor.png',outfitdir/'T_Ranger_Normal.png')
mat('MI_Superhero_Male',(.65,.42,.25),bodydir/'T_Superhero_Male_Ligh.png',bodydir/'T_Superhero_Male_Normal.png')
mat('MI_Regular_Male',(.65,.42,.25),bodydir/'T_Superhero_Male_Ligh.png')
mat('MI_Eyes',(.6,.5,.4),bodydir/'T_Eye_Brown.png')
mat('MI_Hair_1',(.085,.045,.022))
for obj in bpy.context.scene.objects:
    if obj.type=='MESH':
        for p in obj.data.polygons:p.use_smooth=True
# Save neutral assembly for repeatable asset inspection before presentation posing.
bpy.ops.wm.save_as_mainfile(filepath=str(root/'peasant-head-assembly.blend'))
bpy.ops.object.select_all(action='DESELECT')
for o in bpy.context.scene.objects:
    if o.type in ('MESH','ARMATURE'):o.select_set(True)
bpy.ops.export_scene.fbx(filepath=str(root/'PeasantResidentPreview.fbx'),use_selection=True,add_leaf_bones=False,bake_anim=False,axis_forward='-Z',axis_up='Y',use_mesh_modifiers=True)
# Preserve source and geometry evidence independently of the rendered pose.
inputs=[outfitdir/'Male_Peasant.fbx',outfitdir/'Male_Ranger_Head_Hood.fbx',bodydir/'Superhero_Male_FullBody.fbx']
manifest={
    'status':'exported-awaiting-visual-review',
    'blenderVersion':bpy.app.version_string,
    'generatorSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    'inputs':[{'file':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in inputs],
    'meshes':[{'name':o.name,'vertices':len(o.data.vertices),'triangles':sum(len(f.vertices)-2 for f in o.data.polygons)} for o in bpy.context.scene.objects if o.type=='MESH'],
    'fbxSha256':hashlib.sha256((root/'PeasantResidentPreview.fbx').read_bytes()).hexdigest()
}
(root/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
# Pose upper arms in armature space; don't bake pose into source geometry.
for side in ('l','r'):
    b=rig.pose.bones['upperarm_'+side]
    m=b.matrix.copy();direction=Vector((.23 if side=='l' else -.23,-.03,-.95)).normalized()
    rotation=(b.tail-b.head).normalized().rotation_difference(direction)
    rot=rotation.to_matrix().to_4x4();target=Matrix.Translation(b.head)@rot@Matrix.Translation(-b.head)@m
    b.matrix=target
bpy.context.view_layer.update()
scene=bpy.context.scene;scene.render.engine='CYCLES';scene.cycles.samples=32
scene.render.resolution_x=1000;scene.render.resolution_y=1100;scene.render.resolution_percentage=100
scene.world=bpy.data.worlds.new('IntakeWorld')
scene.world.color=(.28,.30,.34)
scene.view_settings.view_transform='AgX'
bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,-.012));ground=bpy.context.object
m=bpy.data.materials.new('PreviewSand');m.use_nodes=True;m.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=(.20,.16,.12,1);ground.data.materials.append(m)
for location,energy,size,color in [((2,-3,5),650,4,(1,.85,.67)),((-3,-1,3),400,3,(.75,.85,1)),((0,3,4),500,3,(1,.92,.8))]:
    bpy.ops.object.light_add(type='AREA',location=location);light=bpy.context.object;light.data.energy=energy;light.data.shape='DISK';light.data.size=size;light.data.color=color;light.rotation_euler=(Vector((0,0,1))-light.location).to_track_quat('-Z','Y').to_euler()
bpy.ops.object.camera_add(location=(2.4,4.2,2.4));camera=bpy.context.object;camera.data.type='ORTHO';camera.data.ortho_scale=2.45;camera.rotation_euler=(Vector((0,0,.95))-camera.location).to_track_quat('-Z','Y').to_euler();scene.camera=camera
scene.render.filepath=str(root/'peasant-preview-front-02.png');bpy.ops.render.render(write_still=True)
camera.location=(2.8,4,5.5);camera.rotation_euler=(Vector((0,0,.75))-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.ortho_scale=2.65
scene.render.filepath=str(root/'peasant-preview-overhead-02.png');bpy.ops.render.render(write_still=True)
print('PREVIEW_COMPLETE')
