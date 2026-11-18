"""
Procedural corals for Sea Simulator: five low-poly pieces to scatter on the seabed, sharing one small
texture (Corals.png) and so one material in Unity.

  Coral_Branching  staghorn-like branching coral, purple with pale tips      (~0.9 m)
  Coral_Brain      brain coral dome, labyrinth of ridges and valleys         (~0.8 m)
  Coral_Table      table coral (Acropora): short trunk, wide lobed plate     (~1.6 m across)
  Coral_Fan        sea fan (gorgonian): a flat lattice of thin branches, red (~1.2 m)
  Coral_Tubes      cluster of hollow tube sponges, orange                    (~0.7 m)

Textures (1024 x 1024): Corals.png (colour) and Corals_Normal.png (tangent-space normal map, OpenGL / Y+),
same layout:
  - top-left quadrant    : one vertical gradient column per coral (v = 0 base / inside .. 1 tips / rim);
  - top-right quadrant   : top of the table coral, seen from above (a disc of tiny upright branch tips);
  - bottom-left quadrant : the brain coral, mapped from its summit (azimuthal projection): a maze made by
                           a Gray-Scott reaction-diffusion simulation, the same process that draws real
                           labyrinth patterns;
  - bottom-right quadrant: underside of the table coral (radiating branches).
Every piece has its origin at the centre of its base (z = 0 on the ground). Units: metres, Z up.
"""
import math
import random
import bpy
import bmesh
import numpy as np
from mathutils import Vector, Matrix, noise

OUT_DIR = globals().get("OUT_DIR")
TEX = 1024
COLUMNS = 8          # gradient columns in the top-left quadrant of the texture


def column_u(col, jitter=True):
    w = 0.5 / COLUMNS
    return col * w + w * (0.5 + (random.uniform(-0.3, 0.3) if jitter else 0))


def disc_uv(quadrant, x, y):
    """UV in a quadrant disc: quadrant = (u0, v0) of its corner, (x, y) in -1..1."""
    return (quadrant[0] + 0.25 + 0.235 * x, quadrant[1] + 0.25 + 0.235 * y)


Q_TABLE_TOP, Q_BRAIN, Q_TABLE_UNDER = (0.5, 0.5), (0.0, 0.0), (0.5, 0.0)


def grad_uv(col, v):
    """UV into gradient column `col`, at height v (0..1)."""
    return (column_u(col), 0.5 + 0.48 * min(max(v, 0.0), 1.0) + 0.01)


# ------------------------------------------------------------------------------------------------ helpers
class Builder:
    """Collects vertices with their UV and faces, then makes a mesh object."""

    def __init__(self):
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")
        self.vuv = {}

    def vert(self, co, uv):
        v = self.bm.verts.new(co)
        self.vuv[v] = uv
        return v

    def face(self, verts):
        try:
            return self.bm.faces.new(verts)
        except ValueError:
            return None

    def finish(self, name, smooth_angle=50):
        for f in self.bm.faces:
            for loop in f.loops:
                loop[self.uv].uv = self.vuv[loop.vert]
        bmesh.ops.remove_doubles(self.bm, verts=self.bm.verts, dist=1e-5)
        old = bpy.data.objects.get(name)
        if old:
            me = old.data
            bpy.data.objects.remove(old, do_unlink=True)
            bpy.data.meshes.remove(me)
        me = bpy.data.meshes.new(name)
        self.bm.normal_update()
        self.bm.to_mesh(me)
        self.bm.free()
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
        bpy.ops.object.select_all(action="DESELECT")
        ob.select_set(True)
        bpy.context.view_layer.objects.active = ob
        bpy.ops.object.shade_smooth_by_angle(angle=math.radians(smooth_angle))
        return ob


def frame(direction):
    d = Vector(direction).normalized()
    a = Vector((1, 0, 0)) if abs(d.x) < 0.9 else Vector((0, 1, 0))
    s = d.cross(a).normalized()
    return s, d.cross(s)


