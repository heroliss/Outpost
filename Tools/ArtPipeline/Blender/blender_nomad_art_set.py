"""Nomad first playable art set. Unity-facing dimensions, semantic moving parts.

Run with Blender --background --factory-startup --python-exit-code 1 --python this.py -- --output DIR.
Writes only that output directory. No add-ons or user preferences are required.
FBX files are independently re-imported before a manifest can report success.
"""
import argparse
import hashlib
import json
import math
import random
import sys
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector

VERSION = "0.6.1"
PALETTE = {
    "SandPaint": ((0.68, 0.52, 0.30), 0.12, 0.57),
    "Deck": ((0.28, 0.255, 0.215), 0.36, 0.77),
    "DeckPatch": ((0.34, 0.31, 0.255), 0.26, 0.82),
    "Terracotta": ((0.55, 0.22, 0.12), 0.0, 0.86),
    "Ivory": ((0.84, 0.78, 0.60), 0.04, 0.63),
    "Teal": ((0.115, 0.26, 0.24), 0.16, 0.56),
    "Orange": ((0.83, 0.29, 0.095), 0.08, 0.52),
    "Frame": ((0.105, 0.145, 0.15), 0.67, 0.49),
    "Steel": ((0.43, 0.49, 0.46), 0.78, 0.32),
    "Rubber": ((0.045, 0.060, 0.060), 0.0, 0.88),
    "Canvas": ((0.65, 0.47, 0.27), 0.0, 0.91),
    "Wood": ((0.34, 0.20, 0.105), 0.0, 0.80),
    "Glass": ((0.13, 0.31, 0.34), 0.23, 0.18),
    "Water": ((0.22, 0.67, 0.73), 0.0, 0.24),
    "Lamp": ((1.0, 0.63, 0.20), 0.0, 0.25),
    "Rock": ((0.44, 0.325, 0.225), 0.0, 0.95),
    "RockLayer": ((0.38, 0.29, 0.21), 0.0, 0.98),
    "Sand": ((0.69, 0.49, 0.29), 0.0, 0.98),
    "Ground": ((0.47, 0.355, 0.24), 0.0, 0.96),
    "Foliage": ((0.18, 0.24, 0.10), 0.0, 0.94),
    "RugCanvas": ((0.31, 0.23, 0.16), 0.0, 0.95),
}
MATS = {}


def point(p):
    # Unity's FBX importer changes handedness. Authoring +Z must remain gameplay +Z.
    return Vector((p[0], p[2], p[1]))


def empty(name, parent=None, p=(0, 0, 0)):
    o = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(o)
    o.parent = parent
    o.location = point(p)
    o["nomad_part_id"] = name
    return o


def finish(o, name, mat, parent, bevel=0):
    o.name = name
    o.data.name = name + "_Mesh"
    if bevel:
        mod = o.modifiers.new("Manufactured edge", "BEVEL")
        mod.width, mod.segments = bevel, 2
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.modifier_apply(modifier=mod.name)
        for face in o.data.polygons:
            face.use_smooth = True
    o.data.materials.append(MATS[mat])
    o.parent = parent
    return o


def box(name, p, s, mat, parent, bevel=.025):
    bpy.ops.mesh.primitive_cube_add(location=point(p))
    o = bpy.context.object
    o.dimensions = (s[0], s[2], s[1])
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return finish(o, name, mat, parent, min(bevel, min(s) * .22))


def cylinder(name, p, radius, length, mat, parent, axis="y", sides=20):
    bpy.ops.mesh.primitive_cylinder_add(vertices=sides, radius=radius, depth=length, location=point(p))
    o = bpy.context.object
    if axis == "x":
        o.rotation_euler.y = math.pi / 2
    elif axis == "z":
        o.rotation_euler.x = math.pi / 2
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(o, name, mat, parent, min(.012, radius * .15, length * .2))


def pipe(name, points, radius, mat, parent):
    curve = bpy.data.curves.new(name, "CURVE")
    curve.dimensions, curve.resolution_u = "3D", 8
    curve.bevel_depth, curve.bevel_resolution = radius, 2
    spline = curve.splines.new("BEZIER")
    spline.bezier_points.add(len(points) - 1)
    for bp, p in zip(spline.bezier_points, points):
        bp.co = point(p)
        bp.handle_left_type = bp.handle_right_type = "AUTO"
    o = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(o)
    bpy.ops.object.select_all(action="DESELECT")
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.convert(target="MESH")
    return finish(bpy.context.object, name, mat, parent)


