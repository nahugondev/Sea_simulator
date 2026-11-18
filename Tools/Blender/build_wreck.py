"""
Procedural shipwreck for Sea Simulator: a 14 m wooden coastal trader resting on the seabed.

Run inside Blender (Text editor > Run, or through the Blender MCP). Builds the "Wreck" object:
  - a hull shell with real thickness (inner and outer faces), bulwarks, a transom stern, a keel and a rudder;
  - a hold you can enter: big breach on the starboard side, a smaller one at the port bow, an open cargo
    hatch and a collapsed patch of deck; a hold floor, ribs, a bulkhead with a doorway to the aft cabin;
  - a deckhouse (door, windows, hatch in its floor down to the aft cabin), a broken mast, a fallen spar,
    and some cargo (barrels, crates) in the hold.
Every wall is a closed solid, so it lights correctly with back-face culling and can later be turned into
a signed distance field for fish obstacle avoidance. Openings are at least 1.2 m wide for fish to pass.

Frame (Blender): bow along +Y, up +Z, keel bottom at z = 0, centred on x = y = 0. Units: metres.
"""
import math
import random
import bpy
import bmesh
from mathutils import Vector, Matrix

NAME = "Wreck"
random.seed(11)

# ------------------------------------------------------------------------------------------------ hull shape
HALF_L = 7.0          # half length (m)
BEAM = 2.2            # half beam (m)
Z_KEEL = 0.35         # bottom of the hull planking (the keel timber goes below, down to 0)
SHELL = 0.14          # hull planking thickness
BULWARK = 0.6         # bulwark height above the deck
STATIONS = 29
HALF_SECTION = 10     # points per side of a section


def half_breadth(y):
    if y <= 0:
        return BEAM * (1 - 0.36 * (-y / HALF_L) ** 2.5)
    return max(BEAM * (1 - (y / HALF_L) ** 2.2) ** 0.6, 0.06)


def sheer(y):
    """Top of the bulwark: rises toward bow and stern."""
    a = y / HALF_L
    return 3.6 + 0.5 * a * a + (0.3 * a ** 4 if y > 0 else 0)


def fullness(y):
    a = abs(y) / HALF_L
    return 2.2 + (1.1 - 2.2) * a ** 1.5 if y > 0 else 2.2 + (1.7 - 2.2) * a * a


def section_point(y, t):
    """t = 0 at the keel .. 1 at the top of the bulwark. Returns (x >= 0, z)."""
    n = fullness(y)
    x = half_breadth(y) * (1 - (1 - t) ** n) * (1 - 0.06 * t ** 6)
    z = Z_KEEL + t * (sheer(y) - Z_KEEL)
    return x, z


def t_at(y, z):
    return min(max((z - Z_KEEL) / (sheer(y) - Z_KEEL), 0.0), 1.0)


def deck_z(y):
    return sheer(y) - BULWARK


def inner_x(y, z, inset=SHELL * 0.5):
    """Half width of the hull at height z, `inset` inside the outer skin."""
    x, _ = section_point(y, t_at(y, z))
    return max(x - inset, 0.0)


# ------------------------------------------------------------------------------------------------ helpers
def new_object(name, bm):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def apply_modifiers(ob):
    bpy.context.view_layer.objects.active = ob
    for m in list(ob.modifiers):
        bpy.ops.object.modifier_apply(modifier=m.name)


def solidify(ob, thickness, offset=-1.0):
    m = ob.modifiers.new("Solidify", "SOLIDIFY")
    m.thickness = thickness
    m.offset = offset
    m.use_even_offset = True
    m.use_rim = True
    apply_modifiers(ob)


def boolean(ob, cutter, op="DIFFERENCE"):
    m = ob.modifiers.new("Bool", "BOOLEAN")
    m.operation = op
    m.object = cutter
    m.solver = "EXACT"
    apply_modifiers(ob)


def box(name, center, size, rot_z=0.0, rot_x=0.0, rot_y=0.0):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=size, verts=bm.verts)
    rot = Matrix.Rotation(rot_z, 4, "Z") @ Matrix.Rotation(rot_y, 4, "Y") @ Matrix.Rotation(rot_x, 4, "X")
    bmesh.ops.transform(bm, matrix=Matrix.Translation(center) @ rot, verts=bm.verts)
    return new_object(name, bm)


