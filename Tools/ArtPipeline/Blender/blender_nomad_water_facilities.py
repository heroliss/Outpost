"""Reference J water facility pair: editable forms, shared baked atlas, separate work pivots.

Blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python this.py -- --output DIR
Use --preview-only to inspect shapes before baking/export. Never edits project Assets or user preferences.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

HELPER_PATH = Path(__file__).with_name('blender_nomad_vehicle_forms.py')
SPEC = importlib.util.spec_from_file_location('nomad_water_bake_helpers', HELPER_PATH)
HELPER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPER)
STUDY, BASE = HELPER.STUDY, HELPER.BASE
VERSION = '0.1.1'


def materials():
    palette = [
        ('Teal', (.19, .31, .29), .24, .55), ('Hull', (.46, .46, .40), .35, .59),
        ('Inset', (.31, .345, .31), .32, .65), ('Dark', (.065, .085, .082), .45, .60),
        ('Steel', (.36, .39, .365), .78, .37), ('Ochre', (.58, .30, .085), .25, .58),
        ('Rubber', (.035, .045, .043), 0, .83), ('Ceramic', (.62, .60, .51), .08, .36)]
    for name, color, metal, rough in palette:
        mat = STUDY.material(name, color, metal, rough)
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        for node in nodes:
            if node.type == 'BUMP':
                node.inputs['Strength'].default_value = .09
                node.inputs['Distance'].default_value = .002
        # Recesses and seals must stay dark; edge wear belongs to painted shells.
        if name not in ('Teal', 'Hull', 'Inset', 'Ochre'):
            continue
        bsdf = nodes.get('Principled BSDF')
        base_color = bsdf.inputs['Base Color'].links[0].from_socket
        geometry = nodes.new('ShaderNodeNewGeometry')
        edge = nodes.new('ShaderNodeValToRGB')
        edge.color_ramp.elements[0].position = .48
        edge.color_ramp.elements[1].position = .57
        links.new(geometry.outputs['Pointiness'], edge.inputs[0])
        mix = nodes.new('ShaderNodeMixRGB')
        weight = nodes.new('ShaderNodeMath'); weight.operation = 'MULTIPLY'
        links.new(edge.outputs['Color'], weight.inputs[0]); weight.inputs[1].default_value = .28
        links.new(weight.outputs[0], mix.inputs[0]); links.new(base_color, mix.inputs[1])
        mix.inputs[2].default_value = (.42, .40, .33, 1)
        links.new(mix.outputs[0], bsdf.inputs['Base Color'])
    lamp = bpy.data.materials.new('NW1_Lamp'); lamp.use_nodes = True
    bsdf = lamp.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Base Color'].default_value = (1, .57, .16, 1)
    bsdf.inputs['Emission Color'].default_value = (1, .36, .07, 1)
    bsdf.inputs['Emission Strength'].default_value = .3
    BASE.MATS['Lamp'] = lamp


def lathe(name, center, profile, material, root, sides=48, caps=True):
    """Explicit vertical section with domed transitions; profile uses local height/radius."""
    vertices = [(center[0] + radius * math.cos(i * math.tau / sides), center[1] + height,
                 center[2] + radius * math.sin(i * math.tau / sides))
                for height, radius in profile for i in range(sides)]
    faces = [tuple(reversed(range(sides)))] if caps else []
    for ring in range(len(profile) - 1):
        for i in range(sides):
            j = (i + 1) % sides
            faces.append((ring*sides+i, ring*sides+j, (ring+1)*sides+j, (ring+1)*sides+i))
    if caps: faces.append(tuple((len(profile)-1)*sides+i for i in range(sides)))
    obj = STUDY.mesh(name, vertices, faces, material, root, 0)
    for poly in obj.data.polygons: poly.use_smooth = len(poly.vertices) == 4
    return obj


def torus(name, center, major, minor, material, root, axis='y'):
    bpy.ops.mesh.primitive_torus_add(major_segments=32, minor_segments=8, location=BASE.point(center),
                                   major_radius=major, minor_radius=minor)
    obj = bpy.context.object
    if axis == 'z': obj.rotation_euler.x = math.pi/2
    elif axis == 'x': obj.rotation_euler.y = math.pi/2
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return BASE.finish(obj, name, material, root)


def fastener(x, y, z, root, material='Steel', radius=.014):
    BASE.cylinder('Captive hex fastener', (x, y, z), radius, .008, material, root, 'z', 6)


def indicator(root, p):
    BASE.cylinder('Indicator socket', p, .040, .035, 'Dark', root)
    work = BASE.empty(root.name + '_Work_ConditionIndicator', root, (p[0], p[1]+.03, p[2]))
    BASE.cylinder('Amber status lens', (0, .01, 0), .031, .035, 'Lamp', work, sides=16)
    BASE.cylinder('Indicator weather lip', (p[0], p[1]+.065, p[2]), .039, .012, 'Steel', root)


def tray(root, p, size):
    surface = BASE.empty(root.name + '_Placement_MaintenanceTray', root, p)
    BASE.box('Tray pressed base', (0, -.009, 0), (size[0], .018, size[1]), 'Steel', surface, .004)
    for sign in (-1, 1):
        BASE.box('Tray folded long lip', (0, .014, sign*(size[1]-.016)/2),
                 (size[0], .028, .016), 'Steel', surface, .003)
        BASE.box('Tray folded end', (sign*(size[0]-.016)/2, .014, 0),
                 (.016, .028, size[1]), 'Steel', surface, .003)
    return surface


def tank(root):
    # Raised tank sits inside a serviceable frame; bevelled rails have a visible underside.
    for z in (-.35, .35):
        STUDY.extrude_profile('Folded mounting rail', -.86, .86,
            [(.025,z-.065),(.025,z+.065),(.10,z+.065),(.15,z+.035),(.15,z-.035),(.10,z-.065)],
            'Dark', root, .006)
        for x in (-.75, .74):
            BASE.box('Isolated mounting pad', (x, .019, z), (.19,.028,.17), 'Rubber', root, .007)
    # Rounded shoulders and a domed crown replace a straight primitive cap.
    lathe('Spun storage vessel', (-.26, 0, 0),
          [(.18,.14),(.20,.27),(.26,.38),(.35,.455),(.48,.47),(1.18,.47),
           (1.30,.455),(1.39,.38),(1.445,.26),(1.465,.14)], 'Teal', root)
    for y in (.36, 1.25):
        lathe('Rolled circumferential reinforcement', (-.26,0,0),
              [(y-.02,.451),(y-.014,.466),(y+.014,.466),(y+.02,.451)], 'Hull', root)
    for x in (-.55,.03):
        STUDY.extrude_profile('Tank saddle foot', x-.055, x+.055,
            [(.11,-.36),(.11,.36),(.38,.31),(.31,0),(.38,-.31)], 'Steel', root, .008)
    # Service cabinet is a distinct warm-grey structure with a clipped lower panel.
    STUDY.plate('Service cabinet rear', .54,.68,.32,.58,1.05,.075,.06,'Hull',root)
    for x in (.25,.83):
        STUDY.extrude_profile('Service cabinet folded side', x-.022,x+.022,
            [(.15,-.32),(1.13,-.32),(1.22,-.22),(1.22,.39),(.15,.39)], 'Hull', root, .005)
    BASE.box('Service cabinet top', (.54,1.235,.04), (.60,.04,.74), 'Inset', root, .012)
    STUDY.plate('Front inspection recess', .54,.66,-.337,.51,.89,.02,.045,'Dark',root)
    STUDY.plate('Front inspection plate', .54,.66,-.352,.45,.81,.014,.038,'Inset',root)
    for x in (.37,.70):
        for y in (.32,1.00): fastener(x,y,-.367,root)
    STUDY.plate('Recessed service handle', .54,.88,-.37,.17,.09,.014,.02,'Dark',root)
    BASE.box('Service handle', (.54,.88,-.391), (.10,.017,.02), 'Steel',root,.003)
    for y in (.36,.41,.46,.51):
        BASE.box('Cabinet ventilation opening', (.54,y,-.367), (.28,.014,.01), 'Dark',root,.002)
    # Existing maintenance motion remains a separate sliding rear door.
    door = BASE.empty(root.name+'_Work_ServiceDoor', root, (.54,.73,.423))
    STUDY.plate('Sliding service panel', 0,0,-.012,.50,.78,.025,.045,'Hull',door)
    BASE.pipe('Service pull', [(-.10,-.20,.035),(-.10,-.17,.065),(.10,-.17,.065),(.10,-.20,.035)],
              .012,'Ochre',door)
    for x in (.245,.835): BASE.box('Rear door guide', (x,.74,.445),(.018,.88,.026),'Steel',root,.003)
    for x in (-.37,-.17):
        BASE.pipe('Protected level gauge rail',[(x,.57,-.47),(x,1.06,-.47)],.010,'Steel',root)
    STUDY.plate('Gauge inset',-.27,.81,-.474,.135,.40,.012,.018,'Dark',root)
    for y in (.66,.72,.78,.84,.90,.96): BASE.box('Gauge tick',(-.31,y,-.491),(.028,.006,.009),'Ceramic',root,.001)
    BASE.box('Water level scale',(-.255,.81,-.491),(.026,.28,.010),'Ceramic',root,.002)
    # Source-specific markers keep physical contacts and motion pivots inspectable.
    lathe('Open filler neck',(-.26,0,-.40),[(1.32,.10),(1.41,.10),(1.41,.08),(1.32,.08),(1.32,.10)],'Steel',root,32,False)
    lid = BASE.empty(root.name+'_Work_FillLid',root,(-.26,1.41,-.28))
    lathe('Dished filler cap',(0,0,-.12),[(0,.115),(.018,.125),(.038,.11),(.046,.045)],'Hull',lid,32)
    BASE.pipe('Filler cap loop',[(-.035,.045,-.12),(-.035,.08,-.12),(.035,.08,-.12),(.035,.045,-.12)],.009,'Ochre',lid)
    BASE.empty(root.name+'_Work_Inlet',root,(-.26,1.405,-.40))
    valve = BASE.empty(root.name+'_Work_Valve',root,(-.48,.64,-.51))
    BASE.cylinder('Valve spindle',(0,0,-.025),.026,.085,'Steel',valve,'z')
    torus('Open valve wheel',(0,0,-.069),.075,.011,'Ochre',valve,'z')
    for angle in (0,math.tau/3,2*math.tau/3):
        BASE.pipe('Valve spoke',[(0,0,-.07),(.068*math.cos(angle),.068*math.sin(angle),-.07)],.009,'Steel',valve)
    BASE.pipe('Outlet riser',[(-.48,.55,-.36),(-.60,.77,-.44),(-.58,1.10,-.47),(-.50,1.17,-.49)],.022,'Steel',root)
    BASE.cylinder('Hose quick coupling',(-.5,1.17,-.54),.035,.08,'Steel',root,'z',16)
    BASE.empty(root.name+'_Work_Outlet',root,(-.50,1.17,-.58))
    indicator(root,(-.59,1.35,.15))
    tray(root,(.54,1.28,.035),(.50,.38))


def dispenser(root):
    # Hollow front volume, not a solid box with a painted cavity.
    for x in (-.30,.30):
        for z in (-.24,.24): BASE.box('Rubber cabinet foot',(x,.045,z),(.12,.07,.12),'Rubber',root,.01)
    BASE.box('Cabinet lower pan',(0,.115,0),(.77,.085,.66),'Dark',root,.016)
    for x in (-.36,.36):
        STUDY.extrude_profile('Formed dispenser side',x-.024,x+.024,
            [(.13,-.32),(.26,-.35),(.92,-.35),(1.045,-.28),(1.045,.28),(.99,.33),(.13,.33)],
            'Hull',root,.007)
    STUDY.plate('Dispenser rear panel',0,.56,.29,.69,.83,.026,.038,'Teal',root)
    STUDY.plate('Lower service door recess',0,.32,-.347,.66,.32,.025,.035,'Dark',root)
    STUDY.plate('Lower removable service door',0,.32,-.365,.60,.26,.016,.026,'Teal',root)
    for x in (-.245,.245): fastener(x,.33,-.387,root)
    for x in (-.18,-.12,-.06,0,.06,.12,.18):
        BASE.box('Lower ventilation slot',(x,.30,-.378),(.022,.09,.009),'Dark',root,.003)
    STUDY.plate('Tap cavity back',0,.68,.07,.65,.45,.025,.025,'Inset',root)
    BASE.box('Recessed drinking shelf',(0,.46,-.105),(.65,.028,.46),'Steel',root,.009)
    for x in (-.25,-.15,-.05,.05,.15,.25):
        BASE.box('Drain grate',(x,.481,-.11),(.026,.014,.32),'Dark',root,.003)
    BASE.pipe('Drinking spout',[(.04,.78,.045),(.04,.78,-.15),(.04,.70,-.19)],.018,'Steel',root)
    valve = BASE.empty(root.name+'_Work_Valve',root,(.20,.78,-.12))
    BASE.cylinder('Tap knob', (0,0,0),.035,.045,'Ochre',valve,'z',16)
    BASE.box('Tap thumb lever',(0,.04,-.026),(.020,.07,.014),'Steel',valve,.003)
    STUDY.plate('Control brow',0,.935,-.327,.64,.125,.034,.024,'Hull',root)
    STUDY.plate('Status window',-.18,.935,-.365,.13,.047,.008,.006,'Dark',root)
    BASE.box('Readable status strip',(-.18,.935,-.373),(.085,.01,.008),'Ceramic',root,.001)
    for x in (.13,.22): BASE.cylinder('Small control', (x,.934,-.37),.020,.014,'Ochre',root,'z',12)
    top = BASE.box('Top rim',(0,1.025,0),(.74,.035,.61),'Dark',root,.007)
    hole = BASE.cylinder('Temporary refill aperture',(0,1.025,-.16),.094,.20,'Dark',root,sides=48)
    HELPER.prepare_meshes([top,hole]); HELPER.select([top])
    cut = top.modifiers.new('Actual refill opening','BOOLEAN'); cut.operation='DIFFERENCE'; cut.object=hole
    bpy.ops.object.modifier_apply(modifier=cut.name); bpy.data.objects.remove(hole,do_unlink=True)
    lathe('Open refill well',(0,0,-.16),[(1.015,.12),(1.053,.12),(1.053,.094),(1.015,.094),(1.015,.12)],'Steel',root,32,False)
    BASE.empty(root.name+'_Work_Inlet',root,(0,1.05,-.16))
    lid=BASE.empty(root.name+'_Work_FillLid',root,(0,1.045,.30))
    BASE.box('Separate top hatch',(0,.032,-.30),(.76,.055,.65),'Teal',lid,.014)
    BASE.pipe('Recessed top pull',[(-.07,.065,-.47),(-.07,.085,-.47),(.07,.085,-.47),(.07,.065,-.47)],.010,'Steel',lid)
    for x in (-.27,.27): BASE.cylinder('Top hinge',(x,.02,0),.021,.09,'Steel',lid,'x',12)


def bake_preserving_pivots(roots, out):
    meshes=[o for r in roots for o in r.children_recursive if o.type=='MESH']
    HELPER.prepare_meshes(meshes)
    opaque=[o for o in meshes if o.data.materials[0].name != 'NW1_Lamp']
    parents=[]
    for obj in opaque:
        if obj.parent not in parents: parents.append(obj.parent)
        tag='part_'+str(parents.index(obj.parent))
        group=obj.vertex_groups.new(name=tag); group.add(list(range(len(obj.data.vertices))),1,'REPLACE')
    HELPER.select(opaque); bpy.ops.object.join(); joined=bpy.context.object
    HELPER.bake(joined,out,'NW8_WaterFacilities')
    # Bake once for a shared atlas, then restore original owning pivots. Loose components
    # retain the temporary vertex tags; regrouping does not change UVs or world vertices.
    before=set(bpy.data.objects)
    HELPER.select([joined]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.separate(type='LOOSE'); bpy.ops.object.mode_set(mode='OBJECT')
    pieces=list(set(bpy.data.objects)-before)+[joined]
    grouped={i:[] for i in range(len(parents))}
    for obj in pieces:
        tags={obj.vertex_groups[g.group].name for v in obj.data.vertices for g in v.groups if g.weight>.5}
        assert len(tags)==1,('invalid-pivot-partition',obj.name,tags)
        index=int(next(iter(tags)).split('_')[1]); grouped[index].append(obj)
    for index,objects in grouped.items():
        HELPER.select(objects)
        if len(objects)>1: bpy.ops.object.join()
        obj=bpy.context.object; world=obj.matrix_world.copy(); obj.parent=parents[index]; obj.matrix_world=world
        obj.name=parents[index].name+'_Mesh'; obj.vertex_groups.clear()
    for obj in meshes:
        # Objects joined into the atlas have been deleted by Blender.
        try:
            if obj.type!='MESH' or obj.data.materials[0].name!='NW1_Lamp': continue
        except ReferenceError: continue
        HELPER.select([obj]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.uv.smart_project(island_margin=.01); bpy.ops.object.mode_set(mode='OBJECT')


def export(root, out):
    objects=[root]+list(root.children_recursive); source=BASE.stats(objects)
    assert source['degenerateTriangles']==0 and source['uvComplete'],source
    HELPER.select(objects)
    path=out/(root.name+'.fbx')
    bpy.ops.export_scene.fbx(filepath=str(path),use_selection=True,object_types={'EMPTY','MESH'},
        apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',axis_forward='-Z',axis_up='Y',
        use_space_transform=True,bake_space_transform=False,add_leaf_bones=False,bake_anim=False,
        use_mesh_modifiers=True,mesh_smooth_type='FACE',path_mode='AUTO',use_custom_props=True)
    before=set(bpy.data.objects); bpy.ops.import_scene.fbx(filepath=str(path))
    imported=set(bpy.data.objects)-before; readback=BASE.stats(imported)
    assert source['triangles']==readback['triangles'] and readback['uvComplete']
    assert readback['degenerateTriangles']==0
    for key in ('minimumBlender','maximumBlender'):
        assert all(abs(a-b)<.002 for a,b in zip(source[key],readback[key])),(key,source,readback)
    assert source['pivots'].keys()==readback['pivots'].keys()
    for key,p in source['pivots'].items():
        assert all(abs(a-b)<.002 for a,b in zip(p,readback['pivots'][key])),key
    for obj in imported: bpy.data.objects.remove(obj,do_unlink=True)
    return {'name':root.name,'source':source,'roundTrip':readback}


def render_preview(roots, out):
    roots[0].location=BASE.point((-1.02,0,0)); roots[1].location=BASE.point((.78,0,-.06))
    scene=bpy.context.scene; scene.render.engine='CYCLES'; scene.cycles.samples=32
    scene.cycles.use_denoising=True
    floor=STUDY.material('PreviewFloor',(.20,.19,.16),.08,.85)
    BASE.box('Preview ground',(0,-.09,0),(200,.14,200),'PreviewFloor',None,0)
    world=bpy.data.worlds.new('Warm workshop studio'); scene.world=world; world.use_nodes=True
    world.node_tree.nodes.get('Background').inputs['Color'].default_value=(.48,.51,.54,1)
    world.node_tree.nodes.get('Background').inputs['Strength'].default_value=.40
    for name,p,power,size,color in [('Key',(-3,5,-4),700,5,(1,.86,.68)),('Fill',(4,3,-1),350,4,(.79,.89,1))]:
        light=bpy.data.lights.new(name,'AREA'); light.energy=power; light.shape='DISK'; light.size=size; light.color=color
        obj=bpy.data.objects.new(name,light); scene.collection.objects.link(obj); obj.location=BASE.point(p)
        obj.rotation_euler=(BASE.point((0,.6,0))-obj.location).to_track_quat('-Z','Y').to_euler()
    data=bpy.data.cameras.new('Facilities comparison'); camera=bpy.data.objects.new('Facilities comparison',data)
    scene.collection.objects.link(camera); camera.location=BASE.point((3.2,2.9,-4.9))
    camera.rotation_euler=(BASE.point((-.1,.78,0))-camera.location).to_track_quat('-Z','Y').to_euler()
    data.type='ORTHO'; data.ortho_scale=3.5; scene.camera=camera
    scene.render.resolution_x=1600; scene.render.resolution_y=1100; scene.render.resolution_percentage=100
    scene.view_settings.view_transform='AgX'; scene.render.image_settings.file_format='PNG'
    scene.render.filepath=str(out/'water-facilities-preview.png')
    bpy.ops.wm.save_as_mainfile(filepath=str(out/'NW8_WaterFacilities.blend'))
    bpy.ops.render.render(write_still=True)


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--output',required=True)
    parser.add_argument('--preview-only',action='store_true'); args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    out=Path(args.output).resolve(); out.mkdir(parents=True,exist_ok=True)
    (out/'manifest.json').write_text(json.dumps({'version':VERSION,'status':'running'}),encoding='utf-8')
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    bpy.context.scene.unit_settings.system='METRIC'; bpy.context.scene.render.threads_mode='FIXED'; bpy.context.scene.render.threads=8
    materials(); roots=[]
    for name,build in [('WaterTank',tank),('Dispenser',dispenser)]:
        root=BASE.empty('NW8_'+name); build(root); roots.append(root)
        for axis,p in [('Right',(1,0,0)),('Up',(0,1,0)),('Forward',(0,0,1))]: BASE.empty(root.name+'_Axis'+axis,root,p)
    records=[]
    # Keep an unbaked, neutral-origin master with the original editable components.
    # The later presentation .blend has the pair separated for comparison.
    bpy.ops.wm.save_as_mainfile(filepath=str(out/'NW8_WaterFacilities_Source.blend'))
    if not args.preview_only:
        bake_preserving_pivots(roots,out)
        records=[export(root,out) for root in roots]
    else: HELPER.prepare_meshes([o for r in roots for o in r.children_recursive if o.type=='MESH'])
    render_preview(roots,out)
    paths=[Path(__file__),HELPER_PATH,HELPER.STUDY_PATH,STUDY.BASE_PATH,HELPER.DETAIL_PATH,HELPER.CANOPY_PATH]
    generated=[p for p in out.iterdir() if p.suffix in ('.fbx','.png')]
    manifest={'version':VERSION,'status':'preview-only' if args.preview_only else 'passed-export',
              'blenderVersion':bpy.app.version_string,'atlasSize':2048,'assets':records,
              'sources':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in paths],
              'files':[{'file':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in generated]}
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    print('WATER_FACILITIES_READY '+manifest['status'],flush=True)


if __name__=='__main__': main()