def panel(name, points, faces, mat, parent):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([point(p) for p in points], [], faces)
    mesh.update()
    o = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(o)
    return finish(o, name, mat, parent)


def vehicle(root):
    box("Chassis", (0, -.65, 0), (10.8, .8, 8.4), "Frame", root, .15)
    for x in [-4.1, 4.1]:
        box("Longitudinal beam", (x, -1.05, 0), (.38, .65, 8.8), "Frame", root)
    for ix in range(9):
        for iz in range(7):
            box("Deck cassette", (-4.8 + ix * 1.2, -.06, -3.6 + iz * 1.2),
                (1.18, .10, 1.18), "Deck" if (ix + iz) % 7 else "DeckPatch", root, .012)
    for x in [-5.34, 5.34]:
        box("Rim rail", (x, .03, 0), (.14, .18, 8.4), "Steel", root)
        for z in [-3.45, 3.45]:
            for zz in [z - .6, z + .6]:
                box("Stanchion", (x, .48, zz), (.10, .92, .10), "Frame", root)
            box("Handrail", (x, .93, z), (.105, .10, 1.3), "Orange", root)
    # Four chassis-mounted tracked pods leave the deck boundary independent of wheels.
    for side in [-1, 1]:
        for i, z in enumerate([-2.35, 2.35]):
            pod = empty(f"TrackPod_{side}_{i}", root, (side * 5.75, -1.56, z))
            track_pod(pod, side)
            box("Pod suspension", (side * 5.24, -1.08, z), (1.18, .44, 2.3), "Frame", root)
            box("Mudguard", (side * 5.83, -.63, z), (1.35, .20, 3.58), "Teal", root, .07)
    # +Z is the direction the existing driving station faces.
    box("Bow", (0, -.3, 4.55), (10.0, .95, .80), "Ivory", root, .17)
    box("Front bumper", (0, -.95, 4.93), (10.6, .28, .30), "Frame", root, .06)
    box("Front intake", (0, -.28, 5.0), (3.7, .44, .10), "Rubber", root)
    for x in [-1.5, -1, -.5, 0, .5, 1, 1.5]:
        box("Intake fin", (x, -.28, 5.08), (.055, .39, .08), "Steel", root, .008)
    for x in [-4.1, 4.1]:
        box("Headlight housing", (x, -.22, 5.02), (.95, .45, .16), "Frame", root)
        for xx in [-.24, .24]:
            cylinder("Headlight", (x + xx, -.22, 5.13), .14, .04, "Lamp", root, "z")
    for x in [-3.8, 3.8]:
        box("Rear locker", (x, -.28, -4.28), (2.3, .82, .43), "Teal", root, .05)
        box("Locker pull", (x, -.15, -4.54), (.35, .07, .07), "Steel", root)
    # A low windscreen frames the existing driver console without obscuring the deck.
    for x in [2.9, 4.65]:
        box("Windscreen upright", (x, 1.06, 4.18), (.10, 1.60, .10), "Frame", root)
    box("Windscreen glass", (3.77, 1.10, 4.18), (1.66, 1.40, .04), "Glass", root)
    box("Sun visor", (3.77, 1.88, 4.04), (2.1, .14, .68), "Ivory", root)
    # Side storage, tow eyes, ribs and service piping convey assembly rather than noise.
    for z in [-3.2, 3.2]:
        cylinder("Tow eye", (0, -.62, z * 1.5), .19, .12, "Orange", root, "z")
    pipe("Side service line", [(-5.43, -.58, -3.6), (-5.5, -.55, 0), (-5.43, -.58, 3.6)], .045, "Steel", root)
    box("Woven runner", (0, .001, -.7), (1.5, .009, 2.2), "RugCanvas", root, 0)
    for x in [-.67,.67]:
        box("Runner border", (x,.007,-.7), (.065,.003,2.08), "Teal", root, 0)
    for z in [-1.67,-1.52,.12,.27]:
        box("Woven warm stripe", (0,.007,z), (1.30,.003,.045), "Terracotta", root, 0)
    for z in [-3.3,3.3]:
        box("Rail herb box", (-5.48,.76,z), (.38,.32,.64), "Wood", root)
        box("Soil", (-5.48,.925,z), (.32,.012,.57), "Frame", root, 0)
        for j in range(9):
            a = j*math.tau/9
            panel("Herb leaves", [(-5.48,.93,z), (-5.48+math.cos(a)*.12,1.14,z+math.sin(a)*.14),
                (-5.48+math.cos(a+.3)*.29,1.17,z+math.sin(a+.3)*.29),
                (-5.48+math.cos(a+.4)*.10,1.03,z+math.sin(a+.4)*.10)],
                [(0,1,2),(0,2,3)], "Foliage", root)
    for i, (x,z) in enumerate([(-5.34,3.45),(5.34,-3.45)]):
        lantern = empty("WorkLamp_"+str(i), root, (x,1.12,z))
        cylinder("Lantern base", (0,-.12,0), .095, .04, "Frame", lantern)
        cylinder("Warm glass", (0,0,0), .07, .18, "Lamp", lantern, sides=12)
        cylinder("Lantern cap", (0,.12,0), .10, .055, "Frame", lantern)


