"""
Procedural reef shark for Sea Simulator, in the style of the imported tropical fish:
a low-poly body (~1.2k triangles) with a side-projected painted texture.

Frame: nose along +Y, back along +Z, 1 unit long (y = +0.5 nose .. -0.5 tail tip), sideways = X.
The body is a loft of rings along the length; the fins are thin lens-shaped strips (no open edges
except their root, hidden inside the body) so they light correctly with back-face culling.

Texture atlas (1024 x 1024):
  - top half    : side view of the shark (u = 1 - s, v = height), used by the body and the vertical fins;
  - bottom left : top side of the horizontal fins (pectorals, pelvics), bottom right: their underside.
"""
import math
import bpy
import bmesh
import numpy as np

OUT_DIR = globals().get("OUT_DIR", r"C:\temp")
TEX_SIZE = 1024
NAME = "Shark"

# ---------------------------------------------------------------------------------------------------------
# Body profile, s = 0 at the nose .. 1 at the tail tip. Values: (s, half width, top radius, bottom radius, centre z)
PROFILE = [
    (0.000, 0.000, 0.000, 0.000, -0.004),
    (0.012, 0.018, 0.012, 0.010, -0.004),
    (0.035, 0.036, 0.026, 0.020, -0.002),
    (0.070, 0.054, 0.042, 0.032, 0.000),
    (0.110, 0.066, 0.056, 0.044, 0.002),
    (0.160, 0.075, 0.068, 0.054, 0.004),
    (0.220, 0.081, 0.078, 0.062, 0.005),
    (0.290, 0.083, 0.083, 0.066, 0.005),
    (0.360, 0.081, 0.083, 0.064, 0.005),
    (0.430, 0.075, 0.078, 0.058, 0.004),
    (0.500, 0.066, 0.068, 0.050, 0.004),
    (0.570, 0.054, 0.056, 0.040, 0.004),
    (0.630, 0.043, 0.044, 0.031, 0.005),
    (0.680, 0.033, 0.034, 0.024, 0.006),
    (0.720, 0.025, 0.027, 0.019, 0.008),
    (0.755, 0.020, 0.022, 0.016, 0.010),
    (0.790, 0.016, 0.019, 0.013, 0.014),
    (0.820, 0.012, 0.016, 0.009, 0.019),
    (0.845, 0.000, 0.000, 0.000, 0.024),
]
RADIAL = 16          # vertices around the body
Z_MIN, Z_MAX = -0.15, 0.20   # height range mapped into the side-view texture


def profile_at(s):
    for a, b in zip(PROFILE, PROFILE[1:]):
        if a[0] <= s <= b[0]:
            t = (s - a[0]) / (b[0] - a[0])
            return [a[i] + (b[i] - a[i]) * t for i in range(1, 5)]
    return list(PROFILE[-1][1:])


def top_z(s):
    w, ht, hb, c = profile_at(s)
    return c + ht


def bottom_z(s):
    w, ht, hb, c = profile_at(s)
    return c - hb


def side_uv(s, z):
    return (1.0 - s, 0.5 + 0.5 * (z - Z_MIN) / (Z_MAX - Z_MIN))


def y_of(s):
    return 0.5 - s


# ---------------------------------------------------------------------------------------------------------
bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new("UVMap")
faces_uv = []   # (face, [uv per loop])


def add_face(verts, uvs, outward=None):
    """outward: a direction the face must point to (sets the winding, so back-face culling keeps it)."""
    if outward is not None:
        p = [np.array(v.co) for v in verts]
        nrm = np.cross(p[1] - p[0], p[-1] - p[0])
        if np.dot(nrm, outward) < 0:
            verts, uvs = verts[::-1], uvs[::-1]
    f = bm.faces.new(verts)
    faces_uv.append((f, uvs))
    return f


# ---- Body ----
rings = []
ring_s = [p[0] for p in PROFILE[1:-1]]
for s in ring_s:
    w, ht, hb, c = profile_at(s)
    ring = []
    for k in range(RADIAL):
        a = 2 * math.pi * k / RADIAL           # 0 = back, pi = belly
        sa, ca = math.sin(a), math.cos(a)
        # slightly squarish section (superellipse), flatter belly
        e = 2.4
        x = w * math.copysign(abs(sa) ** (2 / e), sa)
        r = ht if ca >= 0 else hb
        z = c + r * math.copysign(abs(ca) ** (2 / e), ca)
        ring.append((bm.verts.new((x, y_of(s), z)), s, z))
    rings.append(ring)

