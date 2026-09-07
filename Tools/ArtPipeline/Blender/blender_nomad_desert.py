"""Author the NW6 roadside desert in metres, with reusable tiling PBR textures.

Blender --background --factory-startup --python-exit-code 1 --python this.py -- --output DIR
Only writes DIR. The export manifest is successful only after an independent FBX
readback. Unity binding and visual acceptance are separate from this check.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import random
import sys
from pathlib import Path

import bpy
import bmesh
import numpy as np
from mathutils import Vector

BASE_PATH = Path(__file__).with_name('blender_nomad_art_set.py')
spec = importlib.util.spec_from_file_location('nomad_desert_base', BASE_PATH)
BASE = importlib.util.module_from_spec(spec)
spec.loader.exec_module(BASE)
VERSION = '0.1.0'
SIZE = 2048
GROUND_PERIOD = 6.0
TRACK_PERIOD = 1.4
GROUND_Y = -2.33
MATERIALS = {}


def noise(size, cells, seed):
    """Periodic, smooth value noise; integer cell counts make every mip tileable."""
    grid = np.random.default_rng(seed).random((cells, cells), dtype=np.float32)
    p = np.arange(size, dtype=np.float32) * (cells / size)
    a = np.floor(p).astype(np.int32)
    t = p - a
    t = t * t * (3 - 2 * t)
    b = (a + 1) % cells
    return ((1-t[:, None])*((1-t)*grid[a[:, None], a]+t*grid[a[:, None], b]) +
            t[:, None]*((1-t)*grid[b[:, None], a]+t*grid[b[:, None], b]))


def write_image(out, name, values, color=False):
    h, w = values.shape[:2]
    image = bpy.data.images.new(name, width=w, height=h, alpha=True)
    image.colorspace_settings.name = 'sRGB' if color else 'Non-Color'
    image.pixels.foreach_set(np.ascontiguousarray(values, dtype=np.float32).ravel())
    image.filepath_raw = str(out / (name + '.png'))
    image.file_format = 'PNG'
    image.save()
    return image


def rgba(rgb, alpha=1):
    out = np.ones((*rgb.shape[:2], 4), dtype=np.float32)
    out[..., :3] = rgb
    out[..., 3] = alpha
    return out


def surface_material(out, stem, kind):
    broad = noise(SIZE, 5, 713)
    middle = noise(SIZE, 23, 722)
    fine = noise(SIZE, 137, 731)
    grain = noise(SIZE, 461, 740)
    if kind == 'ground':
        # Quiet large patches and many small stone flecks, not uniform high contrast noise.
        value = (broad-.5)*.08 + (middle-.5)*.024 + (grain-.5)*.023
        height = middle*.002 + fine*.0004 + grain*.00010
        # Isolated mineral fragments with varied radius, not thresholded grid noise plateaus.
        rng = np.random.default_rng(19413)
        for _ in range(4200):
            cx, cy = rng.integers(0, SIZE, 2)
            rx, ry = rng.uniform(1.5, 9), rng.uniform(1.5, 7)
            extent = int(math.ceil(max(rx, ry)))
            xx, yy = np.meshgrid(np.arange(-extent, extent+1), np.arange(-extent, extent+1))
            shape = np.clip(1-(xx/rx)**2-(yy/ry)**2, 0, 1)
            indices = np.ix_((np.arange(-extent, extent+1)+cy) % SIZE, (np.arange(-extent, extent+1)+cx) % SIZE)
            value[indices] += shape*rng.uniform(-.05, .045)
            height[indices] += shape*rng.uniform(.0002, .0014)
        rgb = np.array([.415, .365, .298], dtype=np.float32) + value[..., None]
        rgb += (middle-.5)[..., None] * np.array([.018, .009, -.008])
        period = GROUND_PERIOD
    else:
        y = np.arange(SIZE, dtype=np.float32)[:, None] / SIZE
        layers = np.sin(y*math.tau*11 + (middle-.5)*.9)
        seams = np.maximum(layers-.8, 0)*.09
        value = (broad-.5)*.065 + (middle-.5)*.037 + (grain-.5)*.025 - seams
        rgb = np.array([.445, .412, .363], dtype=np.float32) + value[..., None]
        height = middle*.003 + fine*.0005 - seams*.055
        period = 2.0
    dx = (np.roll(height, -1, axis=1)-np.roll(height, 1, axis=1))/(2*period/SIZE)
    dy = (np.roll(height, -1, axis=0)-np.roll(height, 1, axis=0))/(2*period/SIZE)
    normal = np.stack((-dx, -dy, np.ones_like(dx)), axis=-1)
    normal /= np.linalg.norm(normal, axis=-1)[..., None]
    mask = np.zeros((SIZE, SIZE, 4), dtype=np.float32)
    mask[..., 3] = .06 + grain*.07
    images = {
        'Color': write_image(out, stem+'_Color', rgba(np.clip(rgb, 0, 1)), True),
        'Normal': write_image(out, stem+'_Normal', rgba(normal*.5+.5)),
        'Surface': write_image(out, stem+'_Surface', mask),
    }
    mat = bpy.data.materials.new(stem)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes.get('Principled BSDF')
    tex = nodes.new('ShaderNodeTexImage'); tex.image = images['Color']
    links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    tex = nodes.new('ShaderNodeTexImage'); tex.image = images['Normal']
    convert = nodes.new('ShaderNodeNormalMap')
    links.new(tex.outputs['Color'], convert.inputs['Color'])
    links.new(convert.outputs['Normal'], bsdf.inputs['Normal'])
    tex = nodes.new('ShaderNodeTexImage'); tex.image = images['Surface']
    inv = nodes.new('ShaderNodeMath'); inv.operation = 'SUBTRACT'; inv.inputs[0].default_value = 1
    links.new(tex.outputs['Alpha'], inv.inputs[1]); links.new(inv.outputs[0], bsdf.inputs['Roughness'])
    MATERIALS[stem] = mat


def track_material(out):
    w, h = 256, 1024
    u = np.arange(w, dtype=np.float32)[None, :] / w
    v = np.arange(h, dtype=np.float32)[:, None] / h
    variation = noise(h, 29, 332)[:, ::4]
    edge = np.clip(np.minimum(u, 1-u)*14, 0, 1)
    # Five 28 cm treads per tile, slightly staggered left/right; broken edges soften the stamp.
    phase = v*5 + np.abs(u-.5)*.16
    bars = np.clip((np.cos(phase*math.tau)-.15)*2.2, 0, 1)
    alpha = edge * (.055 + bars*.19) * (.5+variation*.5)
    rgb = np.empty((h, w, 3), dtype=np.float32); rgb[:] = [.335, .296, .242]
    tex = write_image(out, 'NW6_Track_Color', rgba(rgb, alpha), True)
    mat = bpy.data.materials.new('NW6_Track'); mat.use_nodes = True
    node = mat.node_tree.nodes.new('ShaderNodeTexImage'); node.image = tex
    bsdf = mat.node_tree.nodes.get('Principled BSDF')
    mat.node_tree.links.new(node.outputs['Color'], bsdf.inputs['Base Color'])
    mat.node_tree.links.new(node.outputs['Alpha'], bsdf.inputs['Alpha'])
    bsdf.inputs['Roughness'].default_value = .96
    MATERIALS['NW6_Track'] = mat


def mesh(name, points, faces, parent, material, uv_mode='rock', bevel=0):
    data = bpy.data.meshes.new(name+'_Mesh')
    data.from_pydata([BASE.point(p) for p in points], [], faces); data.update()
    obj = bpy.data.objects.new(name, data); bpy.context.collection.objects.link(obj)
    obj.parent = parent; data.materials.append(MATERIALS[material])
    bm = bmesh.new(); bm.from_mesh(data)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    if uv_mode in ('ground', 'track') and bm.faces[0].normal.z < 0:
        bmesh.ops.reverse_faces(bm, faces=list(bm.faces))
    bm.to_mesh(data); bm.free()
    if bevel:
        bpy.context.view_layer.objects.active = obj
        mod = obj.modifiers.new('Eroded ledge edges', 'BEVEL')
        mod.width = bevel; mod.segments = 2
        bpy.ops.object.modifier_apply(modifier=mod.name)
        # Narrow undercuts can leave sub-millimetre bevel slivers; weld these before UV/export.
        bm = bmesh.new(); bm.from_mesh(obj.data)
        bmesh.ops.triangulate(bm, faces=list(bm.faces), quad_method='BEAUTY', ngon_method='BEAUTY')
        bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=.0001)
        bmesh.ops.dissolve_degenerate(bm, edges=list(bm.edges), dist=.0001)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bm.to_mesh(obj.data); bm.free()
    data = obj.data
    uv = data.uv_layers.new(name='Metre-scaled surface')
    for face in data.polygons:
        for index in face.loop_indices:
            p = data.vertices[data.loops[index].vertex_index].co
            if uv_mode == 'ground': coord = (p.x/GROUND_PERIOD, p.y/GROUND_PERIOD)
            elif uv_mode == 'track': coord = ((p.x+.65)/1.3, p.y/TRACK_PERIOD)
            elif abs(face.normal.z) > .65: coord = (p.x/2, p.y/2)
            elif abs(face.normal.x) > abs(face.normal.y): coord = (p.y/2, p.z/2)
            else: coord = (p.x/2, p.z/2)
            uv.data[index].uv = coord
    return obj


def terrain_height(x):
    return GROUND_Y + max(0, abs(x)-25)*.025


def terrain(root):
    points = [(x, terrain_height(x), z) for z in range(-40, 41) for x in range(-40, 41)]
    faces = [(z*81+x, z*81+x+1, (z+1)*81+x+1, (z+1)*81+x) for z in range(80) for x in range(80)]
    obj = mesh('NW6_GroundSurface', points, faces, root, 'NW6_Ground', 'ground')
    for face in obj.data.polygons: face.use_smooth = True
    for side, x in [('Left', -5.1), ('Right', 5.1)]:
        track = mesh('NW6_Track'+side, [(-.65, GROUND_Y+.006, -40), (.65, GROUND_Y+.006, -40),
                     (.65, GROUND_Y+.006, 40), (-.65, GROUND_Y+.006, 40)], [(0, 1, 2, 3)], root, 'NW6_Track', 'track')
        track.location.x = x


def rock_geometry(seed, height=1):
    rng = random.Random(seed)
    sides = 16
    angles = [i*math.tau/sides+rng.uniform(-.055, .055) for i in range(sides)]
    outline = [rng.uniform(.79, 1.18) for _ in range(sides)]
    # Paired shelves and undercuts give a sedimentary silhouette, not a rounded primitive.
    rings = [(0, .86), (.06, 1), (.20, 1.02), (.26, .95), (.31, 1.0),
             (.51, .91), (.57, .82), (.63, .88), (.86, .69), (1, .54)]
    points = []
    for j, (y, scale) in enumerate(rings):
        shift = .14*y
        for i, a in enumerate(angles):
            radius = outline[i]*scale*(1+rng.uniform(-.045, .045))
            vertical = (y*(1+.08*math.sin(a+.3*seed)) + (rng.uniform(-.026, .026) if j else 0))*height
            points.append((math.cos(a)*radius+shift, vertical, math.sin(a)*radius*.72))
    faces = [tuple(reversed(range(sides)))]
    for j in range(len(rings)-1):
        for i in range(sides):
            n = (i+1) % sides
            faces.append((j*sides+i, j*sides+n, (j+1)*sides+n, (j+1)*sides+i))
    faces.append(tuple((len(rings)-1)*sides+i for i in range(sides)))
    return points, faces


def scatter_group(root, index, x, z, templates, rng):
    group = BASE.empty('NW6_Scenery_'+str(index).zfill(2), root, (x, terrain_height(x)-.035, z))
    for k in range(2 if index % 3 else 3):
        template = templates[(index+k) % len(templates)]
        rock = bpy.data.objects.new('NW6_LayeredRock', template.data)
        bpy.context.collection.objects.link(rock); rock.parent = group
        size = rng.uniform(.7, 1.3) if k == 0 else rng.uniform(.3, .7)
        rock.scale = (size*rng.uniform(.9, 1.4), size, size)
        rock.location = BASE.point((k*rng.uniform(.8, 1.25), 0, k*rng.uniform(-.8, .8)))
        rock.rotation_euler.z = rng.uniform(-math.pi, math.pi)
        rock.rotation_euler.x = rng.uniform(-.08, .08)
    # Combine stone fragments per moving group, retaining believable density without one Renderer per pebble.
    points, faces = [], []
    for k in range(92):
        a = rng.uniform(0, math.tau); radius = rng.uniform(.9, 5.1)
        px, pz = math.cos(a)*radius, math.sin(a)*radius
        if -8.5 < x+px < 13.5: continue
        size = rng.uniform(.04, .21) if k > 9 else rng.uniform(.23, .42)
        count = 6
        base = len(points)
        angles = [i*math.tau/count for i in range(count)]
        radii = [rng.uniform(.7, 1.1)*size for _ in angles]
        for y, s in [(0, 1), (size*.57, .82), (size*.96, .35)]:
            points.extend((px+math.cos(a)*r*s, y, pz+math.sin(a)*r*s*.8) for a, r in zip(angles, radii))
        for ring in range(2):
            faces.extend((base+ring*count+i, base+ring*count+(i+1)%count,
                          base+(ring+1)*count+(i+1)%count, base+(ring+1)*count+i) for i in range(count))
        faces.append(tuple(base+2*count+i for i in range(count)))
        faces.append(tuple(base+i for i in reversed(range(count))))
    mesh('NW6_Rubble', points, faces, group, 'NW6_Sandstone')
    stones = [o for o in group.children if o.type == 'MESH']
    bpy.ops.object.select_all(action='DESELECT')
    for obj in stones: obj.select_set(True)
    bpy.context.view_layer.objects.active = stones[-1]
    bpy.ops.object.join()
    stones[-1].name = 'NW6_StoneField_'+str(index).zfill(2)
    # Low, muted scrub clumps. Double-sided blades avoid disappearing from the top-down view.
    points, faces = [], []
    for k in range(4):
        cx, cz = rng.uniform(-3, 3), rng.uniform(-3, 3)
        if -8.5 < x+cx < 13.5: continue
        for j in range(9):
            a = rng.uniform(0, math.tau); h = rng.uniform(.14, .36); base = len(points)
            dx, dz = math.cos(a), math.sin(a)
            points.extend([(cx-dz*.014, 0, cz+dx*.014), (cx+dz*.014, 0, cz-dx*.014),
                           (cx+dx*h*.43, h*.8, cz+dz*h*.43), (cx+dx*h*.84, h*.65, cz+dz*h*.84)])
            faces.extend([(base, base+1, base+2), (base+1, base+3, base+2)])
    if faces: mesh('NW6_Scrub', points, faces, group, 'NW6_Scrub')


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--output', required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    out = Path(args.output).resolve(); out.mkdir(parents=True, exist_ok=True)
    (out/'manifest.json').write_text(json.dumps({'version': VERSION, 'status': 'running'}), encoding='utf-8')
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    bpy.context.scene.unit_settings.system = 'METRIC'
    surface_material(out, 'NW6_Ground', 'ground'); print('GROUND_TEXTURES_READY', flush=True)
    surface_material(out, 'NW6_Sandstone', 'rock'); track_material(out)
    scrub = bpy.data.materials.new('NW6_Scrub'); scrub.use_nodes = True
    bsdf = scrub.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Base Color'].default_value = (.23, .205, .13, 1); bsdf.inputs['Roughness'].default_value = .94
    MATERIALS['NW6_Scrub'] = scrub
    root = BASE.empty('NW6_Desert'); terrain(root)
    templates = []
    for i, height in enumerate([1.45, .96, 1.75, 1.15]):
        points, faces = rock_geometry(901+i, height)
        templates.append(mesh('NW6_RockVariant_'+str(i), points, faces, None, 'NW6_Sandstone', bevel=.025))
    rng = random.Random(18307)
    for i in range(16):
        side = -1 if i < 8 else 1
        x = side*(11.5 if i % 2 else 18.8) + (3.5 if side > 0 else 0) + rng.uniform(-.75, .75)
        z = -27 + (i % 8)*7.5 + rng.uniform(-1.5, 1.5)
        scatter_group(root, i, x, z, templates, rng)
    for template in templates: bpy.data.objects.remove(template, do_unlink=True)
    for axis, p in [('Right', (1,0,0)), ('Up', (0,1,0)), ('Forward', (0,0,1))]: BASE.empty('NW6_Axis'+axis, root, p)
    objects = [root] + list(root.children_recursive)
    source = BASE.stats(objects)
    if source['degenerateTriangles']:
        print([(o.name, [(t.index, t.area) for t in o.data.loop_triangles if t.area < 1e-10])
               for o in objects if o.type == 'MESH' and any(t.area < 1e-10 for t in o.data.loop_triangles)], flush=True)
    assert source['degenerateTriangles'] == 0 and source['uvComplete'], source
    assert source['triangles'] < 160000, source['triangles']
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objects: obj.select_set(True)
    fbx = out/'NW6_Desert.fbx'
    bpy.ops.export_scene.fbx(filepath=str(fbx), use_selection=True, object_types={'EMPTY','MESH'},
        apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS', axis_forward='-Z', axis_up='Y',
        use_space_transform=True, bake_space_transform=False, add_leaf_bones=False, bake_anim=False,
        use_mesh_modifiers=True, mesh_smooth_type='FACE', path_mode='AUTO', use_custom_props=True)
    before = set(bpy.data.objects); bpy.ops.import_scene.fbx(filepath=str(fbx))
    imported = set(bpy.data.objects)-before; readback = BASE.stats(imported)
    assert source['triangles'] == readback['triangles'] and readback['uvComplete'] and readback['degenerateTriangles'] == 0
    for key in ('minimumBlender','maximumBlender'):
        assert all(abs(a-b)<.002 for a,b in zip(source[key], readback[key])), key
    assert source['pivots'].keys() == readback['pivots'].keys()
    for key, position in source['pivots'].items():
        assert all(abs(a-b)<.002 for a,b in zip(position, readback['pivots'][key])), key
    for obj in imported: bpy.data.objects.remove(obj, do_unlink=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(out/'NW6_Desert.blend'))
    files = [fbx]+sorted(out.glob('NW6_*.png'))
    record = lambda p: {'file': p.name, 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()}
    report = {'version': VERSION, 'status': 'passed-export', 'sources': [record(Path(__file__)), record(BASE_PATH)],
              'files': [record(p) for p in files], 'source': source, 'roundTrip': readback,
              'textureSize': SIZE, 'groundRepeatMeters': GROUND_PERIOD, 'trackRepeatMeters': TRACK_PERIOD,
              'sceneryLoopMeters': 60, 'sceneryGroupCount': 16, 'normalConvention': 'OpenGL +Y tangent',
              'surfaceChannels': 'R metallic, A smoothness; linear',
              'unityValidated': False, 'manualVisualReviewRequired': True}
    (out/'manifest.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('EXPORT_PASSED '+str(source['triangles']), flush=True)


if __name__ == '__main__': main()