def track_pod(root, side):
    profile = []
    for center, start in [(.93, -math.pi / 2), (-.93, math.pi / 2)]:
        for i in range(17):
            a = start + math.pi * i / 16
            profile.append((math.sin(a), center, math.cos(a)))
    points = []
    for x, radius in [(-.5, .67), (.5, .67), (-.5, .53), (.5, .53)]:
        points += [(x, sy * radius, center + cz * radius) for sy, center, cz in profile]
    n = len(profile)
    faces = []
    for i in range(n):
        j = (i + 1) % n
        for a, b in [(0, 1), (1, 3), (3, 2), (2, 0)]:
            faces.append((a*n+i, a*n+j, b*n+j, b*n+i))
    panel("Track belt", points, faces, "Rubber", root)
    # Equal arc-length shoes keep the authored spacing when driven by travelled distance.
    radius, half_straight = .7, .93
    loop_length = 4*half_straight + math.tau*radius
    for i in range(42):
        distance = loop_length*i/42
        if distance < 2*half_straight:
            y, z, angle = radius, -half_straight+distance, 0
        elif distance < 2*half_straight+math.pi*radius:
            a = (distance-2*half_straight)/radius
            y, z, angle = radius*math.cos(a), half_straight+radius*math.sin(a), a
        elif distance < 4*half_straight+math.pi*radius:
            y, z, angle = -radius, half_straight-(distance-2*half_straight-math.pi*radius), math.pi
        else:
            a = (distance-4*half_straight-math.pi*radius)/radius
            y, z, angle = -radius*math.cos(a), -half_straight-radius*math.sin(a), math.pi+a
        shoe = empty(f"{root.name}_Shoe_{i:02d}", root, (0,y,z))
        shoe.rotation_euler.x = -angle
        box("Track shoe", (0,0,0), (1.10,.09,.17), "Frame", shoe, .014)
    for z in [-.7, 0, .7]:
        wheel = empty(f"{root.name}_Wheel_{z}", root, (0, -.05, z))
        cylinder("Road wheel", (0, 0, 0), .44, .92, "Rubber", wheel, "x", 20)
        cylinder("Road wheel hub", (side*.485, 0, 0), .30, .05, "Ivory", wheel, "x", 16)
        cylinder("Axle bolt", (side*.52, 0, 0), .10, .08, "Orange", wheel, "x", 12)
    box("Bogie side beam", (side*.54, -.04, 0), (.12, .22, 1.85), "Teal", root)


