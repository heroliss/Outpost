"""Reference J form study, independent of production assets.

Blender --background --factory-startup --python-exit-code 1 --python this.py -- --output DIR
Writes a navigable .blend, two renders and a measured manifest. Procedural preview
materials are not a Unity material delivery; no production FBX is overwritten.
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
from mathutils import Vector

BASE_PATH = Path(__file__).with_name("blender_nomad_art_set.py")
SPEC = importlib.util.spec_from_file_location("nomad_forms_base", BASE_PATH)
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)


def material(name, color, metal, roughness):
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*color, 1)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes.get("Principled BSDF")
    bsdf.inputs["Metallic"].default_value = metal
    bsdf.inputs["Roughness"].default_value = roughness
    noise = nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 7
    noise.inputs["Detail"].default_value = 3
    tex = nodes.new("ShaderNodeTexCoord")
    links.new(tex.outputs["Object"], noise.inputs["Vector"])
    ramp = nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = .15
    ramp.color_ramp.elements[0].color = (*(c * .80 for c in color), 1)
    ramp.color_ramp.elements[1].position = .85
    ramp.color_ramp.elements[1].color = (*(c * 1.06 for c in color), 1)
    links.new(noise.outputs["Fac"], ramp.inputs[0])
    links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    fine = nodes.new("ShaderNodeTexNoise")
    fine.inputs["Scale"].default_value = 180
    links.new(tex.outputs["Object"], fine.inputs["Vector"])
    bump = nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = .12
    bump.inputs["Distance"].default_value = .008
    links.new(fine.outputs["Fac"], bump.inputs["Height"])
    links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    BASE.MATS[name] = mat
    return mat


def mesh(name, vertices, faces, mat, parent, bevel=.006):
    obj = BASE.panel(name, vertices, faces, mat, parent)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(obj.data)
    bm.free()
    if bevel:
        mod = obj.modifiers.new("Folded edge radius", "BEVEL")
        mod.width, mod.segments = bevel, 3
        mod = obj.modifiers.new("Weighted panel normals", "WEIGHTED_NORMAL")
        mod.keep_sharp = True
    return obj


def extrude_profile(name, x0, x1, yz, mat, root, bevel=.008):
    """Explicit cross section, extruded along a beam; yz uses game metres."""
    count = len(yz)
    vertices = [(x, y, z) for x in (x0, x1) for y, z in yz]
    faces = [tuple(reversed(range(count))), tuple(range(count, count * 2))]
    faces += [(i, (i + 1) % count, (i + 1) % count + count, i + count) for i in range(count)]
    return mesh(name, vertices, faces, mat, root, bevel)


def octagon(cx, cy, w, h, cut):
    return [(cx + x, cy + y) for x, y in [
        (-w/2+cut, -h/2), (w/2-cut, -h/2), (w/2, -h/2+cut),
        (w/2, h/2-cut), (w/2-cut, h/2), (-w/2+cut, h/2),
        (-w/2, h/2-cut), (-w/2, -h/2+cut)]]


def plate(name, cx, cy, z, w, h, depth, cut, mat, root):
    outline = octagon(cx, cy, w, h, cut)
    vertices = [(x, y, zz) for zz in (z, z+depth) for x, y in outline]
    faces = [tuple(reversed(range(8))), tuple(range(8, 16))]
    faces += [(i, (i+1) % 8, (i+1) % 8+8, i+8) for i in range(8)]
    return mesh(name, vertices, faces, mat, root)


def frame(name, cx, cy, z, w, h, cut, border, root):
    outer = octagon(cx, cy, w, h, cut)
    inner = octagon(cx, cy, w-border*2, h-border*2, max(.008, cut-border*.45))
    vertices = [(x, y, zz) for zz in (z, z+.026) for ring in (outer, inner) for x, y in ring]
    faces = []
    for i in range(8):
        j = (i+1) % 8
        faces += [(i, j, j+8, i+8), (i+16, i+24, j+24, j+16),
                  (i, i+16, j+16, j), (i+8, j+8, j+24, i+24)]
    return mesh(name, vertices, faces, "Steel", root, .003)


def bolt(x, y, z, root):
    BASE.cylinder("Recessed hex fastener", (x,y,z), .018, .012, "Steel", root, "z", 6)


def edge_module(root):
    # Formed perimeter beam, two lips and sloped underside; no cuboid chassis.
    extrude_profile("Pressed perimeter beam", -2.1, 2.1,
                    [(.03,0), (-.57,0), (-.77,.14), (-.84,.43), (-.64,.70), (.03,.70)], "Hull", root, .018)
    extrude_profile("Upper rolled channel", -2.12, 2.12,
                    [(.075,-.025), (.045,-.06), (-.015,-.06), (-.025,.12), (.04,.12), (.075,.08)], "Steel", root)
    extrude_profile("Lower sacrificial rub rail", -2.10, 2.10,
                    [(-.61,-.04), (-.67,-.045), (-.80,.13), (-.80,.22), (-.74,.22)], "Dark", root)
    for i in range(4):
        for j in range(3):
            BASE.box("Interlocking deck plate", (-1.56+i*1.04,.04,.62+j*.77),
                     (1.02,.07,.75), "Deck" if (i+j)%3 else "DeckPatch", root, .006)
    for cx in (-1.07, 1.07):
        plate("Hatch shadow recess", cx,-.32,-.009,1.72,.44,.025,.075,"Dark",root)
        plate("Pressed access door", cx,-.32,-.025,1.59,.34,.016,.052,"Hull",root)
        frame("Continuous hatch lip",cx,-.32,-.040,1.73,.45,.078,.035,root)
        for bx in (cx-.70,cx+.70):
            for by in (-.46,-.19): bolt(bx,by,-.052,root)
        for bx in (cx-.56,cx+.56):
            BASE.cylinder("Hinge pin",(bx,-.12,-.055),.020,.14,"Steel",root,"x",12)
        plate("Latch cup",cx,-.32,-.057,.24,.105,.022,.025,"Dark",root)
        BASE.box("Flush latch grip",(cx,-.315,-.073),(.13,.018,.025),"Steel",root,.004)
    # Low bulwark with sloped corners, separate cap and inset panels.
    for cx in (-1.50,-.50,.50,1.50):
        plate("Clipped bulwark panel",cx,.39,.25,.975,.66,.055,.09,"Hull",root)
        plate("Recessed bulwark field",cx,.40,.238,.79,.46,.012,.066,"Teal",root)
        for bx in (cx-.33,cx+.33): bolt(bx,.56,.225,root)
        extrude_profile("Bent bulwark cap",cx-.49,cx+.49,
                        [(.76,.21),(.78,.23),(.78,.34),(.72,.37),(.72,.33),(.745,.32),(.745,.25),(.73,.23)],"Steel",root,.004)
    for cx in (-2.04,0,2.04):
        extrude_profile("Sloped reinforcement rib",cx-.045,cx+.045,
                        [(.04,.14),(.72,.23),(.73,.34),(.04,.44)],"Dark",root)
    # Safety accents are small fittings, not full bright rails.
    for cx in (-1.82,1.82):
        BASE.pipe("Folded grab loop",[(cx-.10,.64,.12),(cx-.10,.90,.12),(cx+.10,.90,.12),(cx+.10,.64,.12)],.018,"Ochre",root)
    for cx in (-1.35,1.35):
        BASE.box("Tie down socket",(cx,.086,1.08),(.24,.014,.10),"Dark",root,.008)
        BASE.pipe("Recessed tie down ring",[(cx-.06,.09,1.08),(cx-.04,.117,1.08),(cx+.04,.117,1.08),(cx+.06,.09,1.08)],.008,"Steel",root)


def stair_tread(name, x0, x1, level, front, direction, root):
    # Folded steel section with downturned nosing and rear stiffener.
    yz = [(level,front),(level,front+direction*.30),(level-.055,front+direction*.30),
          (level-.055,front+direction*.278),(level-.022,front+direction*.278),
          (level-.022,front+direction*.024),(level-.070,front+direction*.024),(level-.070,front)]
    extrude_profile(name,x0,x1,yz,"Deck",root,.003)
    for dz in (.035,.09,.15,.21):
        BASE.box("Stamped anti slip rib",((x0+x1)*.5,level+.002,front+direction*dz),
                 (x1-x0-.11,.004,.008),"Steel",root,.001)


def switchback(root):
    width, gap, rise, tread = 1.65, .22, 3.2/18, .30
    centers = [-(width+gap)*.5, (width+gap)*.5]
    landing_depth, run = 1.65, 2.7
    for lane in range(2):
        center, direction = centers[lane], 1 if lane == 0 else -1
        for i in range(9):
            level = (i+1)*rise if lane == 0 else 1.6+(i+1)*rise
            front = i*tread if lane == 0 else run-i*tread
            stair_tread(f"Flight {lane+1} tread {i+1:02d}",center-width*.5,center+width*.5,level,front,direction,root)
        for side in (-1,1):
            x = center+side*(width*.5+.025)
            z0,z1 = (0,run) if lane == 0 else (run,0)
            y0,y1 = (0,1.6) if lane == 0 else (1.6,3.2)
            # Match each real tread underside; a diagonal top leaves the plates floating.
            notches = []
            for i in range(9):
                y = y0+(i+1)*rise-.022
                notches.extend([(y,z0+direction*i*tread),(y,z0+direction*(i+1)*tread)])
            extrude_profile("Laser cut stringer",x-.022,x+.022,
                            notches+[(y1-.33,z1),(y0-.13,z0)],"Hull",root,.006)
            for i in range(4):
                t = i/3
                y,z = y0+(y1-y0)*t,z0+(z1-z0)*t
                BASE.cylinder("Handrail upright",(x,y+.51,z),.018,1.02,"Dark",root,sides=12)
            BASE.pipe("Continuous flight handrail",[(x,y0+1.04,z0),(x,y1+1.04,z1)],.024,"Steel",root)
    BASE.box("Full carrier turning landing",(0,1.56,run+landing_depth*.5),
             (width*2+gap,.08,landing_depth),"DeckPatch",root,.012)
    for x in (-1.79,1.79):
        for z in (run+.07,run+landing_depth-.06):
            BASE.cylinder("Landing support",(x,.78,z),.043,1.56,"Dark",root,sides=12)
            BASE.cylinder("Landing guard post",(x,2.11,z),.018,1.02,"Dark",root,sides=12)
        BASE.pipe("Landing side rail",[(x,2.64,run),(x,2.64,run+landing_depth)],.024,"Steel",root)
    BASE.pipe("Landing back rail",[(-1.79,2.64,run+landing_depth),(1.79,2.64,run+landing_depth)],.024,"Steel",root)
    # Full current carrier envelope on the turning platform: a diagnostic outline.
    points = [(math.cos(a)*.67,1.608,run+landing_depth*.5+math.sin(a)*.67) for a in [i*math.tau/64 for i in range(65)]]
    BASE.pipe("1.34 m carrier turning envelope (diagnostic)",points,.007,"Ochre",root)
    return {"height":3.2,"risers":18,"riserHeight":rise,"treadDepth":tread,
            "clearWidthPerFlight":width,"landingDepth":landing_depth,"carrierRadius":.67,
            "treadAndLandingBoundsXZ":[width*2+gap,run+landing_depth],
            "guardOuterBoundsXZ":[3.62,4.40],"straightTreadBoundsXZ":[1.65,5.4],
            "clearanceValidation":"geometry dimensions only; Unity traversal pending",
            "tradeoff":"Shorter long edge, wider footprint; does not reduce total occupied floor area."}


def render(name, root, all_roots, camera, look, size, out):
    for other in all_roots:
        for obj in [other]+list(other.children_recursive): obj.hide_render = other != root
    scene = bpy.context.scene
    scene.camera.location = BASE.point(camera)
    scene.camera.rotation_euler = (BASE.point(look)-scene.camera.location).to_track_quat('-Z','Y').to_euler()
    scene.camera.data.ortho_scale = size
    scene.render.filepath = str(out / (name+'.png'))
    bpy.ops.render.render(write_still=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output",required=True)
    out = Path(parser.parse_args(sys.argv[sys.argv.index('--')+1:]).output).resolve()
    out.mkdir(parents=True,exist_ok=True)
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene
    scene.unit_settings.system = 'METRIC'
    for args in [
        ('Hull',(.38,.37,.315),.48,.55),('Dark',(.072,.079,.077),.55,.63),
        ('Steel',(.38,.39,.355),.72,.38),('Deck',(.185,.171,.142),.40,.72),
        ('DeckPatch',(.215,.195,.158),.38,.70),('Teal',(.155,.225,.207),.27,.64),
        ('Ochre',(.65,.365,.085),.20,.63),('Ground',(.19,.174,.145),0,.95)]: material(*args)
    edge = BASE.empty('NW5 Reference J perimeter study')
    edge_module(edge)
    stairs = BASE.empty('NW5 Carrier switchback dimensional study')
    dimensions = switchback(stairs)
    ground = BASE.box('Preview ground',(0,-1.02,0),(200,.10,200),'Ground',None,0)
    bpy.ops.object.camera_add()
    scene.camera = bpy.context.object
    scene.camera.data.type = 'ORTHO'
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 32
    scene.cycles.use_denoising = True
    scene.render.resolution_x,scene.render.resolution_y = 1440,1000
    scene.render.resolution_percentage = 100
    scene.world.color = (.22,.24,.26)
    bpy.ops.object.light_add(type='AREA',location=(-3,-4,8))
    bpy.context.object.data.energy = 1500
    bpy.context.object.data.shape = 'DISK'
    bpy.context.object.data.size = 7
    bpy.context.object.data.color = (1,.92,.80)
    bpy.context.object.rotation_euler = (Vector((0,1,0))-bpy.context.object.location).to_track_quat('-Z','Y').to_euler()
    bpy.ops.object.light_add(type='AREA',location=(4,4,6))
    bpy.context.object.data.energy = 850
    bpy.context.object.data.size = 6
    bpy.context.object.data.color = (.82,.90,1)
    bpy.context.object.rotation_euler = (Vector((0,1,1))-bpy.context.object.location).to_track_quat('-Z','Y').to_euler()
    scene.view_settings.view_transform = 'AgX'
    render('reference-edge-study',edge,[edge,stairs],(5.7,3.7,-6), (0,-.05,.7),6.2,out)
    ground.location.z = -.19
    render('switchback-dimensions',stairs,[edge,stairs],(6.5,6,-7), (0,1.6,2.0),7.4,out)
    scene.render.filepath = str(out/'switchback-dimensions.png')
    # Separate collections allow either overlapping local-origin specimen to be inspected.
    for root in (edge,stairs):
        collection = bpy.data.collections.new(root.name)
        scene.collection.children.link(collection)
        for obj in [root]+list(root.children_recursive):
            for old in list(obj.users_collection): old.objects.unlink(obj)
            collection.objects.link(obj)
        collection.hide_viewport = root == edge
        collection.hide_render = root == edge
    bpy.ops.wm.save_as_mainfile(filepath=str(out/'Nomad_ReferenceFormStudy.blend'))
    report = {"version":"0.1.1","status":"preview-rendered","sourceSha256":hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "baseGeneratorSha256":hashlib.sha256(BASE_PATH.read_bytes()).hexdigest(),"units":"metres","stairs":dimensions,
              "productionAssetsChanged":False,"unityValidated":False,"materialsBaked":False,
              "images":["reference-edge-study.png","switchback-dimensions.png"]}
    (out/'manifest.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__': main()
