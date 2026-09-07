"""NW5 deck and cockpit forms, authored in gameplay metres by the vehicle exporter.

No scene/logic ownership: decorative geometry stays below the walk surface or outside
its perimeter. The floor and opaque cockpit get separate baked atlases.
"""
import math
import bpy


def deck_material(study, name, shade, center_x, center_z, width):
    mat = study.material(name, tuple(c*shade for c in (.285,.263,.224)), .62, .59)
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes.get('Principled BSDF')
    position = nodes.new('ShaderNodeNewGeometry').outputs['Position']

    def calc(operation, a, b):
        n = nodes.new('ShaderNodeMath'); n.operation = operation
        for i,value in enumerate((a,b)):
            if isinstance(value,(int,float)): n.inputs[i].default_value = value
            else: links.new(value,n.inputs[i])
        return n.outputs[0]

    axes = nodes.new('ShaderNodeSeparateXYZ'); links.new(position,axes.inputs[0])
    x = calc('DIVIDE',calc('ABSOLUTE',axes.outputs['X'],0),5.4)
    z = calc('DIVIDE',calc('ABSOLUTE',axes.outputs['Y'],0),4.2)
    edge = calc('POWER',calc('MAXIMUM',x,z),5)
    broad = nodes.new('ShaderNodeTexNoise'); broad.inputs['Scale'].default_value = 2.7
    broad.inputs['Detail'].default_value = 4
    links.new(position,broad.inputs['Vector'])
    dust = calc('MULTIPLY',calc('ADD',.13,calc('MULTIPLY',edge,.5)),broad.outputs['Fac'])
    base = bsdf.inputs['Base Color'].links[0].from_socket
    mix = nodes.new('ShaderNodeMixRGB'); mix.blend_type = 'MIX'
    links.new(dust,mix.inputs[0]); links.new(base,mix.inputs[1])
    mix.inputs[2].default_value = (.32,.245,.155,1)
    # Stretched fine noise gives broken wear lines, not regular grooves everywhere.
    scale = nodes.new('ShaderNodeVectorMath'); scale.operation = 'MULTIPLY'
    links.new(position,scale.inputs[0]); scale.inputs[1].default_value = (65,2.2,1)
    scratches = nodes.new('ShaderNodeTexNoise'); scratches.inputs['Scale'].default_value = 1
    scratches.inputs['Detail'].default_value = 2; links.new(scale.outputs[0],scratches.inputs['Vector'])
    wear = calc('MULTIPLY',calc('MAXIMUM',calc('SUBTRACT',scratches.outputs['Fac'],.66),0),1.1)
    worn = nodes.new('ShaderNodeMixRGB'); links.new(wear,worn.inputs[0]); links.new(mix.outputs[0],worn.inputs[1])
    worn.inputs[2].default_value = (.43,.415,.36,1)
    # Plate-specific coordinates survive the mesh join: wear gathers near joints,
    # while the large walking field remains relatively calm and legible.
    dx = calc('SUBTRACT',width*.5,calc('ABSOLUTE',calc('SUBTRACT',axes.outputs['X'],center_x),0))
    dz = calc('SUBTRACT',.7,calc('ABSOLUTE',calc('SUBTRACT',axes.outputs['Y'],center_z),0))
    distance = calc('MINIMUM',dx,dz)
    seam = calc('MAXIMUM',calc('SUBTRACT',1,calc('DIVIDE',distance,.085)),0)
    stained = nodes.new('ShaderNodeMixRGB'); links.new(calc('MULTIPLY',seam,.6),stained.inputs[0])
    links.new(worn.outputs[0],stained.inputs[1]); stained.inputs[2].default_value = (.135,.099,.060,1)
    band = calc('MULTIPLY',calc('GREATER_THAN',distance,.035),calc('LESS_THAN',distance,.049))
    band = calc('MULTIPLY',band,calc('GREATER_THAN',broad.outputs['Fac'],.52))
    edge_wear = nodes.new('ShaderNodeMixRGB'); links.new(calc('MULTIPLY',band,.48),edge_wear.inputs[0])
    links.new(stained.outputs[0],edge_wear.inputs[1]); edge_wear.inputs[2].default_value = (.42,.365,.27,1)
    links.new(edge_wear.outputs[0],bsdf.inputs['Base Color'])
    links.new(calc('MINIMUM',calc('ADD',.59,calc('ADD',calc('MULTIPLY',dust,.55),calc('MULTIPLY',seam,.23))),.94),bsdf.inputs['Roughness'])
    links.new(calc('MAXIMUM',calc('SUBTRACT',.62,calc('ADD',calc('MULTIPLY',dust,.6),calc('MULTIPLY',seam,.35))),.12),bsdf.inputs['Metallic'])
    return mat