def tank(root):
    box("Tank cradle", (0, .22, 0), (1.72, .35, .90), "Frame", root)
    cylinder("Reservoir", (0, .87, 0), .43, 1.62, "Teal", root, "x", 32)
    for x in [-.52, .52]:
        cylinder("Retaining strap", (x, .87, 0), .451, .085, "Ivory", root, "x", 32)
    cylinder("Filler", (.5, 1.35, 0), .14, .12, "Frame", root)
    lid = empty(root.name + "_Work_FillLid", root, (.5, 1.42, .14))
    cylinder("Orange filler cap", (0, .02, -.14), .16, .07, "Orange", lid)
    empty(root.name + "_Work_Inlet", root, (.5, 1.405, 0))
    pipe("Water outlet", [(-.48, .62, -.26), (-.48, .44, -.51), (-.48, .34, -.56)], .035, "Steel", root)
    valve = empty(root.name + "_Work_Valve", root, (-.48, .64, -.51))
    cylinder("Valve spindle", (0, 0, 0), .045, .1, "Steel", valve, "z")
    bpy.ops.mesh.primitive_torus_add(major_segments=24, minor_segments=8,
                                  location=point((0, 0, -.065)), major_radius=.105, minor_radius=.017)
    bpy.context.object.rotation_euler.x = math.pi / 2
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    finish(bpy.context.object, "Valve wheel", "Orange", valve)
    box("Valve horizontal spoke", (0, 0, -.065), (.19, .025, .025), "Orange", valve, .004)
    box("Valve vertical spoke", (0, 0, -.065), (.025, .19, .025), "Orange", valve, .004)
    box("Valve position mark", (.073, .013, -.085), (.035, .04, .012), "Ivory", valve, .002)
    pipe("Fill hose riser", [(-.48, .62, -.32), (-.64, .91, -.44), (-.50, 1.17, -.49)], .027, "Steel", root)
    cylinder("Hose coupling", (-.50, 1.17, -.53), .045, .08, "Frame", root, "z")
    empty(root.name + "_Work_Outlet", root, (-.50, 1.17, -.58))
    box("Service plinth", (.55, .20, -.30), (.46, .12, .36), "Ivory", root)
    box("Readable gauge", (.12, .88, -.438), (.24, .32, .025), "Frame", root)
    box("Gauge strip", (.12, .9, -.46), (.09, .22, .02), "Lamp", root)
    cylinder("Warning lamp socket", (-.57, 1.345, .12), .085, .055, "Frame", root)
    indicator = empty(root.name + "_Work_ConditionIndicator", root, (-.57, 1.43, .12))
    cylinder("Warning lamp lens", (0, 0, 0), .065, .12, "Lamp", indicator, sides=16)
    cylinder("Warning lamp cap", (-.57, 1.50, .12), .078, .025, "Frame", root)
    box("Rear service cavity", (0, .82, .425), (.84, .57, .11), "Frame", root)
    for x in [-.24, .13]:
        pipe("Service valve plumbing", [(x, .61, .49), (x, .84, .51), (x+.12, 1.01, .49)], .022, "Steel", root)
        cylinder("Valve cartridge", (x, .80, .51), .065, .08, "Orange", root, "z", 12)
    # A sliding cover leaves both rear service slots clear of a door swing.
    for x in [-.45, .45]:
        box("Service cover guide", (x, 1.07, .52), (.035, 1.14, .075), "Steel", root, .005)
    service = empty(root.name + "_Work_ServiceDoor", root, (0, .82, .52))
    box("Service hatch", (0, 0, 0), (.86, .59, .045), "Ivory", service, .012)
    box("Hatch inset", (0, 0, .025), (.73, .43, .015), "Teal", service, .01)
    box("Hatch pull", (0, -.18, .055), (.28, .045, .045), "Orange", service, .006)


def dispenser(root):
    box("Dispenser cabinet", (0, .40, 0), (.74, .78, .62), "Ivory", root, .045)
    box("Front panel", (0, .5, -.32), (.63, .57, .035), "Teal", root)
    box("Cup recess", (0, .66, -.345), (.43, .29, .05), "Frame", root)
    box("Drip shelf", (0, .50, -.36), (.50, .04, .14), "Steel", root)
    for x in [-.15, .15]:
        cylinder("Tap", (x, .76, -.37), .035, .10, "Steel", root, "z")
    tap = empty(root.name + "_Work_Valve", root, (0, .78, -.40))
    box("Tap selector bar", (0, .02, -.025), (.38, .04, .065), "Orange", tap)
    lid = empty(root.name + "_Work_FillLid", root, (0, .81, .30))
    box("Top lid", (0, .05, -.30), (.79, .1, .66), "Teal", lid)
    box("Lid pull", (0, .11, -.51), (.20, .035, .055), "Orange", lid)
    cylinder("Refill mouth", (0, .805, .04), .16, .025, "Frame", root)
    empty(root.name + "_Work_Inlet", root, (0, .83, .04))
    for x in [-.24, -.12, 0, .12, .24]:
        box("Base ventilation", (x, .20, -.35), (.025, .15, .015), "Frame", root, .003)