def jagged(name, center, size, roughness=0.22, subdiv=2):
    """A rough blob used to tear holes: icosphere with random dents."""
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=0.5)
    for v in bm.verts:
        v.co *= 1 + random.uniform(-roughness, roughness)
    bmesh.ops.scale(bm, vec=size, verts=bm.verts)
    bmesh.ops.translate(bm, vec=center, verts=bm.verts)
    return new_object(name, bm)


def cylinder(name, p0, p1, radius, segments=10, top_jag=0.0, bulge=0.0, rings=1):
    """Cylinder between p0 and p1 (closed). top_jag: random height of the broken top. bulge: barrel belly."""
    p0, p1 = Vector(p0), Vector(p1)
    axis = p1 - p0
    length = axis.length
    bm = bmesh.new()
    ring_verts = []
    for r in range(rings + 1):
        f = r / rings
        rad = radius * (1 + bulge * math.sin(math.pi * f))
        ring = []
        for k in range(segments):
            a = 2 * math.pi * k / segments
            h = f * length
            if r == rings and top_jag > 0:
                h -= random.uniform(0, top_jag)
            ring.append(bm.verts.new((rad * math.cos(a), rad * math.sin(a), h)))
        ring_verts.append(ring)
    for r in range(rings):
        for k in range(segments):
            k2 = (k + 1) % segments
            bm.faces.new([ring_verts[r][k], ring_verts[r][k2], ring_verts[r + 1][k2], ring_verts[r + 1][k]])
    bm.faces.new(list(reversed(ring_verts[0])))
    bm.faces.new(ring_verts[-1])
    rot = axis.normalized().to_track_quat("Z", "Y").to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=Matrix.Translation(p0) @ rot, verts=bm.verts)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return new_object(name, bm)


def fix_orientation(bm):
    """Makes every closed piece face outward (positive signed volume), piece by piece."""
    seen = set()
    for f in list(bm.faces):
        if f in seen:
            continue
        piece, stack = [], [f]
        seen.add(f)
        while stack:
            g = stack.pop()
            piece.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h not in seen:
                        seen.add(h)
                        stack.append(h)
        bmesh.ops.recalc_face_normals(bm, faces=piece)
        bm.normal_update()
        volume = sum(g.calc_center_median().dot(g.normal) * g.calc_area() for g in piece)
        if volume < 0:
            for g in piece:
                g.normal_flip()
    bm.normal_update()


def remove(ob):
    me = ob.data
    bpy.data.objects.remove(ob, do_unlink=True)
    if me and me.users == 0:
        bpy.data.meshes.remove(me)


