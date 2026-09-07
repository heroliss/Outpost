"""NW5 cantilever canopy in gameplay metres; skin and frame keep separate bindings.

Roof material reuses Unity's existing metal textures; this helper creates geometry,
not another atlas. No collision/navigation/weather data is authored here.
"""
from mathutils import Vector
import bpy


def channel(study, name, a, b, width, depth, material, parent):
    """Extrude a C-section along arbitrary endpoints, rather than a solid box beam."""
    a, b = Vector(a), Vector(b)
    axis = (b-a).normalized()
    side = axis.cross(Vector((0,1,0)))
    if side.length < .01: side = axis.cross(Vector((0,0,1)))
    side.normalize(); up = side.cross(axis).normalized()
    w, h, t = width/2, depth/2, .014
    profile = [(-w,-h),(w,-h),(w,-h+t),(-w+t,-h+t),
               (-w+t,h-t),(w,h-t),(w,h),(-w,h)]
    vertices = [tuple(p+side*x+up*y) for p in (a,b) for x,y in profile]
    faces = [tuple(reversed(range(8))),tuple(range(8,16))]
    faces += [(i,(i+1)%8,(i+1)%8+8,i+8) for i in range(8)]
    return study.mesh(name,vertices,faces,material,parent,.002)


def build(study, vehicle):
    base = study.BASE
    material = bpy.data.materials.new('NW5_CanopySheet'); material.use_nodes = True
    bsdf = material.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Base Color'].default_value = (.36,.365,.325,1)
    bsdf.inputs['Metallic'].default_value = .67; bsdf.inputs['Roughness'].default_value = .6
    base.MATS['CanopySheet'] = material
    frame_material = bpy.data.materials.new('NW5_CanopyFrame'); frame_material.use_nodes = True
    frame_bsdf = frame_material.node_tree.nodes.get('Principled BSDF')
    frame_bsdf.inputs['Base Color'].default_value = (.32,.345,.305,1)
    frame_bsdf.inputs['Metallic'].default_value = .55; frame_bsdf.inputs['Roughness'].default_value = .62
    base.MATS['CanopyFrame'] = frame_material
    root = base.empty('NW5_Canopy',vehicle)
    frame = base.empty('NW5_CanopyFrame',root)
    roof = base.empty('NW5_CanopyRoof',root)
    def roof_height(z): return 2.86-(z+2.83)*.12/4.12
    for z in (-2.65,1.10):
        # Post and foot remain outside the original 10.8 m walking rectangle.
        channel(study,'Canopy mast',(-5.56,-.08,z),(-5.56,roof_height(z)-.05,z),.14,.15,'CanopyFrame',frame)
        base.box('Mast mounting shoe',(-5.56,-.025,z),(.20,.07,.34),'Steel',frame,.012)
        channel(study,'Canopy diagonal',(-5.56,2.06,z),(-3.56,2.65,z),.075,.09,'CanopyFrame',frame)
    for z in (-2.65,-.775,1.10):
        y = roof_height(z)-.10
        channel(study,'Cantilever spar',(-5.56,y,z),(-1.84,y,z),.10,.15,'CanopyFrame',frame)
    channel(study,'Lamp cable tray',(-4.25,roof_height(-2.70)-.14,-2.70),
            (-4.25,roof_height(1.17)-.14,1.17),.065,.07,'CanopyFrame',frame)
    for x in (-5.59,-1.83):
        channel(study,'Roof edge channel',(x,2.85,-2.83),(x,2.73,1.29),.09,.11,'Steel',frame)
    # Thin standing-seam strips, a folded lip at both long edges and a slight slope.
    # Cross-section is closed and extruded in Z to retain an underside and correct shadows.
    for i in range(5):
        x0 = -5.59+i*.752
        profile = [(0,0),(.012,.043),(.033,.043),(.045,.011),
                   (.720,.011),(.736,.043),(.750,.043),(.752,0),
                   (.731,0),(.713,-.011),(.052,-.011),(.032,0)]
        vertices = [(x0+x, y+height, z) for z,height in ((-2.83,2.86),(1.29,2.74)) for x,y in profile]
        n = len(profile)
        faces = [tuple(reversed(range(n))),tuple(range(n,n*2))]
        faces += [(j,(j+1)%n,(j+1)%n+n,j+n) for j in range(n)]
        study.mesh('Standing seam roof strip',vertices,faces,'CanopySheet',roof,.0015)
    for i,z in enumerate((-1.90,.35)):
        channel(study,'Lamp hanger',(-4.25,2.63,z),(-4.25,roof_height(z)-.14,z),.045,.045,'CanopyFrame',frame)
        lamp = base.empty('CanopyWorkLamp_'+str(i),root,(-4.25,2.56,z))
        base.box('Folded lamp hood',(0,.045,0),(.55,.10,.23),'CanopyFrame',lamp,.02)
        base.box('Warm diffuser',(0,0,0),(.46,.045,.18),'Lamp',lamp,.008)
    return root


def check_clearance(root):
    bpy.context.view_layer.update()
    for obj in root.children_recursive:
        if obj.type != 'MESH': continue
        for v in obj.data.vertices:
            p = obj.matrix_world@v.co
            assert abs(p.x) >= 5.4 or abs(p.y) >= 4.2 or p.z >= 2.02, ('canopy-walking-clearance',obj.name,list(p))