def toilet(root):
    box("Sanitation base", (0, .17, 0), (.78, .30, .86), "Teal", root)
    box("Seat housing", (0, .48, .04), (.66, .40, .66), "Ivory", root, .09)
    cylinder("Seat rim", (0, .70, -.02), .26, .065, "Frame", root, sides=24)
    cylinder("Seat opening", (0, .742, -.02), .185, .02, "Rubber", root, sides=24)
    box("Cistern", (0, .93, .29), (.66, .42, .17), "Teal", root)
    box("Flush handle", (.22, 1.12, .18), (.13, .06, .08), "Orange", root)
    for x in [-.37, .37]:
        pipe("Privacy rail", [(x, .15, .34), (x, 1.70, .34), (x, 1.70, -.30)], .025, "Frame", root)
        # Side screens keep the front interaction and detachable bucket readable.
        box("Canvas screen", (x, 1.12, .03), (.025, 1.05, .66), "Canvas", root, .004)
    box("Back canvas", (0, 1.20, .355), (.73, .90, .025), "Canvas", root, .004)


def easel(root):
    for x in [-.5, .5]:
        o = box("Easel leg", (x, .68, 0), (.065, 1.35, .12), "Wood", root)
        o.rotation_euler.y = x * .20
    box("Canvas stretcher", (0, 1.14, 0), (1.12, .86, .10), "Wood", root)
    box("Painting", (0, 1.16, -.06), (1.00, .73, .025), "Ivory", root)
    box("Landscape sky", (0, 1.31, -.08), (.94, .36, .012), "Teal", root, 0)
    box("Landscape dune", (0, 1.02, -.08), (.94, .24, .012), "SandPaint", root, 0)
    cylinder("Painted sun", (.24, 1.33, -.092), .095, .008, "Orange", root, "z")
    box("Paint shelf", (0, .77, -.13), (1.2, .07, .20), "Wood", root)
    for i, m in enumerate(["Orange", "Teal", "Ivory"]):
        cylinder("Paint pot", (-.30 + i * .23, .85, -.13), .055, .09, m, root, sides=12)


def console(root):
    box("Console foot", (0, .30, 0), (.78, .58, .53), "Teal", root)
    box("Dashboard", (0, .72, 0), (1.0, .23, .63), "Ivory", root)
    box("Instruments", (0, .86, -.14), (.8, .045, .34), "Frame", root)
    for x in [-.25, 0, .25]:
        cylinder("Gauge", (x, .889, -.14), .078, .015, "Glass", root)
    pipe("Steering column", [(0, .45, -.18), (0, .84, -.34)], .035, "Steel", root)
    bpy.ops.mesh.primitive_torus_add(major_segments=24, minor_segments=8,
                                  location=point((0, .92, -.40)), major_radius=.20, minor_radius=.026)
    finish(bpy.context.object, "Steering wheel", "Frame", root)
    pipe("Control lever", [(.38, .63, -.24), (.38, .96, -.25)], .024, "Steel", root)
    cylinder("Lever grip", (.38, 1.0, -.25), .055, .10, "Orange", root)