nose = bm.verts.new((0, y_of(PROFILE[0][0]), PROFILE[0][4]))
tail = bm.verts.new((0, y_of(PROFILE[-1][0]), PROFILE[-1][4]))

for k in range(RADIAL):
    k2 = (k + 1) % RADIAL
    a, b = rings[0][k], rings[0][k2]
    add_face([nose, b[0], a[0]], [side_uv(0, PROFILE[0][4]), side_uv(b[1], b[2]), side_uv(a[1], a[2])])
for i in range(len(rings) - 1):
    for k in range(RADIAL):
        k2 = (k + 1) % RADIAL
        a, b, c_, d = rings[i][k], rings[i][k2], rings[i + 1][k2], rings[i + 1][k]
        add_face([a[0], b[0], c_[0], d[0]], [side_uv(v[1], v[2]) for v in (a, b, c_, d)])
for k in range(RADIAL):
    k2 = (k + 1) % RADIAL
    a, b = rings[-1][k], rings[-1][k2]
    add_face([a[0], b[0], tail], [side_uv(a[1], a[2]), side_uv(b[1], b[2]), side_uv(PROFILE[-1][0], PROFILE[-1][4])])


# ---- Fins ----
def build_fin(leading, trailing, normal, thickness, rows, cols=3, uv_mode="side", mirror_x=False):
    """
    A fin as a strip from its root (t = 0) to its tip (t = 1).
    leading(t), trailing(t): 3D points of the leading / trailing edge. normal: unit vector across the fin.
    The fin is a closed lens: front and back faces meet on both edges and at the tip.
    uv_mode 'side' = side-view projection (vertical fins), 'flat' = fin cells of the atlas (horizontal fins).
    """
    def mx(p):
        return (-p[0], p[1], p[2]) if mirror_x else p
    n = np.array(mx(normal), dtype=float)
    grid = {}
    for r in range(rows + 1):
        t = r / rows
        L = np.array(mx(leading(t)), dtype=float)
        T = np.array(mx(trailing(t)), dtype=float)
        for cidx in range(cols + 1):
            f = cidx / cols
            p = L + (T - L) * f
            thick = thickness * (1 - 0.85 * t) * math.sin(math.pi * f) ** 0.7
            shared = cidx in (0, cols) or r == rows
            for side in (1, -1):
                key = (r, cidx, 0 if shared else side)
                if key not in grid:
                    q = p + n * (0 if shared else side * thick * 0.5)
                    grid[key] = (bm.verts.new(tuple(q)), t, f, q)
    def g(r, cidx, side):
        shared = cidx in (0, cols) or r == rows
        return grid[(r, cidx, 0 if shared else side)]

    def uv(v, side):
        _, t, f, q = v
        if uv_mode == "side":
            return side_uv(0.5 - q[1], q[2])
        # flat fin cells: u = chord fraction, v = span (t). Top side left cell, underside right cell.
        cell_u = 0.0 if side > 0 else 0.5
        return (cell_u + 0.02 + 0.46 * f, 0.02 + 0.42 * t)

    for r in range(rows):
        for cidx in range(cols):
            for side in (1, -1):
                q = [g(r, cidx, side), g(r, cidx + 1, side), g(r + 1, cidx + 1, side), g(r + 1, cidx, side)]
                if (side > 0) == mirror_x:
                    q.reverse()
                verts = [v[0] for v in q]
                if len(set(verts)) < 3:
                    continue
                if len(set(verts)) == 3:
                    seen, vv = set(), []
                    for v in q:
                        if v[0] not in seen:
                            seen.add(v[0]); vv.append(v)
                    q = vv
                add_face([v[0] for v in q], [uv(v, side) for v in q], outward=n * side)


