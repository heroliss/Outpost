"""Curated canvas workwear derived from the committed NW3 skinned residents.

No upstream files are modified. Bone transforms, hands and foot soles stay intact.
Pocket surfaces inherit barycentric skin weights from the actual garment triangles.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import barycentric_transform

VAR_PATH=Path(__file__).with_name('blender_nomad_resident_variants.py')
SPEC=importlib.util.spec_from_file_location('nomad_resident_source',VAR_PATH)
VAR=importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(VAR)
VERSION='0.2.3'
PALETTE={'Mechanic':(.18,.29,.25),'Caretaker':(.38,.29,.17),'Driver':(.22,.265,.29)}


def islands(obj):
    neighbors={v.index:set() for v in obj.data.vertices}
    for e in obj.data.edges:
        a,b=e.vertices; neighbors[a].add(b); neighbors[b].add(a)
    unseen=set(neighbors); result=[]
    while unseen:
        todo=[min(unseen)]; found=set()
        while todo:
            i=todo.pop()
            if i in found: continue
            found.add(i); unseen.discard(i); todo.extend(neighbors[i]-found)
        result.append(found)
    return sorted(result,key=lambda g:-len(g))


def remove_indices(obj,indices):
    if not indices:return
    mesh=bmesh.new(); mesh.from_mesh(obj.data); mesh.verts.ensure_lookup_table()
    bmesh.ops.delete(mesh,geom=[mesh.verts[i] for i in indices],context='VERTS')
    mesh.to_mesh(obj.data); mesh.free()


def assign(obj,materials,choose):
    obj.data.materials.clear()
    for m in materials:obj.data.materials.append(m)
    for face in obj.data.polygons:
        points=[obj.matrix_world@obj.data.vertices[i].co for i in face.vertices]
        face.material_index=choose(sum(points,Vector())/len(points))
        face.use_smooth=True


def normalize_skin(obj,rig):
    """Bake the existing Unity Bone4 choice before interpolating garment weights.

    Imported NW3 weights are not normalized and can contain five influences.
    Normalizing each source vertex first matters: interpolating raw weights would
    bias the patch toward vertices with larger sums, unlike the rendered cloth.
    """
    bones=set(rig.data.bones.keys()); maximum=0.; sums=[]
    for vertex in obj.data.vertices:
        entries=sorted([(g.group,g.weight) for g in vertex.groups
            if obj.vertex_groups[g.group].name in bones and g.weight>0],key=lambda g:-g[1])
        total=sum(w for _,w in entries)
        if total<1e-8:raise RuntimeError(f'{obj.name}: unweighted source vertex {vertex.index}')
        sums.append(total); selected=entries[:4]; retained=sum(w for _,w in selected)
        maximum=max(maximum,1-retained/total)
        for group in obj.vertex_groups:group.remove([vertex.index])
        for index,weight in selected:obj.vertex_groups[index].add([vertex.index],weight/retained,'REPLACE')
    return {'mesh':obj.name,'sourceWeightSumMin':min(sums),'sourceWeightSumMax':max(sums),
        'maxDiscardedInfluenceFraction':maximum}


def fabric(name,color,tex,normal):
    material=VAR.material(name,color,tex,normal)
    if material.get('fineWeave'):return material
    nodes,links=material.node_tree.nodes,material.node_tree.links
    uv=nodes.new('ShaderNodeTexCoord');mapping=nodes.new('ShaderNodeVectorMath')
    mapping.operation='SCALE';mapping.inputs['Scale'].default_value=12
    links.new(uv.outputs['UV'],mapping.inputs[0])
    for node in list(nodes):
        if node.type=='TEX_IMAGE':links.new(mapping.outputs['Vector'],node.inputs['Vector'])
        if node.type=='NORMAL_MAP':node.inputs['Strength'].default_value=.15
    material['fineWeave']=True
    return material


def restore_source_materials(meshes,identity,gender,asset_root):
    """FBX cannot round-trip the original tint nodes; rebuild the known NW3 recipe."""
    third=asset_root.parent/'ThirdParty/QuaterniusUniversalBaseCharacters'
    recipe=json.loads(VAR_PATH.with_name('nomad_resident_variants.json').read_text(encoding='utf-8-sig'))
    config=next(v for v in recipe['variants'] if v['id']==identity)
    skin=third/('T_Superhero_Male_Ligh.png' if gender=='Male' else 'T_Superhero_Female_Light_BaseColor.png')
    definitions={
        'NW3_Skin_'+gender:((1,1,1),skin,third/f'T_Superhero_{gender}_Normal.png'),
        'NW3_Eyes':((1,1,1),third/'T_Eye_Brown.png',None),
        'NW3_Hair_'+identity:(config['hairColor'],None,None)}
    for name,(color,texture,normal) in definitions.items():
        old=bpy.data.materials.get(name)
        if old is not None:old.name='Imported_'+name
        new=VAR.material(name,color,texture,normal)
        for obj in meshes:
            for slot in obj.material_slots:
                if slot.material==old:slot.material=new
    return [p for definition in definitions.values() for p in definition[1:] if p]


def pocket(obj,rig,name,cx,cz,width,height,material,scale,bulge=.006,bridge=False):
    """Surface-bound, deforming patch; reject missed rays instead of floating a box."""
    obj.data.calc_loop_triangles()
    world=[obj.matrix_world@v.co for v in obj.data.vertices]
    triangles=[tuple(t.vertices) for t in obj.data.loop_triangles]
    bvh=BVHTree.FromPolygons(world,triangles,all_triangles=True)
    verts=[]; weights=[]; n=5; max_discard=0
    for row in range(n):
        for col in range(n):
            u,v=col/(n-1),row/(n-1)
            hit,normal,index,_=bvh.ray_cast(Vector(((cx+(u-.5)*width)*scale,scale,(cz+(v-.5)*height)*scale)),Vector((0,-1,0)),2*scale)
            if hit is None:raise RuntimeError(f'{name}: pocket misses actual cloth at {u},{v}')
            samples=[(hit,index,1)]
            if bridge:
                # Close the narrow original laced neckline using its real side edges.
                samples=[]
                for x,blend in [(-.034,1-u),(.034,u)]:
                    side,side_normal,side_index,_=bvh.ray_cast(Vector((x*scale,scale,hit.z)),Vector((0,-1,0)),2*scale)
                    if side is None or side_normal.y<.2:raise RuntimeError('Neckline has no front cloth edge')
                    samples.append((side,side_index,blend))
                hit=sum((point*blend for point,_,blend in samples),Vector())
            combined={}
            for point,tri_index,blend in samples:
                tri=triangles[tri_index]
                bary=barycentric_transform(point,*[world[i] for i in tri],Vector((1,0,0)),Vector((0,1,0)),Vector((0,0,1)))
                for source,weight in zip(tri,bary):
                    for group in obj.data.vertices[source].groups:
                        key=obj.vertex_groups[group.group].name
                        combined[key]=combined.get(key,0)+max(0,weight)*group.weight*blend
            source_total=sum(combined.values())
            if abs(source_total-1)>.001:raise RuntimeError(f'{name}: invalid normalized cloth sample')
            combined=sorted(combined.items(),key=lambda item:-item[1])[:4]
            total=sum(w for _,w in combined)
            if total<.90:raise RuntimeError(f'{name}: patch loses over 10% skin influence')
            max_discard=max(max_discard,1-total/source_total)
            weights.append([(k,w/total) for k,w in combined])
            hit.y+=(.0025+bulge*math.sin(math.pi*u)*math.sin(math.pi*v))*scale
            verts.append(obj.matrix_world.inverted()@hit)
    faces=[(i,i+n,i+n+1,i+1) for row in range(n-1) for col in range(n-1) for i in [row*n+col]]
    mesh=bpy.data.meshes.new(name); mesh.from_pydata(verts,[],faces); mesh.update()
    patch=bpy.data.objects.new(name,mesh); bpy.context.collection.objects.link(patch)
    patch.parent=rig; patch.matrix_world=obj.matrix_world.copy()
    patch['maxDiscardedInfluenceFraction']=max_discard
    mod=patch.modifiers.new('Inherited cloth deformation','ARMATURE'); mod.object=rig
    for vertex,entries in enumerate(weights):
        for key,weight in entries:
            group=patch.vertex_groups.get(key) or patch.vertex_groups.new(name=key); group.add([vertex],weight,'REPLACE')
    mesh.materials.append(material)
    uv=mesh.uv_layers.new(name='UVMap')
    for face in mesh.polygons:
        face.use_smooth=True
        for loop in face.loop_indices:
            i=mesh.loops[loop].vertex_index; uv.data[loop].uv=(i%n/(n-1),i//n/(n-1))
    return patch


def skeleton(rig):
    return {b.name:[round(v,6) for row in (rig.matrix_world@b.matrix_local) for v in row] for b in rig.data.bones}


def merge_patches(garment,patches,meshes):
    """Keep editable mesh islands, without a separate skinned renderer per seam."""
    bpy.ops.object.select_all(action='DESELECT');garment.select_set(True)
    for patch in patches:patch.select_set(True);meshes.remove(patch)
    bpy.context.view_layer.objects.active=garment;bpy.ops.object.join()


def skin_summary(meshes):
    count=0; max_weights=0
    for obj in meshes:
        bone_names=set(obj.modifiers[0].object.data.bones.keys()) if obj.modifiers and obj.modifiers[0].type=='ARMATURE' else set()
        for vertex in obj.data.vertices:
            weights=[g.weight for g in vertex.groups if obj.vertex_groups[g.group].name in bone_names and g.weight>1e-7]
            if not weights or len(weights)>4 or abs(sum(weights)-1)>.001:raise RuntimeError(f'{obj.name}: invalid Bone4 skin weights at {vertex.index}')
            max_weights=max(max_weights,len(weights));count+=1
    return {'vertices':count,'maxInfluences':max_weights}


def build(identity,gender,scale,asset_root,out):
    source=asset_root/'Models'/f'NW3_{identity}.fbx'
    rig,meshes=VAR.import_fbx(source); before=skeleton(rig)
    source_skin=[normalize_skin(obj,rig) for obj in meshes]
    # Re-imports create suffixed copies; the same source material keeps one stable id.
    for obj in meshes:
        for slot in obj.material_slots:
            name=slot.material.name
            if len(name)>4 and name[-4]=='.' and name[-3:].isdigit():
                existing=bpy.data.materials.get(name[:-4])
                if existing:slot.material=existing
    body=next(o for o in meshes if o.name.endswith('_Body'))
    arms=next(o for o in meshes if o.name.endswith('_Arms'))
    legs=next(o for o in meshes if o.name.endswith('_Legs'))
    feet=next(o for o in meshes if o.name.endswith('_Feet'))
    tex=asset_root/'Textures'/'NW1_Fabric_Color.png'; normal=asset_root/'Textures'/'NW1_Fabric_Normal.png'
    retained_textures=restore_source_materials(meshes,identity,gender,asset_root)
    shirt=fabric('NW10_Shirt_'+identity,PALETTE[identity],tex,normal)
    pants=fabric('NW10_Trousers',(.12,.14,.12),tex,normal)
    leather=VAR.material('NW10_Leather',(.085,.06,.04))
    trim=fabric('NW10_Stitch',(.26,.23,.16),tex,normal)
    original_skin=next((m for m in arms.data.materials if m.name.startswith('NW3_Skin')),None)
    # The female bracer buckles are disconnected ornaments; the full sleeve remains.
    removed=0
    if gender=='Female':
        ornaments=set().union(*[g for g in islands(arms) if len(g)<300])
        removed=len(ornaments);remove_indices(arms,ornaments)
        assign(arms,[shirt,leather],lambda p:1 if abs(p.x)/scale>.62 else 0)
        for vertex in body.data.vertices:
            p=body.matrix_world@vertex.co;z=p.z/scale
            if 1.02<z<1.38:
                blend=math.sin(math.pi*(z-1.02)/.36)
                p.x*=1+.14*blend
                if p.y>0:p.y+=.015*scale*blend
            if z<1.08:p.z=scale*(1.02+(z-.9879)*.35)
            vertex.co=body.matrix_world.inverted()@p
        cloth=bmesh.new();cloth.from_mesh(body.data)
        bmesh.ops.remove_doubles(cloth,verts=list(cloth.verts),dist=.000001)
        relaxed=[v for v in cloth.verts if not v.is_boundary and 1.05<(body.matrix_world@v.co).z/scale<1.40]
        for _ in range(8):bmesh.ops.smooth_vert(cloth,verts=relaxed,factor=.4,use_axis_x=True,use_axis_y=True,use_axis_z=False)
        cloth.to_mesh(body.data);cloth.free()
        normalize_skin(body,rig)
    else:
        assign(arms,[shirt,original_skin],lambda p:1 if abs(p.x)/scale>.525 else 0)
        # Flatten the oversized tunic belt into a narrow webbing belt, keeping skin weights.
        groups=islands(body)
        for part in groups[1:]:
            for index in part:
                vertex=body.data.vertices[index];p=body.matrix_world@vertex.co
                p.z=scale*1.093+(p.z-scale*1.093)*.42
                vertex.co=body.matrix_world.inverted()@p
        # A shorter, nearly level shirt hem replaces the long pointed tunic tails.
        for index in groups[0]:
            vertex=body.data.vertices[index];p=body.matrix_world@vertex.co
            if p.z/scale<1.065:
                p.z=scale*(1.008+(p.z/scale-.9214)*.32)
                vertex.co=body.matrix_world.inverted()@p
            if abs(p.x)/scale<.045 and 1.16<p.z/scale<1.44 and p.y>0:
                # Remove the raised lacing silhouette before adding a plain placket.
                p.y=min(p.y,scale*(.112-(p.z/scale-1.16)*.11))
                vertex.co=body.matrix_world.inverted()@p
    body_groups=islands(body);main=body_groups[0]
    body.data.materials.clear();body.data.materials.append(shirt);body.data.materials.append(leather)
    for f in body.data.polygons:f.material_index=0 if set(f.vertices)<=main else 1
    assign(legs,[pants],lambda p:0)
    # Split the calf at the trouser cuff, so the new material boundary is horizontal.
    bm=bmesh.new();bm.from_mesh(feet.data)
    plane=feet.matrix_world.inverted()@Vector((0,0,.205*scale))
    direction=feet.matrix_world.to_3x3().transposed()@Vector((0,0,1))
    bmesh.ops.bisect_plane(bm,geom=list(bm.verts)+list(bm.edges)+list(bm.faces),dist=.00001,
        plane_co=plane,plane_no=direction,clear_inner=False,clear_outer=False)
    bm.to_mesh(feet.data);bm.free()
    assign(feet,[pants,leather],lambda p:0 if p.z/scale>.205 else 1)
    # Reduce the old reinforced boot cuff's radial ridges inside the trouser silhouette.
    leg_world=[legs.matrix_world@v.co for v in legs.data.vertices]
    leg_min=min(p.z for p in leg_world)
    leg_centers={};leg_radii={}
    for sign in (-1,1):
        edge=[p for p in leg_world if p.x*sign>0 and p.z<leg_min+.02*scale]
        center=sum(edge,Vector())/len(edge);leg_centers[sign]=center
        leg_radii[sign]=sum(Vector((p.x-center.x,p.y-center.y,0)).length for p in edge)/len(edge)*.82
    for vertex in feet.data.vertices:
        p=feet.matrix_world@vertex.co;z=p.z/scale
        if .205<z<.47:
            sign=1 if p.x>0 else -1;t=min(1,(z-.205)/.20)
            center=Vector(((.101 if gender=='Male' else .127)*scale*sign,-.060*scale,p.z))
            center.x=center.x*(1-t)+leg_centers[sign].x*t
            center.y=center.y*(1-t)+leg_centers[sign].y*t
            radial=p-center
            radius=radial.length/scale
            blend=min(1,(z-.205)/.035)*.95
            target=.057*(1-t)+(leg_radii[sign]/scale+.001)*t
            if radius>1e-5:p=center+radial*(1-blend*(1-target/radius))
            vertex.co=feet.matrix_world.inverted()@p
    # Seam and patch grids follow the existing garment rather than a single bone.
    chest=1.335 if gender=='Male' else 1.36
    pocket_x=.087 if gender=='Male' else .065
    pocket_width=.092 if gender=='Male' else .070
    pocket_height=.105 if gender=='Male' else .090
    patch_start=len(meshes)
    pocket_surface=body
    for side in (-1,1):
        meshes.append(pocket(pocket_surface,rig,'Workshirt pocket '+str(side),side*pocket_x,chest,pocket_width,pocket_height,shirt,scale))
        meshes.append(pocket(pocket_surface,rig,'Pocket upper seam '+str(side),side*pocket_x,chest+pocket_height*.45,pocket_width*.95,.007,trim,scale,.002))
        meshes.append(pocket(legs,rig,'Trouser patch '+str(side),side*.105,.78,.060,.100,pants,scale,.004))
    if gender=='Male':meshes.append(pocket(body,rig,'Workshirt placket',0,1.31,.028,.30,shirt,scale,.001))
    if gender=='Male':meshes.append(pocket(body,rig,'Closed cloth neckline',0,1.498,.068,.055,shirt,scale,.001,True))
    patches=meshes[patch_start:]
    patch_report=[{'name':o.name,'maxDiscardedInfluenceFraction':o.get('maxDiscardedInfluenceFraction',0)} for o in patches]
    leg_patches=[o for o in patches if o.name.startswith('Trouser patch')]
    body_patches=[o for o in patches if not o.name.startswith('Trouser patch')]
    merge_patches(legs,leg_patches,meshes)
    merge_patches(body,body_patches,meshes)
    for obj in meshes:
        for face in obj.data.polygons:face.use_smooth=True
    # Plane splitting interpolates weights too; canonicalize its new edge vertices.
    normalize_skin(feet,rig)
    bpy.context.view_layer.update()
    if skeleton(rig)!=before:raise RuntimeError('Workwear changed skeleton')
    skin=skin_summary(meshes)
    bpy.ops.object.select_all(action='DESELECT')
    for obj in [rig]+meshes:obj.select_set(True)
    bpy.context.view_layer.objects.active=rig
    destination=out/f'NW10_{identity}.fbx'
    bpy.ops.export_scene.fbx(filepath=str(destination),use_selection=True,add_leaf_bones=False,bake_anim=False,
        axis_forward='-Z',axis_up='Y',use_mesh_modifiers=True)
    # Fresh FBX readback must preserve the same bone matrices and valid weighted meshes.
    original=set(bpy.data.objects); read_rig,read_meshes=VAR.import_fbx(destination)
    read_bones=skeleton(read_rig)
    if before.keys()!=read_bones.keys():raise RuntimeError('FBX changed workwear bone names')
    max_error=max(abs(a-b) for key in before for a,b in zip(before[key],read_bones[key]))
    if max_error>.0001:raise RuntimeError('FBX changed workwear bones')
    read_skin=skin_summary(read_meshes)
    for obj in set(bpy.data.objects)-original:bpy.data.objects.remove(obj,do_unlink=True)
    return rig,{'id':identity,'body':gender,'source':str(source),'sourceSha256':VAR.digest(source),'fbx':destination.name,
        'sha256':VAR.digest(destination),'removedOrnamentVertices':removed,'boneCount':len(before),'maxBoneRoundTripError':max_error,
        'skin':skin,'sourceSkinNormalization':source_skin,'readbackSkin':read_skin,'patches':patch_report,
        'retainedTextures':[{'file':str(p),'sha256':VAR.digest(p)} for p in retained_textures],
        'meshes':[{'name':o.name,'triangles':sum(len(p.vertices)-2 for p in o.data.polygons),
            'maxPatchDiscardedInfluenceFraction':o.get('maxDiscardedInfluenceFraction',0)} for o in meshes]}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    out=Path(args.output).resolve();out.mkdir(parents=True,exist_ok=True)
    (out/'manifest.json').write_text(json.dumps({'version':VERSION,'status':'running'}))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.render.threads_mode='FIXED';bpy.context.scene.render.threads=8
    asset_root=Path(__file__).resolve().parents[3]/'Assets/Game/NomadWorkshop/ArtFirstPass'
    rigs=[];reports=[]
    for identity,gender,scale in [('Mechanic','Male',1),('Caretaker','Female',1),('Driver','Male',1.04)]:
        rig,report=build(identity,gender,scale,asset_root,out);rigs.append(rig);reports.append(report)
        for obj in [rig]+list(rig.children):obj.name=identity+'_'+obj.name
    bpy.ops.wm.save_as_mainfile(filepath=str(out/'NW10_Workwear_Source.blend'))
    for i,rig in enumerate(rigs):VAR.pose(rig);rig.location.x+=(i-1)*1.05
    bpy.context.view_layer.update();bpy.ops.wm.save_as_mainfile(filepath=str(out/'NW10_Workwear_Lineup.blend'))
    VAR.preview(out)
    camera=bpy.context.scene.camera;camera.location=(6,1.5,2.8)
    camera.rotation_euler=(Vector((0,0,.9))-camera.location).to_track_quat('-Z','Y').to_euler()
    bpy.context.scene.render.filepath=str(out/'residents-side.png');bpy.ops.render.render(write_still=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(out/'NW10_Workwear_Lineup.blend'))
    inputs=[Path(__file__),VAR_PATH,VAR_PATH.with_name('nomad_resident_variants.json'),asset_root/'Textures/NW1_Fabric_Color.png',asset_root/'Textures/NW1_Fabric_Normal.png']
    manifest={'version':VERSION,'status':'passed-export','blenderVersion':bpy.app.version_string,'variants':reports,
        'sources':[{'file':str(p),'sha256':VAR.digest(p)} for p in inputs]}
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    print('WORKWEAR_READY',flush=True)


if __name__=='__main__':main()