def rock(root):
    rng = random.Random(7129)
    for i, (x, y, z, sx, sy, sz) in enumerate([(0, 1.1, 0, 3.6, 2.2, 2.2), (1.2, .55, .7, 2, 1.1, 1.8), (-1, .3, -1, 1.7, .6, 1.2)]):
        points, faces = [], []
        rings = [(0,.75),(.18,1),(.39,.93),(.42,.91),(.73,.79),(.90,.62),(1,.34)]
        sides = 9
        angles = [j*math.tau/sides + rng.uniform(-.08,.08) for j in range(sides)]
        for k,(h,radius) in enumerate(rings):
            for a in angles:
                r = radius*rng.uniform(.89,1.05)
                points.append((x+math.cos(a)*sx*.5*r+h*.11,
                    y-sy*.5+sy*h, z+math.sin(a)*sz*.5*r))
        for k in range(len(rings)-1):
            for j in range(sides):
                a,b=k*sides+j,k*sides+(j+1)%sides
                faces.append((a,b,b+sides,a+sides))
        faces += [tuple(range(sides-1,-1,-1)), tuple((len(rings)-1)*sides+j for j in range(sides))]
        o = panel(f"Sandstone_{i}", points, faces, "Rock", root)
        o.data.materials.append(MATS["RockLayer"])
        for polygon in o.data.polygons:
            polygon.material_index = 1 if polygon.index//sides==2 else 0


def stop(root):
    # The stable water point is at the local origin. Apron navigation remains owned by Foundation.
    box("Raised loading dock", (-1, -.14, 0), (6.0, .26, 4.0), "Wood", root)
    for x in [-3.6, -.8, 1.6]:
        for z in [-1.65, 1.65]:
            box("Dock pier", (x, -1.25, z), (.23, 2.25, .23), "Frame", root)
    for i in range(20):
        box("Dock board", (-3.85+i*.3, .002, 0), (.28, .04, 4), "Canvas" if i%6==0 else "Wood", root, .008)
    for x in [-1.4, 1.5]:
        cylinder("Awning pole", (x, 1.4, 1.6), .065, 2.8, "Frame", root)
        cylinder("Pole collar", (x, .25, 1.6), .12, .12, "Orange", root)
    panel("Canvas awning", [(-1.6, 2.8, 1.8), (1.7, 2.8, 1.8), (1.7, 2.45, -.1), (-1.6, 2.45, -.1),
                            (-1.6, 2.74, 1.8), (1.7, 2.74, 1.8), (1.7, 2.39, -.1), (-1.6, 2.39, -.1)],
          [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (3, 2, 6, 7), (0, 3, 7, 4), (1, 5, 6, 2)], "Canvas", root)
    cylinder("Station water tank", (0, .65, .9), .50, 1.1, "Ivory", root)
    cylinder("Water tank band", (0, .65, .9), .512, .14, "Teal", root)
    pipe("Station tap", [(0, .48, .42), (0, .48, .34), (0, .31, .3)], .045, "Steel", root)
    cylinder("Receiving tank", (0, .45, -1.6), .44, .82, "Teal", root)
    cylinder("Receiving lid", (0, .90, -1.6), .46, .10, "Orange", root)
    box("Supplies table", (1.2, .64, 1.35), (1.1, .12, .55), "Wood", root)
    for x in [.75, 1.65]:
        box("Table legs", (x, .30, 1.35), (.08, .6, .45), "Frame", root)
    box("Waystation sign", (-1.35, 2.00, 1.57), (.75, .35, .06), "Teal", root)


def desert(root):
    rng = random.Random(19722)
    points = []
    faces = []
    size = 41
    for z in range(size):
        for x in range(size):
            xx, zz = -40+x*2, -40+z*2
            height = -2.33 + max(0, min(1, (abs(xx)-23)/9)) * (
                .6 + .55*math.sin(xx*.18+zz*.09) + .32*math.cos(zz*.28-xx*.14))
            points.append((xx, height, zz))
    for z in range(size-1):
        for x in range(size-1):
            a = z*size+x
            faces.extend([(a, a+1, a+size+1), (a, a+size+1, a+size)])
    ground = panel("Wind-carved terrain", points, faces, "Ground", root)
    ground["nomad_upward_surface"] = True
    for polygon in ground.data.polygons:
        polygon.use_smooth = True
    for i, (x,z,scale) in enumerate([(-13,-8,1.8), (-12,10,2.1), (16,12,1.6), (19,18,2.2),
                                  (13,-13,1.3), (-18,-19,2.1), (-14,-20,1.6), (-20,5,2.5)]):
        cluster = empty("Desert outcrop_"+str(i), root, (x,-2.43,z))
        rock(cluster)
        cluster.scale = (scale, scale, scale)
    for i in range(85):
        x,z = rng.uniform(-20,20),rng.uniform(-20,20)
        if abs(x)<7.1 and abs(z)<6.5: continue
        if 6<x<13 and abs(z)<3: continue
        scale = rng.uniform(.07,.32)
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=1, location=point((x,-2.28,z)))
        pebble = bpy.context.object
        pebble.scale = (scale*1.4,scale,scale*.65)
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        finish(pebble,"Scattered sandstone","Rock",root)
        if i%3: continue
        for j in range(5):
            a = rng.random()*math.tau
            height = rng.uniform(.18,.42)
            panel("Dry scrub", [(x,-2.3,z), (x+.06,-2.3,z-.04),
                  (x+math.cos(a)*.21,-2.3+height,z+math.sin(a)*.21)],
                  [(0,1,2)], "Foliage" if j%2 else "Canvas", root)
    for x in [-5.1,5.1]:
        for z in [-10,10]:
            for i in range(15):
                box("Old track imprint", (x,-2.318,z-2+i*.28), (1.0,.007,.11), "DeckPatch", root, 0)