def lerp(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def bez(p0, p1, p2, t):
    a, b = lerp(p0, p1, t), lerp(p1, p2, t)
    return lerp(a, b, t)


def at_top(s, dz):
    return (0.0, y_of(s), top_z(s) + dz)


def at_bottom(s, dz):
    return (0.0, y_of(s), bottom_z(s) - dz)


X = (1.0, 0.0, 0.0)

# First dorsal: tall, swept back, concave trailing edge, free rear tip.
build_fin(
    leading=lambda t: bez(at_top(0.285, -0.012), (0, y_of(0.355), top_z(0.33) + 0.075), (0, y_of(0.420), top_z(0.40) + 0.115), t),
    trailing=lambda t: bez(at_top(0.455, -0.010), (0, y_of(0.432), top_z(0.44) + 0.045), (0, y_of(0.425), top_z(0.40) + 0.113), t),
    normal=X, thickness=0.020, rows=5)

# Second dorsal and anal fin: small.
build_fin(
    leading=lambda t: lerp(at_top(0.655, -0.006), (0, y_of(0.690), top_z(0.68) + 0.026), t),
    trailing=lambda t: lerp(at_top(0.705, -0.004), (0, y_of(0.700), top_z(0.70) + 0.024), t),
    normal=X, thickness=0.008, rows=2)
build_fin(
    leading=lambda t: lerp(at_bottom(0.665, -0.006), (0, y_of(0.700), bottom_z(0.69) - 0.026), t),
    trailing=lambda t: lerp(at_bottom(0.715, -0.004), (0, y_of(0.712), bottom_z(0.71) - 0.024), t),
    normal=X, thickness=0.008, rows=2)

# Caudal fin (heterocercal): long upper lobe along the raised spine, shorter lower lobe.
build_fin(  # upper lobe
    leading=lambda t: bez((0, y_of(0.760), top_z(0.76) - 0.006), (0, y_of(0.880), 0.080), (0, y_of(1.000), 0.170), t),
    trailing=lambda t: bez((0, y_of(0.835), 0.010), (0, y_of(0.905), 0.060), (0, y_of(0.990), 0.163), t),
    normal=X, thickness=0.016, rows=6)
build_fin(  # lower lobe
    leading=lambda t: bez((0, y_of(0.775), bottom_z(0.775) + 0.004), (0, y_of(0.830), -0.060), (0, y_of(0.905), -0.118), t),
    trailing=lambda t: bez((0, y_of(0.840), 0.012), (0, y_of(0.860), -0.050), (0, y_of(0.900), -0.112), t),
    normal=X, thickness=0.014, rows=4)


def pectoral(mirror):
    # Root on the lower flank, behind the gills; the fin sweeps outward, back and slightly down.
    def root(s):
        w, ht, hb, c = profile_at(s)
        return (w * 0.80, y_of(s), c - hb * 0.45)
    out = np.array([1.0, 0.0, -0.32]); out /= np.linalg.norm(out)
    nrm = tuple(np.cross(out, [0.0, 1.0, 0.0]))   # points up-ish
    def L(t):
        r = np.array(root(0.215))
        tip = r + out * 0.200 + np.array([0, -0.120, 0])
        mid = r + out * 0.110 + np.array([0, -0.020, 0])
        return bez(tuple(r), tuple(mid), tuple(tip), t)
    def T(t):
        r = np.array(root(0.300))
        tip = np.array(L(1.0)) + np.array([0, -0.010, 0.0])
        mid = r + out * 0.080 + np.array([0, -0.050, 0])
        return bez(tuple(r), tuple(mid), tuple(tip), t)
    build_fin(L, T, nrm, 0.022, rows=5, uv_mode="flat", mirror_x=mirror)


def pelvic(mirror):
    def root(s):
        w, ht, hb, c = profile_at(s)
        return (w * 0.45, y_of(s), c - hb * 0.85)
    out = np.array([0.7, 0.0, -0.7]); out /= np.linalg.norm(out)
    nrm = tuple(np.cross(out, [0.0, 1.0, 0.0]))
    L = lambda t: lerp(root(0.560), tuple(np.array(root(0.560)) + out * 0.045 + np.array([0, -0.040, 0])), t)
    T = lambda t: lerp(root(0.610), tuple(np.array(root(0.560)) + out * 0.043 + np.array([0, -0.050, 0])), t)
    build_fin(L, T, nrm, 0.010, rows=2, uv_mode="flat", mirror_x=mirror)


for m in (False, True):
    pectoral(m)
    pelvic(m)

# ---- UVs, normals ----
for f, uvs in faces_uv:          # UVs first: flipping a face below keeps each loop's UV with its vertex
    for loop, uv in zip(f.loops, uvs):
        loop[uv_layer].uv = uv
bm.normal_update()
# Body faces point away from the body axis (the fins' winding is set from their side when they are built).
body_faces = [f for f, _ in faces_uv][: RADIAL * (len(rings) + 1)]
for f in body_faces:
    c = f.calc_center_median()
    axis = np.array([0.0, c.y, profile_at(0.5 - c.y)[3]])
    if np.dot(np.array(f.normal), np.array(c) - axis) < 0:
        f.normal_flip()

# ---------------------------------------------------------------------------------------------------------
# Texture
def noise2(h, w, cells, seed):
    rng = np.random.default_rng(seed)
    g = rng.random((cells + 1, cells * 2 + 1))
    ys = np.linspace(0, cells, h); xs = np.linspace(0, cells * 2, w)
    y0 = np.floor(ys).astype(int).clip(0, cells - 1); x0 = np.floor(xs).astype(int).clip(0, cells * 2 - 1)
    fy = (ys - y0)[:, None]; fx = (xs - x0)[None, :]
    fy = fy * fy * (3 - 2 * fy); fx = fx * fx * (3 - 2 * fx)
    a = g[y0][:, x0]; b = g[y0][:, x0 + 1]; c = g[y0 + 1][:, x0]; d = g[y0 + 1][:, x0 + 1]
    return a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


N = TEX_SIZE
img = np.zeros((N, N, 3))
back = np.array([0.30, 0.34, 0.37])      # slate grey with a hint of bronze
back_dark = np.array([0.20, 0.23, 0.26])
belly = np.array([0.90, 0.90, 0.88])
tipc = np.array([0.07, 0.08, 0.09])

# top half: side view
H = N // 2
vv, uu = np.mgrid[0:H, 0:N]
v = 0.5 + 0.5 * (vv + 0.5) / H          # texture v of each pixel in the top half (row 0 = v 0.5)
u = (uu + 0.5) / N
s = 1.0 - u
z = Z_MIN + (v - 0.5) / 0.5 * (Z_MAX - Z_MIN)
cz = np.interp(s, [p[0] for p in PROFILE], [p[4] for p in PROFILE])
ht = np.interp(s, [p[0] for p in PROFILE], [p[2] for p in PROFILE])
hb = np.interp(s, [p[0] for p in PROFILE], [p[3] for p in PROFILE])
n1 = noise2(H, N, 24, 1); n2 = noise2(H, N, 90, 2)
# counter-shading line a little below the mid-flank, gently wavy
line = cz - hb * 0.22 + 0.003 * np.sin(s * 30)
w_belly = smooth(0.008, -0.008, z - line + (n1 - 0.5) * 0.006)
# the fins outside the body outline (dorsals, caudal lobes, anal) stay grey on both sides
tz = cz + ht
bz = cz - hb
on_fin = np.clip(smooth(0.002, 0.008, z - tz) + smooth(0.002, 0.008, bz - z) + smooth(0.835, 0.85, s), 0, 1)
w_belly = w_belly * (1 - on_fin)
up = np.clip((z - cz) / np.maximum(ht, 1e-3), 0, 1)
col = back[None, None, :] * (1 - w_belly[..., None]) + belly[None, None, :] * w_belly[..., None]
col = col - ((1 - w_belly) * (1 - on_fin) * up)[..., None] * (back - back_dark)
col *= (0.92 + 0.10 * n1 + 0.05 * n2)[..., None]
# fin markings: dark tip of the first dorsal, black trailing edge and tips on the caudal fin
dark = smooth(0.08, 0.11, z - tz) * smooth(0.30, 0.38, s) * (1 - smooth(0.46, 0.50, s))
cauda = smooth(0.80, 0.84, s) * (smooth(0.11, 0.16, z) + smooth(-0.085, -0.11, z))
edge = smooth(0.85, 0.99, s - 0.18 * np.clip((z - 0.06) / 0.11, 0, 1) + 0.04 * np.clip(-z / 0.1, 0, 1)) * smooth(0.82, 0.86, s)
dark = np.clip(dark + cauda + edge * 0.9, 0, 1) * 0.85
col = col * (1 - dark[..., None]) + tipc * dark[..., None]
# eye
ex, ez = 0.068, cz.mean() * 0 + 0.012
d_eye = np.sqrt(((s - 0.068) / 0.0075) ** 2 + ((z - 0.014) / 0.0060) ** 2)
eye = smooth(1.15, 0.85, d_eye)
col = col * (1 - eye[..., None]) + np.array([0.02, 0.02, 0.025]) * eye[..., None]
glint = smooth(0.5, 0.2, np.sqrt(((s - 0.065) / 0.0025) ** 2 + ((z - 0.0165) / 0.002) ** 2))
col = col + glint[..., None] * 0.5
# five gill slits, slightly curved
for i in range(5):
    gs = 0.175 + i * 0.013
    gz0, gz1 = -0.035, 0.030 - i * 0.002
    inside = smooth(gz0 - 0.004, gz0 + 0.004, z) * smooth(gz1 + 0.004, gz1 - 0.004, z)
    sx = gs + 0.004 * ((z - (gz0 + gz1) / 2) / 0.03) ** 2
    slit = smooth(0.0030, 0.0008, np.abs(s - sx)) * inside
    col = col * (1 - 0.55 * slit[..., None])
# mouth line on the underside of the snout
mouth = smooth(0.003, 0.0, np.abs(z - (cz - hb * 0.85 + (s - 0.08) * 0.15))) * smooth(0.03, 0.05, s) * smooth(0.115, 0.09, s)
col = col * (1 - 0.4 * mouth[..., None])
img[H:, :, :] = np.clip(col, 0, 1)

# bottom half: horizontal fins. Left cell = top side (grey, dark tip), right cell = underside (pale, dark tip).
vv, uu = np.mgrid[0:H, 0:N]
fv = np.clip(((vv + 0.5) / N - 0.02) / 0.42, 0, 1)        # span
cellf = ((uu + 0.5) / N % 0.5 - 0.02) / 0.46               # chord
under = (uu >= N // 2)[..., None]
n3 = noise2(H, N, 20, 3)
base = np.where(under, belly * 0.95, back)
tip = smooth(0.72, 0.95, fv + 0.25 * cellf)
trail = smooth(0.80, 1.0, cellf) * 0.6
mk = np.clip(tip + trail * (0.5 + 0.5 * fv), 0, 1)[..., None] * 0.85
fins = base * (1 - mk) + tipc * mk
fins *= (0.93 + 0.1 * n3)[..., None]
img[:H, :, :] = np.clip(fins, 0, 1)

image = bpy.data.images.get(NAME + "_Texture") or bpy.data.images.new(NAME + "_Texture", N, N)
image.scale(N, N)
rgba = np.concatenate([img, np.ones((N, N, 1))], axis=2).astype(np.float32)
image.pixels.foreach_set(rgba.ravel())
image.filepath_raw = OUT_DIR + "\\" + NAME + ".png"
image.file_format = "PNG"
image.save()

# ---------------------------------------------------------------------------------------------------------
# Object
old = bpy.data.objects.get(NAME)
if old:
    bpy.data.objects.remove(old, do_unlink=True)
me = bpy.data.meshes.get(NAME)
if me:
    bpy.data.meshes.remove(me)
me = bpy.data.meshes.new(NAME)
bm.to_mesh(me)
bm.free()
me.shade_smooth()
obj = bpy.data.objects.new(NAME, me)
bpy.context.scene.collection.objects.link(obj)

mat = bpy.data.materials.get(NAME) or bpy.data.materials.new(NAME)
mat.use_nodes = True
nt = mat.node_tree
bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
tex = next((n for n in nt.nodes if n.type == "TEX_IMAGE"), None) or nt.nodes.new("ShaderNodeTexImage")
tex.image = image
nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
bsdf.inputs["Roughness"].default_value = 0.45
mat.use_backface_culling = True   # like Unity's Cull Back: reveals any face with the wrong winding
me.materials.clear()
me.materials.append(mat)

# sharp-ish fins vs smooth body: smooth by angle
try:
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(50))
except Exception:
    pass

tris = sum(len(p.vertices) - 2 for p in me.polygons)
result = {"verts": len(me.vertices), "tris": tris, "texture": image.filepath_raw}