def tube(b, points, radii, vs, col, sides=6, cap=True, base_cap=False):
    """A tapered tube through `points` (list of Vector), closed by a point at the end."""
    rings = []
    for i, p in enumerate(points):
        d = (points[min(i + 1, len(points) - 1)] - points[max(i - 1, 0)])
        s, t = frame(d)
        ring = []
        for k in range(sides):
            a = 2 * math.pi * k / sides
            ring.append(b.vert(p + (s * math.cos(a) + t * math.sin(a)) * radii[i], grad_uv(col, vs[i])))
        rings.append(ring)
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(sides):
            k2 = (k + 1) % sides
            b.face([r0[k], r0[k2], r1[k2], r1[k]])
    if cap:
        d = (points[-1] - points[-2]).normalized()
        tip = b.vert(points[-1] + d * radii[-1] * 1.2, grad_uv(col, vs[-1]))
        for k in range(sides):
            b.face([rings[-1][k], rings[-1][(k + 1) % sides], tip])
    if base_cap:
        b.face(list(reversed(rings[0])))


# ------------------------------------------------------------------------------------------------ branching coral
def branching(name="Coral_Branching", col=0, seed=3):
    random.seed(seed)
    b = Builder()
    max_depth = 3

    def grow(p, d, length, radius, depth, v0):
        n = 3
        pts, rad, vs = [p], [radius], [v0]
        cur, dirv = Vector(p), Vector(d)
        for i in range(n):
            dirv = (dirv + Vector((random.gauss(0, 0.25), random.gauss(0, 0.25), 0.25))).normalized()
            cur = cur + dirv * (length / n)
            pts.append(cur.copy())
            rad.append(radius * (1 - 0.35 * (i + 1) / n))
            vs.append(v0 + (1 - v0) * (i + 1) / n * (0.35 if depth < max_depth else 1.0))
        tube(b, pts, rad, vs, col, sides=6, cap=True)
        if depth >= max_depth:
            return
        kids = random.choice((2, 3, 3))
        for k in range(kids):
            spread = math.radians(random.uniform(30, 50))
            az = 2 * math.pi * (k / kids) + random.uniform(-0.5, 0.5)
            s, t = frame(dirv)
            nd = (dirv * math.cos(spread) + (s * math.cos(az) + t * math.sin(az)) * math.sin(spread)).normalized()
            start = pts[-1 - random.choice((0, 1))]
            grow(start, nd, length * random.uniform(0.7, 0.85), rad[-1] * 0.95, depth + 1, vs[-1])

    for k in range(4):
        a = k * math.pi / 2 + random.uniform(-0.4, 0.4)
        d = Vector((math.cos(a) * 0.5, math.sin(a) * 0.5, 1.0)).normalized()
        grow(Vector((math.cos(a) * 0.08, math.sin(a) * 0.08, -0.05)), d, 0.36, 0.05, 1, 0.0)
    return b.finish(name)