def join(objs, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    objs[0].name = name
    objs[0].data.name = name
    return objs[0]


# ------------------------------------------------------------------------------------------------ clean up
for ob in list(bpy.data.objects):
    if ob.name.startswith(NAME):
        remove(ob)

parts = []

# ------------------------------------------------------------------------------------------------ hull shell
bm = bmesh.new()
ys = [-HALF_L + 2 * HALF_L * i / (STATIONS - 1) for i in range(STATIONS)]
rings = []
for y in ys:
    ring = []
    for j in range(-HALF_SECTION, HALF_SECTION + 1):
        t = abs(j) / HALF_SECTION
        x, z = section_point(y, t)
        ring.append(bm.verts.new((math.copysign(x, j) if j else 0.0, y, z)))
    rings.append(ring)
faces = []
for i in range(STATIONS - 1):
    for j in range(2 * HALF_SECTION):
        faces.append(bm.faces.new([rings[i][j], rings[i + 1][j], rings[i + 1][j + 1], rings[i][j + 1]]))
transom = bm.faces.new(rings[0])
bm.normal_update()
# Outward winding: the starboard faces must point to +x, the transom to -y.
f = faces[HALF_SECTION + 3 + (STATIONS // 2) * 2 * HALF_SECTION]
if f.normal.x < 0:
    for f in faces:
        f.normal_flip()
if transom.normal.y > 0:
    transom.normal_flip()
hull = new_object(NAME + "_Hull", bm)
solidify(hull, SHELL, -1.0)

# ribs (frames) along the inside of the hull, from the floor up to the deck
bm = bmesh.new()
for y in [-5.6 + 1.4 * i for i in range(9)]:
    for side in (-1, 1):
        rows = []
        t_deck = t_at(y, deck_z(y))
        ts = [j / HALF_SECTION for j in range(1, HALF_SECTION + 1) if j / HALF_SECTION < t_deck] + [t_deck]
        for t in ts:
            x, z = section_point(y, t)
            xa, za = section_point(y, max(t - 0.02, 0))
            xb, zb = section_point(y, min(t + 0.02, 1))
            tx, tz = xb - xa, zb - za
            ln = math.hypot(tx, tz)
            nx, nz = tz / ln, -tx / ln          # outward normal in the section plane
            row = []
            for depth in (SHELL * 0.7, SHELL + 0.14):
                for dy in (-0.08, 0.08):
                    row.append(bm.verts.new((side * (x - nx * depth), y + dy, z - nz * depth)))
            rows.append(row)
        for a, b in zip(rows, rows[1:]):
            # row = [outer-, outer+, inner-, inner+]
            bm.faces.new([a[2], a[3], b[3], b[2]])     # inner face
            bm.faces.new([a[0], b[0], b[1], a[1]])     # outer face (hidden in the shell)
            bm.faces.new([a[0], a[2], b[2], b[0]])     # aft side
            bm.faces.new([a[1], b[1], b[3], a[3]])     # fore side
        for r in (rows[0], rows[-1]):
            bm.faces.new([r[0], r[1], r[3], r[2]])
fix_orientation(bm)
ribs = new_object(NAME + "_Ribs", bm)

# ------------------------------------------------------------------------------------------------ deck, floor, bulkhead
def slab_between(name, y0, y1, z_of_y, inset, thickness, steps=24):
    """Horizontal slab that follows the hull: its edge is `inset` inside the outer skin at height z(y)."""
    bm = bmesh.new()
    rows = []
    for i in range(steps + 1):
        y = y0 + (y1 - y0) * i / steps
        z = z_of_y(y)
        w = inner_x(y, z, inset)
        rows.append((bm.verts.new((-w, y, z)), bm.verts.new((w, y, z))))
    for a, b in zip(rows, rows[1:]):
        bm.faces.new([a[0], a[1], b[1], b[0]])
    bm.normal_update()
    bm.faces.ensure_lookup_table()
    if bm.faces[0].normal.z < 0:
        for f in bm.faces:
            f.normal_flip()
    ob = new_object(name, bm)
    solidify(ob, thickness, -1.0)
    return ob


deck = slab_between(NAME + "_Deck", -HALF_L + SHELL * 0.6, HALF_L - 0.25, deck_z, SHELL * 0.5, 0.10)
floor_z = 1.3
floor = slab_between(NAME + "_Floor", -HALF_L + SHELL * 0.6, 6.0, lambda y: floor_z, SHELL * 0.5, 0.08)


def fit_x(x, y, margin):
    """Keeps cargo inside the hull: clamps x so the object stays `margin` away from the inner skin."""
    lim = max(inner_x(y, floor_z + 0.3, SHELL + 0.15) - margin, 0.0)
    return max(-lim, min(lim, x))

# bulkhead between the hold and the aft cabin, with a doorway
BULK_Y = -2.4
bm = bmesh.new()
pts = []
zs = [floor_z + (deck_z(BULK_Y) - floor_z) * k / 8 for k in range(9)]
for z in zs:
    pts.append((inner_x(BULK_Y, z), z))
loop = [bm.verts.new((x, BULK_Y, z)) for x, z in pts] + [bm.verts.new((-x, BULK_Y, z)) for x, z in reversed(pts)]
face = bm.faces.new(loop)
bm.normal_update()
if face.normal.y < 0:
    face.normal_flip()
bulkhead = new_object(NAME + "_Bulkhead", bm)
solidify(bulkhead, 0.10, 0.0)
door = box(NAME + "_cut", (0.0, BULK_Y, floor_z + 0.9), (1.3, 0.6, 1.8))
boolean(bulkhead, door)
remove(door)

# ------------------------------------------------------------------------------------------------ openings
DZ_MID = deck_z(1.4)


def skin(side, y, z):
    """Point on the outer skin of the hull (side = +1 starboard, -1 port)."""
    return (side * section_point(y, t_at(y, z))[0], y, z)


cutters = [
    # big breach on the starboard side, into the hold (fish and camera way in)
    (jagged(NAME + "_cut", skin(1, 1.2, 2.1), (1.8, 4.2, 2.4), 0.35), (hull, ribs)),
    # smaller breach at the port bow
    (jagged(NAME + "_cut", skin(-1, 4.2, 2.1), (2.0, 2.6, 2.0), 0.3), (hull, ribs)),
    # broken stretches of bulwark
    (jagged(NAME + "_cut", skin(1, 3.4, sheer(3.4)), (0.9, 1.6, 1.0), 0.3, 1), (hull,)),
    (jagged(NAME + "_cut", skin(-1, -2.6, sheer(-2.6)), (0.9, 2.0, 0.9), 0.3, 1), (hull,)),
    (jagged(NAME + "_cut", skin(-1, 6.3, sheer(6.3)), (0.8, 1.0, 0.8), 0.3, 1), (hull,)),
    # small holes
    (jagged(NAME + "_cut", skin(1, -4.9, 2.0), (0.8, 0.8, 0.6), 0.3, 1), (hull,)),
    (jagged(NAME + "_cut", skin(-1, -0.6, 2.4), (0.7, 0.7, 0.55), 0.3, 1), (hull,)),
    # collapsed patch of deck, port side forward of the hatch
    (jagged(NAME + "_cut", (-0.9, 3.9, deck_z(3.9)), (1.6, 1.7, 0.8), 0.25), (deck,)),
]
for cutter, targets in cutters:
    for t in targets:
        boolean(t, cutter)
    remove(cutter)

# cargo hatch with a raised coaming
HATCH = (0.0, 1.4, 2.0, 2.2)   # centre x, centre y, width (x), length (y)
cut = box(NAME + "_cut", (HATCH[0], HATCH[1], DZ_MID), (HATCH[2], HATCH[3], 1.0))
boolean(deck, cut)
remove(cut)
cx, cy, w, l = HATCH
dz = deck_z(cy)
for c, s in (((cx, cy - l / 2 - 0.06, dz + 0.12), (w + 0.24, 0.12, 0.34)),
             ((cx, cy + l / 2 + 0.06, dz + 0.12), (w + 0.24, 0.12, 0.34)),
             ((cx - w / 2 - 0.06, cy, dz + 0.12), (0.12, l, 0.34)),
             ((cx + w / 2 + 0.06, cy, dz + 0.12), (0.12, l, 0.34))):
    parts.append(box(NAME + "_Coaming", c, s))
# its cover, knocked off and lying askew on the deck
parts.append(box(NAME + "_HatchCover", (-1.0, -0.9, deck_z(-0.9) + 0.12), (1.3, 1.1, 0.08), rot_z=0.5, rot_y=0.12))

# ------------------------------------------------------------------------------------------------ deckhouse
DH = (-4.3, 2.6, 2.5, 2.0)     # centre y, length (y), width (x), height
dy, dl, dw, dh = DH
dz = deck_z(dy)
house = box(NAME + "_House", (0, dy, dz + dh / 2 - 0.05), (dw, dl, dh + 0.1))
inner = box(NAME + "_cut", (0, dy, dz + dh / 2 - 0.05), (dw - 0.2, dl - 0.2, dh - 0.1))
boolean(house, inner)
remove(inner)
for c, s in (((0, dy + dl / 2, dz + 0.85), (1.0, 0.5, 1.7)),           # door, facing forward
             ((dw / 2, dy - 0.3, dz + 1.3), (0.5, 0.7, 0.5)),          # starboard window
             ((-dw / 2, dy + 0.2, dz + 1.3), (0.5, 0.7, 0.5)),         # port window
             ((0, dy - dl / 2, dz + 1.3), (0.6, 0.5, 0.45))):          # aft window
    cut = box(NAME + "_cut", c, s)
    boolean(house, cut)
    remove(cut)
# a corner of the roof caved in
cut = jagged(NAME + "_cut", (0.8, dy - 0.6, dz + dh), (1.4, 1.3, 0.6), 0.25, 1)
boolean(house, cut)
remove(cut)
# hatch in the deckhouse floor, down to the aft cabin
cut = box(NAME + "_cut", (0, dy + 0.2, dz), (1.1, 1.0, 0.6))
boolean(deck, cut)
remove(cut)
parts.append(house)

# ------------------------------------------------------------------------------------------------ rig, keel, rudder
MAST_Y = -1.0
parts.append(cylinder(NAME + "_Mast", (0, MAST_Y, floor_z), (0, MAST_Y, deck_z(MAST_Y) + 3.4), 0.17, 10, top_jag=0.5))
parts.append(cylinder(NAME + "_Spar", (1.9, -0.2, deck_z(0) + 0.65), (-3.7, 3.4, 0.25), 0.12, 8))
parts.append(cylinder(NAME + "_Bowsprit", (0, HALF_L - 0.6, deck_z(6.4) + 0.3), (0, HALF_L + 1.3, deck_z(6.4) + 0.9), 0.11, 8, top_jag=0.25))
# keel timber under the whole length, with a stem post at the bow
bm = bmesh.new()
keel_pts = [(-HALF_L - 0.05, 0.0), (HALF_L - 0.55, 0.0), (HALF_L + 0.05, 0.6), (HALF_L + 0.1, sheer(HALF_L) - 0.1),
            (HALF_L - 0.12, sheer(HALF_L) - 0.1), (HALF_L - 0.25, 0.6), (HALF_L - 0.7, Z_KEEL + 0.1), (-HALF_L - 0.05, Z_KEEL + 0.1)]
vs = [[bm.verts.new((sx, y, z)) for (y, z) in keel_pts] for sx in (-0.13, 0.13)]
bm.faces.new(vs[0])
bm.faces.new(list(reversed(vs[1])))
n = len(keel_pts)
for k in range(n):
    k2 = (k + 1) % n
    bm.faces.new([vs[0][k], vs[1][k], vs[1][k2], vs[0][k2]])
bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
parts.append(new_object(NAME + "_Keel", bm))
# rudder, hanging off its post at an angle
parts.append(box(NAME + "_Rudder", (0.25, -HALF_L - 0.45, 1.35), (0.08, 0.85, 2.0), rot_z=0.45))

# ------------------------------------------------------------------------------------------------ cargo
def barrel(p, lying=False, rot=0.0):
    x, y, z = p
    x = fit_x(x, y, 0.45 if lying else 0.3)
    if lying:
        d = Vector((math.cos(rot), math.sin(rot), 0)) * 0.42
        return cylinder(NAME + "_Barrel", (x - d.x, y - d.y, z + 0.3), (x + d.x, y + d.y, z + 0.3), 0.27, 10, bulge=0.15, rings=2)
    return cylinder(NAME + "_Barrel", (x, y, z), (x, y, z + 0.85), 0.27, 10, bulge=0.15, rings=2)


for p in ((0.5, 3.6), (1.0, 4.0), (0.3, 4.3)):
    parts.append(barrel((p[0], p[1], floor_z)))
parts.append(barrel((-0.6, 2.6, floor_z), lying=True, rot=0.8))
parts.append(barrel((0.9, -1.6, floor_z), lying=True, rot=-0.3))
for c, s, r in (((-1.0, -0.4, floor_z + 0.35), (0.75, 0.75, 0.7), 0.3),
                ((-1.1, 0.5, floor_z + 0.35), (0.75, 0.75, 0.7), -0.2),
                ((-1.05, 0.0, floor_z + 1.05), (0.7, 0.7, 0.7), 0.6),
                ((1.0, -4.8, floor_z + 0.3), (0.6, 0.6, 0.6), 0.1)):
    c = (fit_x(c[0], c[1], 0.5), c[1], c[2])
    parts.append(box(NAME + "_Crate", c, s, rot_z=r))

# ------------------------------------------------------------------------------------------------ join
wreck = join([hull, ribs, deck, floor, bulkhead] + parts, NAME)
bm = bmesh.new()
bm.from_mesh(wreck.data)
fix_orientation(bm)
# the boolean cuts leave concave n-gons, which Unity's importer may discard: export triangles only
bmesh.ops.triangulate(bm, faces=list(bm.faces), quad_method="BEAUTY", ngon_method="BEAUTY")
bm.to_mesh(wreck.data)
bm.free()
wreck.data.shade_flat()


# ------------------------------------------------------------------------------------------------ material
def wreck_material():
    """
    Procedural look (object space, so it does not depend on the UVs): weathered planks running along the
    hull and across the deck, algae and sediment on the upward faces, patches of encrusting growth.
    It is baked into a single texture for Unity (see bake()).
    """
    mat = bpy.data.materials.get(NAME) or bpy.data.materials.new(NAME)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    N, L = nt.nodes, nt.links

    def node(kind, x, y, **inputs):
        n = N.new(kind)
        n.location = (x, y)
        for k, v in inputs.items():
            n.inputs[k].default_value = v
        return n

    def math_node(op, x, y, a=None, b=None, clamp=False):
        n = N.new("ShaderNodeMath")
        n.operation = op
        n.location = (x, y)
        n.use_clamp = clamp
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                n.inputs[i].default_value = v
            else:
                L.new(v, n.inputs[i])
        return n.outputs[0]

    def ramp(x, y, fac, stops):
        n = N.new("ShaderNodeValToRGB")
        n.location = (x, y)
        els = n.color_ramp.elements
        while len(els) > len(stops):
            els.remove(els[-1])
        while len(els) < len(stops):
            els.new(0.5)
        for e, (pos, col) in zip(els, stops):
            e.position = pos
            e.color = (*col, 1.0)
        L.new(fac, n.inputs["Fac"])
        return n.outputs["Color"]

    def mix(x, y, fac, a, b):
        n = N.new("ShaderNodeMix")
        n.data_type = "RGBA"
        n.location = (x, y)
        L.new(fac, n.inputs["Factor"])
        for slot, v in ((6, a), (7, b)):
            if isinstance(v, tuple):
                n.inputs[slot].default_value = (*v, 1.0)
            else:
                L.new(v, n.inputs[slot])
        return n.outputs[2]

    coord = node("ShaderNodeTexCoord", -1600, 0)
    geo = node("ShaderNodeNewGeometry", -1600, -300)
    sep = N.new("ShaderNodeSeparateXYZ"); sep.location = (-1400, 0)
    L.new(coord.outputs["Object"], sep.inputs[0])
    nsep = N.new("ShaderNodeSeparateXYZ"); nsep.location = (-1400, -300)
    L.new(geo.outputs["Normal"], nsep.inputs[0])
    up = math_node("ABSOLUTE", -1200, -300, nsep.outputs[2])
    horizontal = math_node("GREATER_THAN", -1000, -300, up, 0.6)     # 1 on deck / floor faces

    # plank coordinate: height on the walls, across the ship (x) on decks
    pa = N.new("ShaderNodeMix")
    pa.data_type = "FLOAT"
    pa.location = (-1000, 100)
    L.new(horizontal, pa.inputs["Factor"])
    L.new(sep.outputs[2], pa.inputs[2])
    L.new(sep.outputs[0], pa.inputs[3])
    plank_n = math_node("MULTIPLY", -800, 100, pa.outputs[0], 4.5)      # ~22 cm planks
    seam = math_node("FRACT", -600, 100, plank_n)
    seam = math_node("PINGPONG", -450, 100, seam, 0.5)                  # 0 at the seam .. 0.5 mid-plank
    seam_dark = math_node("LESS_THAN", -300, 100, seam, 0.035)
    plank_id = math_node("FLOOR", -600, 250, plank_n)
    tint = node("ShaderNodeTexWhiteNoise", -450, 250)
    tint.noise_dimensions = "1D"
    L.new(plank_id, tint.inputs["W"])

    grain = node("ShaderNodeTexNoise", -800, -100, Scale=3.0, Detail=6.0, Roughness=0.6)
    mapping = node("ShaderNodeMapping", -1000, -100)
    mapping.inputs["Scale"].default_value = (6.0, 0.6, 6.0)            # stretched along the ship
    L.new(coord.outputs["Object"], mapping.inputs["Vector"])
    L.new(mapping.outputs[0], grain.inputs["Vector"])
    wood_f = math_node("ADD", -300, -50, grain.outputs["Fac"], math_node("MULTIPLY", -450, -150, tint.outputs["Value"], 0.35))
    wood = ramp(-150, -50, wood_f, [(0.25, (0.13, 0.10, 0.075)), (0.55, (0.27, 0.22, 0.16)), (0.95, (0.40, 0.35, 0.27))])
    wood = mix(100, 0, seam_dark, wood, (0.05, 0.04, 0.03))

    # encrusting growth: large soft patches, greenish-brown algae, a few pinkish coralline spots
    patches = node("ShaderNodeTexNoise", -800, -450, Scale=0.9, Detail=4.0, Roughness=0.55)
    L.new(coord.outputs["Object"], patches.inputs["Vector"])
    growth_mask = patches.outputs["Fac"]
    algae_col = node("ShaderNodeTexNoise", -800, -700, Scale=6.0, Detail=3.0)
    L.new(coord.outputs["Object"], algae_col.inputs["Vector"])
    algae = ramp(-450, -700, algae_col.outputs["Fac"], [(0.3, (0.16, 0.20, 0.10)), (0.7, (0.30, 0.33, 0.17))])
    coralline = node("ShaderNodeTexNoise", -800, -950, Scale=2.5, Detail=2.0)
    L.new(coord.outputs["Object"], coralline.inputs["Vector"])
    pink = ramp(-450, -950, coralline.outputs["Fac"], [(0.64, (0, 0, 0)), (0.74, (0.6, 0.6, 0.6))])
    col = mix(300, -200, ramp(100, -450, growth_mask, [(0.45, (0, 0, 0)), (0.62, (0.85, 0.85, 0.85))]), wood, algae)
    col = mix(500, -300, pink, col, (0.45, 0.27, 0.30))

    # silt on everything that faces up
    silt_n = node("ShaderNodeTexNoise", -800, -1200, Scale=4.0, Detail=4.0)
    L.new(coord.outputs["Object"], silt_n.inputs["Vector"])
    silt_f = math_node("MULTIPLY", -300, -1150, math_node("SUBTRACT", -450, -1150, nsep.outputs[2], 0.45),
                       math_node("ADD", -450, -1300, silt_n.outputs["Fac"], 0.6))
    silt_f = math_node("MULTIPLY", -150, -1150, math_node("SUBTRACT", -300, -1300, silt_f, 0.25), 1.6, clamp=True)
    col = mix(700, -400, silt_f, col, (0.33, 0.30, 0.23))

    bsdf = node("ShaderNodeBsdfPrincipled", 900, -200, Roughness=0.85)
    L.new(col, bsdf.inputs["Base Color"])
    out = node("ShaderNodeOutputMaterial", 1150, -200)
    L.new(bsdf.outputs[0], out.inputs["Surface"])
    return mat


mat = wreck_material()
wreck.data.materials.clear()
wreck.data.materials.append(mat)
bpy.ops.object.select_all(action="DESELECT")
wreck.select_set(True)
bpy.context.view_layer.objects.active = wreck
bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))


# ------------------------------------------------------------------------------------------------ bake
def bake(ob, out_dir, size=2048, ao_strength=0.65):
    """
    Unwraps the wreck and bakes its look into one texture (<out_dir>/Wreck.png): the procedural colour,
    multiplied by ambient occlusion so the inside of the hold and the gaps read darker in Unity.
    """
    import numpy as np
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.004, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode="OBJECT")

    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = 32
    sc.cycles.device = "CPU"
    nt = ob.active_material.node_tree
    imgs = {}
    for key in ("color", "ao"):
        name = NAME + "_" + key
        if name in bpy.data.images:
            bpy.data.images.remove(bpy.data.images[name])
        imgs[key] = bpy.data.images.new(name, size, size, float_buffer=False)
    tex = nt.nodes.new("ShaderNodeTexImage")
    nt.nodes.active = tex
    sc.render.bake.margin = 6
    tex.image = imgs["color"]
    sc.render.bake.use_pass_direct = False
    sc.render.bake.use_pass_indirect = False
    sc.render.bake.use_pass_color = True
    bpy.ops.object.bake(type="DIFFUSE")
    tex.image = imgs["ao"]
    sc.world.light_settings.distance = 1.5
    bpy.ops.object.bake(type="AO")

    c = np.array(imgs["color"].pixels[:]).reshape(size, size, 4)
    a = np.array(imgs["ao"].pixels[:]).reshape(size, size, 4)[..., :1]
    c[..., :3] *= 1 - ao_strength * (1 - a)
    final = bpy.data.images.get(NAME + "_Texture") or bpy.data.images.new(NAME + "_Texture", size, size)
    final.scale(size, size)
    final.pixels.foreach_set(c.ravel().astype(np.float32))
    final.filepath_raw = out_dir + "\\" + NAME + ".png"
    final.file_format = "PNG"
    final.save()
    tex.image = final
    # show the baked result (what Unity will get) instead of the procedural network
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    for key in imgs:
        bpy.data.images.remove(imgs[key])
    sc.render.engine = "BLENDER_EEVEE"
    return final.filepath_raw


texture = bake(wreck, OUT_DIR) if globals().get("OUT_DIR") else None
tris = sum(len(p.vertices) - 2 for p in wreck.data.polygons)
result = {"verts": len(wreck.data.vertices), "tris": tris, "texture": texture}