def prepare_uv_and_merge(root):
    # Merge by direct parent and material: moving pivots stay independent.
    meshes = [o for o in root.children_recursive if o.type == "MESH"]
    for o in meshes:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        # Open terrain has no enclosed volume to define 'outside'; preserve its upward side explicitly.
        if o.get("nomad_upward_surface") and bm.faces and next(iter(bm.faces)).normal.z < 0:
            bmesh.ops.reverse_faces(bm, faces=list(bm.faces))
        bm.to_mesh(o.data)
        bm.free()
        if o.get("nomad_upward_surface"):
            uv = o.data.uv_layers.new(name="Metre-scaled ground UV")
            for loop in o.data.loops:
                vertex = o.data.vertices[loop.vertex_index].co
                uv.data[loop.index].uv = (vertex.x/4,vertex.y/4)
            continue
        bpy.ops.object.select_all(action="DESELECT")
        o.select_set(True)
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.smart_project(angle_limit=1.15192, island_margin=.015)
        bpy.ops.object.mode_set(mode="OBJECT")
    groups = {}
    for o in meshes:
        groups.setdefault((o.parent.name, o.data.materials[0].name), []).append(o)
    for (parent_name, material_name), group in groups.items():
        bpy.ops.object.select_all(action="DESELECT")
        for o in group:
            o.select_set(True)
        bpy.context.view_layer.objects.active = group[0]
        if len(group) > 1:
            bpy.ops.object.join()
        group[0].name = f"{parent_name}_{material_name}"
        if any(face.use_smooth for face in group[0].data.polygons):
            normal = group[0].modifiers.new("Weighted manufactured normals", "WEIGHTED_NORMAL")
            normal.keep_sharp = True
            bpy.ops.object.modifier_apply(modifier=normal.name)