# ------------------------------------------------------------------------------------------------ brain coral
def brain(name="Coral_Brain", radius=0.42, height=0.32, rings=18, seg=48, phi_max=1.85):
    """
    A dome built on a polar grid (rings around the summit), so its UVs are an azimuthal projection of the
    surface: rho = polar angle / phi_max goes from the summit (0) to the base (1). The ridges and valleys
    come from the texture and its normal map; the geometry only carries the large, gentle lumps.
    """
    b = Builder()
    z_base = math.cos(phi_max)

    def point(phi, a):
        lump = 1 + 0.07 * noise.noise(Vector((math.cos(a) * 1.3, math.sin(a) * 1.3, phi * 1.1 + 4.0)))
        r = radius * math.sin(phi) * lump
        z = height * (math.cos(phi) - z_base) / (1 - z_base) * lump
        return Vector((r * math.cos(a), r * math.sin(a), z))

    summit = b.vert(point(0.0, 0.0), disc_uv(Q_BRAIN, 0, 0))
    grid = []
    for i in range(1, rings + 1):
        phi = phi_max * i / rings
        rho = i / rings
        grid.append([b.vert(point(phi, 2 * math.pi * k / seg),
                            disc_uv(Q_BRAIN, rho * math.cos(2 * math.pi * k / seg), rho * math.sin(2 * math.pi * k / seg)))
                     for k in range(seg)])
    for k in range(seg):
        b.face([summit, grid[0][k], grid[0][(k + 1) % seg]])
    for r0, r1 in zip(grid, grid[1:]):
        for k in range(seg):
            k2 = (k + 1) % seg
            b.face([r0[k], r1[k], r1[k2], r0[k2]])
    # flat underside, in the dark base colour of its gradient column
    base = [b.vert(v.co.copy(), grad_uv(1, 0.0)) for v in grid[-1]]
    b.face(list(reversed(base)))
    return b.finish(name, smooth_angle=80)


# ------------------------------------------------------------------------------------------------ table coral
def table(name="Coral_Table", col=2, seed=5, radius=0.8, height=0.42, rings=8, seg=36):
    """
    Acropora table: a short trunk flaring into a wide plate with an irregular, lobed outline, slightly
    turned up at its growing edge. Top and underside are mapped from above into their own texture discs.
    """
    random.seed(seed)
    b = Builder()
    trunk = [Vector((0, 0, -0.05)), Vector((0.03, 0.0, 0.16)), Vector((0.0, 0.02, height - 0.03))]
    tube(b, trunk, [0.13, 0.10, 0.12], [0.0, 0.5, 0.9], col, sides=10, cap=False)

    phase = [random.uniform(0, 6.3) for _ in range(4)]

    def outline(a):
        return 1 + 0.10 * math.sin(3 * a + phase[0]) + 0.07 * math.sin(5 * a + phase[1]) + 0.04 * math.sin(9 * a + phase[2])

    r_max = radius * 1.22
    thick = 0.05
    top, bottom = [], []
    for r in range(rings + 1):
        f = r / rings
        rt, rb = [], []
        for k in range(seg):
            a = 2 * math.pi * k / seg
            rr = (0.10 + (radius - 0.10) * f) * (outline(a) if r else 1)
            z = height + 0.06 * f + 0.06 * f ** 4 + 0.03 * math.sin(4 * a + phase[3]) * f * f
            p = Vector((rr * math.cos(a), rr * math.sin(a), z))
            ux, uy = rr / r_max * math.cos(a), rr / r_max * math.sin(a)
            rt.append(b.vert(p + Vector((0, 0, thick * (1 - 0.55 * f))), disc_uv(Q_TABLE_TOP, ux, uy)))
            rb.append(b.vert(p, disc_uv(Q_TABLE_UNDER, ux, uy)))
        top.append(rt)
        bottom.append(rb)
    # windings: k runs counter-clockwise seen from above, r outward; the top faces up, the underside down
    for r in range(rings):
        for k in range(seg):
            k2 = (k + 1) % seg
            b.face([top[r][k], top[r + 1][k], top[r + 1][k2], top[r][k2]])
            b.face([bottom[r][k], bottom[r][k2], bottom[r + 1][k2], bottom[r + 1][k]])
    # growing edge (rim): its own vertices, mapped just inside the edge of the top disc (pale tips),
    # so the band does not stretch across the texture between the top and underside discs
    rim_top, rim_bottom = [], []
    for k in range(seg):
        u, v = b.vuv[top[-1][k]]
        cu, cv = Q_TABLE_TOP[0] + 0.25, Q_TABLE_TOP[1] + 0.25
        inner = (cu + (u - cu) * 0.97, cv + (v - cv) * 0.97)
        rim_top.append(b.vert(top[-1][k].co.copy(), (u, v)))
        rim_bottom.append(b.vert(bottom[-1][k].co.copy(), inner))
    for k in range(seg):
        k2 = (k + 1) % seg
        b.face([rim_top[k], rim_bottom[k], rim_bottom[k2], rim_top[k2]])
    b.face(top[0])
    b.face(list(reversed(bottom[0])))
    return b.finish(name, smooth_angle=60)