def build_deck(study, vehicle):
    base = study.BASE
    study.material('DeckFastener',(.22,.22,.205),.75,.43)
    study.material('DeckSeam',(.10,.095,.08),.25,.87)
    root = base.empty('NW5_DeckSurface',vehicle)
    # A few larger staggered plates read as assembled flooring rather than a chessboard.
    for row in range(6):
        widths = [2.4,2.4,2.4,2.4,1.2] if row%2 == 0 else [1.2,2.4,2.4,2.4,2.4]
        left = -5.4
        z = -3.5 + row*1.4
        for col,width in enumerate(widths):
            x = left+width*.5; left += width
            name = 'DeckWear_'+str(row)+'_'+str(col)
            deck_material(study,name,(.96,1.00,1.04,1.08,.92)[(row*3+col)%5],x,z,width)
            outline = study.octagon(x,z,width-.018,1.382,.035)
            vertices = [(xx,y,zz) for y in (-.10,-.01) for xx,zz in outline]
            faces = [tuple(reversed(range(8))),tuple(range(8,16))]
            faces += [(j,(j+1)%8,(j+1)%8+8,j+8) for j in range(8)]
            study.mesh('Staggered deck plate',vertices,faces,name,root,.004)
            for xx in (x-width*.5+.065,x+width*.5-.065):
                for zz in (z-.632,z+.632):
                    # A flush head needs no hidden cylinder sides or bevel rings.
                    ring = [(xx+math.cos(j*math.tau/8)*.014,-.004,zz+math.sin(j*math.tau/8)*.014) for j in range(8)]
                    study.mesh('Flush deck fixing',ring,[tuple(range(8))],'DeckFastener',root,0)
            # Narrow service channel lies flush with the walking surface.
            if (row,col) in ((0,2),(5,1),(3,4)):
                base.box('Drain insert',(x,-.007,z),(.30,.006,.56),'DeckSeam',root,.002)
                for j in range(8):
                    base.box('Drain bridge',(x,-.002,z-.23+j*.065),(.27,.004,.015),'DeckFastener',root,.001)
    return root


def build_cockpit(study, vehicle):
    base = study.BASE
    for args in [('CabHull',(.34,.333,.293),.4,.61),('CabInset',(.17,.215,.196),.34,.64),
                 ('CabRubber',(.065,.073,.071),.05,.82),('CabEdge',(.39,.40,.36),.72,.43),
                 ('CabOchre',(.55,.32,.09),.24,.59)]: study.material(*args)
    root = base.empty('NW5_CockpitShell',vehicle)
    study.extrude_profile('Sloped nose',-4.94,4.94,
        [(-.05,4.22),(-.14,4.58),(-.42,4.95),(-.72,4.87),(-.84,4.48),(-.65,4.22)],'CabHull',root,.018)
    # A separate lower rub rail and recessed grille break up the full-width nose.
    study.extrude_profile('Lower impact channel',-5.12,5.12,
        [(-.78,4.62),(-.86,4.99),(-1.0,5.07),(-1.10,4.96),(-1.10,4.76),(-.98,4.63)],'CabRubber',root,.016)
    study.plate('Inset radiator',0,-.46,4.966,2.95,.31,.025,.065,'CabRubber',root)
    for j in range(15):
        base.box('Vent vertical fin',(-1.3+j*.185,-.46,5.01),(.035,.235,.025),'CabEdge',root,.004)
    for x in (-2.65,2.65):
        study.plate('Front service panel',x,-.43,4.96,1.40,.32,.02,.065,'CabInset',root)
        for xx in (x-.57,x+.57):
            base.cylinder('Nose fastener',(xx,-.43,4.992),.020,.01,'CabEdge',root,'z',8)
    for x in (-4.14,4.14):
        study.plate('Clipped lamp pocket',x,-.38,4.94,1.12,.49,.055,.095,'CabRubber',root)
        for dx in (-.24,.24):
            base.cylinder('Lamp bezel',(x+dx,-.38,5.02),.16,.07,'CabEdge',root,'z',16)
            base.cylinder('Warm headlamp',(x+dx,-.38,5.066),.125,.014,'Lamp',root,'z',16)
        base.box('Lamp eyebrow',(x,-.09,4.95),(1.10,.045,.22),'CabHull',root,.009)
    # Retain the existing standing driver console. The windshield and its side frame
    # sit beyond z=4.2, so this upgrade creates no unmodelled cockpit walls inside navigation.
    glass_section = [(.84,4.26),(1.74,4.44),(1.74,4.46),(.84,4.28)]
    study.extrude_profile('Inclined windshield',2.79,5.00,glass_section,'Glass',root,.003)
    for x in (2.76,3.87,5.03):
        study.extrude_profile('Windshield frame',x-.032,x+.032,
            [(.74,4.23),(1.83,4.445),(1.83,4.505),(.74,4.29)],'CabRubber',root,.008)
    for y,z in ((.76,4.25),(1.80,4.47)):
        base.box('Window cross rail',(3.895,y,z),(2.35,.065,.072),'CabEdge',root,.012)
    study.extrude_profile('Pressed sun visor',2.65,5.14,
        [(1.83,4.29),(1.87,4.32),(1.87,4.74),(1.79,4.78),(1.78,4.69),(1.82,4.67)],'CabHull',root,.008)
    for x in (2.92,4.87):
        base.box('Window lower latch',(x,.76,4.35),(.18,.07,.10),'CabOchre',root,.009)
    base.pipe('Side mirror arm',[(5.46,.57,3.48),(5.68,.78,3.70),(5.73,1.15,3.74)],.025,'CabEdge',root)
    base.box('Side mirror casing',(5.73,1.25,3.74),(.11,.39,.24),'CabRubber',root,.025)
    base.box('Side mirror glass',(5.666,1.25,3.74),(.012,.30,.18),'Glass',root,.007)
    # Real open towing loops replace the old solid orange discs, front and rear.
    for side in (-1,1):
        z = side*4.91
        for x in (-.31,.31):
            base.box('Tow loop mounting',(x,-.72,z),(.10,.16,.10),'CabEdge',root,.018)
        base.pipe('Open towing loop',[(-.31,-.72,z),(-.28,-.94,z+side*.04),
            (0,-1.02,z+side*.04),(.28,-.94,z+side*.04),(.31,-.72,z)],.032,'CabOchre',root)
    return root