def stats(objects):
    bpy.context.view_layer.update()
    meshes = [o for o in objects if o.type == "MESH"]
    for o in meshes:
        o.data.calc_loop_triangles()
    points = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    return {
        "meshCount": len(meshes),
        "triangles": sum(len(o.data.loop_triangles) for o in meshes),
        "degenerateTriangles": sum(t.area < 1e-10 for o in meshes for t in o.data.loop_triangles),
        "vertices": sum(len(o.data.vertices) for o in meshes),
        "uvComplete": all(len(o.data.uv_layers) > 0 for o in meshes),
        "boundsBlender": [max(p[i] for p in points) - min(p[i] for p in points) for i in range(3)],
        "minimumBlender": [min(p[i] for p in points) for i in range(3)],
        "maximumBlender": [max(p[i] for p in points) for i in range(3)],
        "pivots": {o["nomad_part_id"]: list(o.matrix_world.translation)
                   for o in objects if o.type == "EMPTY" and "nomad_part_id" in o},
        "parts": [o.name for o in objects if o.type == "EMPTY"],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    out = Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=True)
    (out / "manifest.json").write_text(json.dumps({"version": VERSION, "status": "running"}), encoding="utf-8")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.context.scene.unit_settings.system = "METRIC"
    for name, (color, metal, rough) in PALETTE.items():
        mat = bpy.data.materials.new("NW1_" + name)
        mat.diffuse_color = (*color, 1)
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        bsdf.inputs["Base Color"].default_value = (*color, 1)
        bsdf.inputs["Metallic"].default_value = metal
        bsdf.inputs["Roughness"].default_value = rough
        MATS[name] = mat
    generators = {"Vehicle": vehicle, "WaterTank": tank, "Dispenser": dispenser,
                  "Toilet": toilet, "Easel": easel, "Driver": console, "Rock": rock, "Waystation": stop,
                  "Desert": desert}
    records = []
    roots = []
    for name, build in generators.items():
        root = empty("NW1_" + name)
        build(root)
        anchors = []
        for axis, position in [("Right", (1, 0, 0)), ("Up", (0, 1, 0)), ("Forward", (0, 0, 1))]:
            marker = empty(root.name + "_Axis" + axis, root, position)
            anchors.append({"name": marker.name, "position": list(position)})
        prepare_uv_and_merge(root)
        bpy.context.view_layer.update()
        objects = [root] + list(root.children_recursive)
        source = stats(objects)
        assert source["degenerateTriangles"] == 0, (name, "degenerate triangles", source)
        bpy.ops.object.select_all(action="DESELECT")
        for o in objects:
            o.select_set(True)
        bpy.context.view_layer.objects.active = root
        path = out / f"NW1_{name}.fbx"
        # Keep axis conversion on the hierarchy root. Baking it into mesh data while
        # retaining translated axle parents moves children a second time on FBX import.
        bpy.ops.export_scene.fbx(filepath=str(path), use_selection=True, object_types={"EMPTY", "MESH"},
                                apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                                use_space_transform=True, bake_space_transform=False, add_leaf_bones=False,
                                bake_anim=False, use_mesh_modifiers=True, mesh_smooth_type="FACE", path_mode="AUTO",
                                use_custom_props=True)
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=str(path))
        imported = set(bpy.data.objects) - before
        readback = stats(imported)
        assert source["triangles"] == readback["triangles"], (name, source, readback)
        assert readback["uvComplete"]
        assert all(abs(a - b) < .002 for a, b in zip(source["boundsBlender"], readback["boundsBlender"])), (name, source, readback)
        for key in ["minimumBlender", "maximumBlender"]:
            assert all(abs(a - b) < .002 for a, b in zip(source[key], readback[key])), (name, key, source, readback)
        assert source["pivots"].keys() == readback["pivots"].keys(), (name, "pivot identities", source, readback)
        for part_id, position in source["pivots"].items():
            assert all(abs(a - b) < .002 for a, b in zip(position, readback["pivots"][part_id])), (name, part_id, source, readback)
        for o in imported:
            bpy.data.objects.remove(o, do_unlink=True)
        work_anchors = [{"name": o.name,
                         "position": [o.matrix_world.translation.x, o.matrix_world.translation.z, o.matrix_world.translation.y]}
                        for o in root.children_recursive if o.type == "EMPTY" and "_Work_" in o.name]
        records.append({"name": name, "file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        "source": source, "roundTrip": readback, "anchors": anchors, "workAnchors": work_anchors})
        roots.append(root)
    # Keep a navigable source: one collection per asset, hidden by default except the vehicle.
    for root in roots:
        collection = bpy.data.collections.new(root.name)
        bpy.context.scene.collection.children.link(collection)
        for o in [root] + list(root.children_recursive):
            for old in list(o.users_collection):
                old.objects.unlink(o)
            collection.objects.link(o)
        collection.hide_viewport = root != roots[0]
        collection.hide_render = root != roots[0]
    bpy.ops.wm.save_as_mainfile(filepath=str(out / "Nomad_FirstPass.blend"))
    manifest = {"version": VERSION, "status": "passed", "generatorSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                "units": "metres", "authoring": "Unity x/y/z to Blender x/z/y; three independent axis anchors verified in Unity", "palette": PALETTE,
                "materials": [{"name": "NW1_" + name, "color": list(values[0]), "metallic": values[1], "roughness": values[2]}
                              for name, values in PALETTE.items()],
                "assets": records, "manualArtReviewRequired": True}
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"status": "passed", "assets": len(records), "triangles": sum(r["source"]["triangles"] for r in records)}))


if __name__ == "__main__":
    main()