# ------------------------------------------------------------------------------------------------ sea fan
def fan(name="Coral_Fan", col=3, seed=8, height=1.15):
    random.seed(seed)
    b = Builder()
    max_depth = 5

    def grow(p, ang, length, radius, depth, v0):
        n = 2
        pts, rad, vs = [p], [radius], [v0]
        cur = Vector(p)
        for i in range(n):
            a = ang + random.gauss(0, 0.12)
            cur = cur + Vector((math.sin(a), random.gauss(0, 0.04), math.cos(a))) * (length / n)
            pts.append(cur.copy())
            rad.append(radius * (1 - 0.2 * (i + 1) / n))
            vs.append(min(1.0, v0 + 0.2 * (i + 1) / n))
        tube(b, pts, rad, vs, col, sides=4, cap=True)
        if depth >= max_depth:
            return
        for side in (-1, 1):
            if depth > 1 and random.random() < 0.15:
                continue
            grow(pts[-1], ang + side * random.uniform(0.25, 0.5), length * random.uniform(0.78, 0.9),
                 rad[-1] * 0.85, depth + 1, vs[-1])

    tube(b, [Vector((0, 0, -0.05)), Vector((0, 0, 0.12))], [0.035, 0.03], [0.0, 0.1], col, sides=6, cap=False)
    for a in (-0.7, -0.3, 0.05, 0.4, 0.75):
        grow(Vector((0, 0, 0.1)), a, height * 0.22, 0.02, 2, 0.1)
    return b.finish(name, smooth_angle=70)


# ------------------------------------------------------------------------------------------------ tube sponges
def tubes(name="Coral_Tubes", col=4, seed=13):
    random.seed(seed)
    b = Builder()
    sides = 10
    for i in range(5):
        a = 2 * math.pi * i / 5 + random.uniform(-0.3, 0.3)
        d = 0.0 if i == 0 else random.uniform(0.10, 0.17)
        base = Vector((math.cos(a) * d, math.sin(a) * d, -0.03))
        h = random.uniform(0.35, 0.7) * (1.15 if i == 0 else 1.0)
        lean = Vector((math.cos(a) * 0.25 * (d > 0), math.sin(a) * 0.25 * (d > 0), 1.0)).normalized()
        r0 = random.uniform(0.055, 0.08)
        wall = 0.018
        levels = 4
        outer, inner = [], []
        for l in range(levels + 1):
            f = l / levels
            c = base + lean * h * f
            r = r0 * (1 + 0.35 * f * f)       # flared mouth
            s, t = frame(lean)
            ro, ri = [], []
            for k in range(sides):
                ang = 2 * math.pi * k / sides
                off = s * math.cos(ang) + t * math.sin(ang)
                ro.append(b.vert(c + off * r, grad_uv(col, 0.3 + 0.7 * f)))
                if l == levels or l >= 1:
                    ri.append(b.vert(c + off * (r - wall), grad_uv(col, 0.02 + 0.25 * f)))
                else:
                    ri.append(None)
            outer.append(ro)
            inner.append(ri)
        for l in range(levels):
            for k in range(sides):
                k2 = (k + 1) % sides
                b.face([outer[l][k], outer[l][k2], outer[l + 1][k2], outer[l + 1][k]])
                if l >= 1:
                    b.face([inner[l][k], inner[l + 1][k], inner[l + 1][k2], inner[l][k2]])
        for k in range(sides):     # rim
            k2 = (k + 1) % sides
            b.face([outer[-1][k], outer[-1][k2], inner[-1][k2], inner[-1][k]])
        b.face(list(inner[1]))     # inside bottom of the tube
        b.face(list(reversed(outer[0])))
    return b.finish(name, smooth_angle=60)




# ------------------------------------------------------------------------------------------------ texture
def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def blur(a, sigma):
    """Gaussian blur (periodic), in Fourier space. sigma in pixels."""
    fy = np.fft.fftfreq(a.shape[0])[:, None]
    fx = np.fft.fftfreq(a.shape[1])[None, :]
    k = np.exp(-2 * math.pi ** 2 * sigma ** 2 * (fx * fx + fy * fy))
    return np.real(np.fft.ifft2(np.fft.fft2(a) * k))


def resize_periodic(a, n):
    """Bilinear resize of a tileable square array to n x n."""
    m = a.shape[0]
    c = np.arange(n) * m / n
    i0 = np.floor(c).astype(int)
    f = c - i0
    i1 = (i0 + 1) % m
    rows = a[i0] * (1 - f)[:, None] + a[i1] * f[:, None]
    return rows[:, i0] * (1 - f)[None, :] + rows[:, i1] * f[None, :]


def gray_scott(n=384, steps=6000, feed=0.029, kill=0.057, seed=1):
    """
    Gray-Scott reaction-diffusion on a tileable grid. With this feed / kill pair the two chemicals settle
    into a labyrinth of stripes of even width (about 10 cells apart): the pattern of a brain coral.
    Seeded with small patches everywhere so the maze forms over the whole grid at once.
    Returns V normalised to 0..1.
    """
    rng = np.random.default_rng(seed)
    u = np.ones((n, n))
    v = np.zeros((n, n))
    seeds = blur(rng.random((n, n)), 2.0)
    seeds = seeds > np.quantile(seeds, 0.8)
    u[seeds], v[seeds] = 0.5, 0.25
    u += rng.normal(0, 0.02, (n, n))
    v += np.clip(rng.normal(0, 0.02, (n, n)), 0, None)
    du, dv = 0.2, 0.1

    def lap(z):
        return np.roll(z, 1, 0) + np.roll(z, -1, 0) + np.roll(z, 1, 1) + np.roll(z, -1, 1) - 4 * z

    for _ in range(steps):
        uvv = u * v * v
        u += du * lap(u) - uvv + feed * (1 - u)
        v += dv * lap(v) + uvv - (feed + kill) * v
    return (v - v.min()) / (v.max() - v.min())


def normal_from_height(h, strength):
    """Tangent-space normal map (OpenGL, Y+) from a height field whose rows run along +v."""
    dx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5
    dy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * 0.5
    n = np.stack([-dx * strength, -dy * strength, np.ones_like(h)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def lerp_col(a, b, t):
    return np.array(a) * (1 - t[..., None]) + np.array(b) * t[..., None]


def disc_coords(q):
    """Polar coordinates (r = 0..1 at the disc edge, angle) of the pixels of a q x q quadrant disc."""
    yy, xx = np.mgrid[0:q, 0:q]
    cx = cy = q * 0.5 - 0.5
    rad = 0.235 * 2 * q          # disc radius in pixels (see disc_uv)
    dx, dy = (xx - cx) / rad, (yy - cy) / rad
    return np.hypot(dx, dy), np.arctan2(dy, dx)


def brain_quadrant(q, rng):
    maze = resize_periodic(gray_scott(), q)
    ridge = blur(smooth(0.30, 0.60, maze), 1.2)              # 1 on the ridges, 0 in the valleys
    r, a = disc_coords(q)
    patches = blur(rng.standard_normal((q, q)), 40)
    patches /= patches.std()
    valley = lerp_col((0.26, 0.30, 0.15), (0.34, 0.30, 0.14), smooth(-1, 1, patches))
    ridge_c = lerp_col((0.62, 0.52, 0.30), (0.70, 0.62, 0.36), smooth(-1, 1, -patches))
    col = valley * (1 - ridge[..., None]) + ridge_c * ridge[..., None]
    crest = smooth(0.80, 0.97, ridge)                         # thin pale line along each ridge top
    col = col * (1 - 0.35 * crest[..., None]) + np.array((0.86, 0.80, 0.60)) * 0.35 * crest[..., None]
    col *= (1 - 0.35 * smooth(0.82, 1.0, r))[..., None]      # darker toward the base, near the sand
    col *= (0.93 + 0.12 * rng.random((q, q)))[..., None]
    return col, normal_from_height(ridge, 2.2)


def table_quadrants(q, rng):
    """Top: a carpet of tiny upright branch tips. Underside: branches radiating from the trunk."""
    r, a = disc_coords(q)
    # branch tips: jittered grid of small bumps, about 5 px (2 cm) apart
    step = 5
    gy, gx = np.mgrid[0:q:step, 0:q:step]
    px = (gx + rng.uniform(-1.6, 1.6, gx.shape)).astype(int) % q
    py = (gy + rng.uniform(-1.6, 1.6, gy.shape)).astype(int) % q
    dots = np.zeros((q, q))
    np.add.at(dots, (py.ravel(), px.ravel()), 1.0)
    tips = blur(dots, 1.3)
    tips /= tips.max()
    streak_noise = blur(rng.standard_normal((q, q)), 25)
    streak_noise /= streak_noise.std()
    streaks = 0.5 + 0.5 * np.sin(a * 70 + 2.5 * streak_noise)
    height = np.clip(tips * 1.4, 0, 1) * (0.75 + 0.25 * streaks)
    patches = blur(rng.standard_normal((q, q)), 50)
    patches /= patches.std()
    edge = smooth(0.70, 0.95, r)                              # growing edge: paler, bluish tips
    base = lerp_col((0.40, 0.37, 0.27), (0.47, 0.43, 0.30), smooth(-1, 1, patches))
    tip_c = lerp_col((0.86, 0.82, 0.66), (0.72, 0.74, 0.94), edge)
    base = base * (1 - edge[..., None] * 0.4) + np.array((0.55, 0.55, 0.68)) * edge[..., None] * 0.4
    t = smooth(0.10, 0.60, height)
    top = base * (1 - t[..., None]) + tip_c * t[..., None]
    top *= (0.85 + 0.15 * smooth(0.0, 0.5, r))[..., None]    # older, darker centre
    top *= (0.93 + 0.12 * rng.random((q, q)))[..., None]
    top_n = normal_from_height(blur(height, 0.6), 3.0)

    ribs = 0.5 + 0.5 * np.sin(a * 46 + 3.0 * streak_noise + r * 4)
    ribs = blur(smooth(0.35, 0.9, ribs), 1.0)
    under = lerp_col((0.28, 0.25, 0.18), (0.46, 0.42, 0.31), ribs * 0.35 + 0.4 * smooth(0.6, 1.0, r))
    under *= (0.92 + 0.12 * rng.random((q, q)))[..., None]
    under_n = normal_from_height(ribs, 2.0)
    return top, top_n, under, under_n


def build_textures():
    rng = np.random.default_rng(4)
    img = np.zeros((TEX, TEX, 3))
    nrm = np.zeros((TEX, TEX, 3))
    nrm[...] = (0.5, 0.5, 1.0)
    q = TEX // 2
    # top-left quadrant: gradient columns, base -> tip. (column, [(v, colour), ...])
    gradients = {
        0: [(0.0, (0.20, 0.10, 0.25)), (0.6, (0.55, 0.28, 0.62)), (1.0, (0.92, 0.75, 0.95))],   # branching: purple
        1: [(0.0, (0.18, 0.18, 0.10)), (1.0, (0.30, 0.30, 0.16))],                              # brain underside
        2: [(0.0, (0.28, 0.25, 0.18)), (0.6, (0.42, 0.38, 0.28)), (1.0, (0.50, 0.46, 0.34))],   # table trunk
        3: [(0.0, (0.35, 0.06, 0.06)), (0.5, (0.78, 0.18, 0.12)), (1.0, (0.98, 0.52, 0.30))],   # fan: red-orange
        4: [(0.0, (0.18, 0.06, 0.02)), (0.3, (0.55, 0.22, 0.05)), (1.0, (0.98, 0.62, 0.18))],   # tubes: orange, dark inside
        5: [(0.0, (0.15, 0.25, 0.10)), (1.0, (0.70, 0.85, 0.35))],                              # spare: green
        6: [(0.0, (0.30, 0.10, 0.15)), (1.0, (0.98, 0.55, 0.65))],                              # spare: pink
        7: [(0.0, (0.10, 0.15, 0.30)), (1.0, (0.45, 0.65, 0.98))],                              # spare: blue
    }
    v = (np.arange(q) + 0.5) / q
    cw = q // COLUMNS
    for col, stops in gradients.items():
        pos = [p for p, _ in stops]
        cols = np.array([c for _, c in stops])
        g = np.stack([np.interp((v - 0.02) / 0.96, pos, cols[:, i]) for i in range(3)], axis=1)
        block = np.repeat(g[:, None, :], cw, axis=1)
        block *= (0.9 + 0.2 * rng.random((q, cw, 1)))     # speckle
        img[q:, col * cw:(col + 1) * cw] = block

    def put(quadrant, colour, normal):
        r0, c0 = int(quadrant[1] * TEX), int(quadrant[0] * TEX)
        img[r0:r0 + q, c0:c0 + q] = colour
        nrm[r0:r0 + q, c0:c0 + q] = normal

    put(Q_BRAIN, *brain_quadrant(q, rng))
    top, top_n, under, under_n = table_quadrants(q, rng)
    put(Q_TABLE_TOP, top, top_n)
    put(Q_TABLE_UNDER, under, under_n)

    images = {}
    for name, data, colourspace in (("Corals", np.clip(img, 0, 1), "sRGB"), ("Corals_Normal", nrm, "Non-Color")):
        image = bpy.data.images.get(name) or bpy.data.images.new(name, TEX, TEX)
        image.scale(TEX, TEX)
        image.colorspace_settings.name = colourspace
        rgba = np.concatenate([data, np.ones((TEX, TEX, 1))], axis=2).astype(np.float32)
        image.pixels.foreach_set(rgba.ravel())
        if OUT_DIR:
            image.filepath_raw = OUT_DIR + "\\" + name + ".png"
            image.file_format = "PNG"
            image.save()
        images[name] = image
    return images


def material(images):
    mat = bpy.data.materials.get("Coral") or bpy.data.materials.new("Coral")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(bsdf.outputs[0], out.inputs["Surface"])
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = images["Corals"]
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    ntex = nt.nodes.new("ShaderNodeTexImage")
    ntex.image = images["Corals_Normal"]
    nmap = nt.nodes.new("ShaderNodeNormalMap")
    nt.links.new(ntex.outputs["Color"], nmap.inputs["Color"])
    nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    bsdf.inputs["Roughness"].default_value = 0.75
    mat.use_backface_culling = True
    return mat


# ------------------------------------------------------------------------------------------------ build
images = build_textures()
mat = material(images)
pieces = [branching(), brain(), table(), fan(), tubes()]
info = {}
for i, ob in enumerate(pieces):
    ob.data.materials.clear()
    ob.data.materials.append(mat)
    ob.location = (-4.0 + 2.0 * i, 12.0, 0.0)      # side by side, away from the wreck
    info[ob.name] = sum(len(p.vertices) - 2 for p in ob.data.polygons)
result = {"tris": info}
