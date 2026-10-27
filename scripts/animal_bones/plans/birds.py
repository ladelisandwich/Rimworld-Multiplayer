"""Birds plan: skeletons for the two-legged birds (Multiplayer port, not shipped).

Species: Chicken, Duck, Goose, Turkey (fowl), Emu, Ostrich, Cassowary (ratites) and the Penguin.

Built from the living model and the ModelDef. The head and neck are built in FILE axes (the bird faces -z);
the trunk (spine, ribcage, keel, pelvis, tail, wings) in a TRUNK frame and turned into file axes at the end:

  trunk frame   local x = sideways, local y = dorsal (the back), local z = along the trunk from the chest
                (negative) to the tail (positive). A horizontal bird's trunk frame IS file axes. The Penguin
                stands upright (its living trunk is taller than long), so its trunk axis runs from the hips to
                the head and its back is file +z. The living trunk is sliced across that axis, so every bird
                gets the same build.
  skull         the living head scaled to 0.85 as a loft through its cross-sections, rounded at the back and
                meeting the beak's base at the front; large an_dark eye sockets where the living eyes are
                (a bird's eyes look sideways): lenses on the big skulls (proud on the Penguin, sunk nearly
                flush on the ratites so a pale rim of skull shows round them), the skull's own faces painted on
                the small ones (see sockets()); the living beak copied as bone; the Cassowary's casque copied
                with its horn colour and set on the skull; thin lower jaw rami on the Penguin
  neck          vertebrae on an S curve: from the back of the skull down the living neck to a low bend in front
                of the chest, then back up into the spine at the shoulders; separate on the ratites and the
                Penguin, one loft of a few chunky vertebrae with bone_shade joints on the fowl
  spine         a rod along the back from the shoulders to the synsacrum (not on the fowl: covered by the
                ribcage and the pelvis)
  ribcage       compact and deep, rib ridges with bone_shade grooves; the keel (carina, deepest near the
                front) a separate blade under it on the Penguin and the loft's own lower half narrowing into
                the blade on the fowl; the ratites (no keel) get the raft sternum as the loft's broad flat floor
  shoulders     furcula (wishbone), coracoids and sabre-shaped scapulae: the Penguin only
  wings         one flat plate per side lying on the ribcage's upper side where the living folded wing lies
                (fowl, Ostrich), a small one on the front of the ribcage for the Emu's and the Cassowary's
                vestigial wings; flat flipper bones (humerus, radius and ulna, hand) along the Penguin's living
                flippers. (The fowl were thin rods once: on a body 0.2 cells wide their outlines merged into
                one dark mass, see FAMILY.)
  pelvis        a broad synsacrum-and-ilium block over the hips, deepest at the hip sockets (at hip height,
                over the legs' tops), a bone_shade groove along its top between the ilia and openings on its
                sides behind the sockets, thin pubis rods trailing back (Penguin only); a free tail vertebra and
                a small pygostyle tipped up, or (fowl) the loft going on into the living tail block as a stem
                and an upright pygostyle blade, so the bones end where the living bird does
  leg           one loft per leg file: hip knob at y = legLength, a short femur forward to the knee (not on the
                fowl, whose femur stays in the body), the tibiotarsus back down to the intertarsal joint, the
                tarsometatarsus down to the toes, centred on x = z = 0 like the living leg; the foot one
                star-shaped shell flat on the ground with the living foot's reach (3 forward toes and a hind
                toe for the fowl and the Penguin, 3 forward for the Emu and the Cassowary, 2 for the Ostrich;
                symmetric in x, since one leg model serves both sides)

Families set the proportions (FAMILY), OVERRIDES tunes single species. Triangle counts follow the spec's
budgets by size (animal.size): the Chicken is the only small bird.
"""

import math

import numpy as np

from .. import analyze
from .. import prims as P
from ..prims import BONE, DARK, SHADE

SPECIES_FAMILY = {
    "Chicken": "fowl", "Duck": "fowl", "Goose": "fowl", "Turkey": "fowl",
    "Emu": "ratite", "Ostrich": "ratite", "Cassowary": "ratite",
    "Penguin": "penguin",
}

SPECIES = set(SPECIES_FAMILY)

SHRINK = 0.85

# Toe sets: (angle from straight forward in degrees, + = toward +x; length as a share of the middle toe;
# True for a hind toe). Symmetric in x: the game draws the same leg model on both sides.
TOES_FOWL = [(-27.0, 0.85, False), (0.0, 1.0, False), (27.0, 0.85, False), (180.0, 0.45, True)]
TOES_THREE = [(-24.0, 0.85, False), (0.0, 1.0, False), (24.0, 0.85, False)]
TOES_TWO = [(-11.0, 1.0, False), (11.0, 0.88, False)]
TOES_PENGUIN = [(-28.0, 0.85, False), (0.0, 1.0, False), (28.0, 0.85, False), (180.0, 0.3, True)]

BASE = dict(
    # leg, as shares of legLength: knee (y, z) and intertarsal joint (y, z); file z, forward negative
    knee=(0.84, -0.22), ankle=(0.40, 0.10), leg_r=1.0, toes=TOES_FOWL,
    # the foot's top above the sole, in toe radii: at the toe tips, in the notches, at the hub (at least)
    toe_h=(0.45, 0.9, 1.6),
    keel=True, keel_depth=0.05, wings="folded", furcula=True, shoulders=True,
    neck=7, neck_r=0.30, neck_bow=0.10, neck_low=(0.04, 0.55), neck_sides=6, neck_loft=False,
    ribs=4, rib_frac=0.55, rib_w=0.80, sternum=0.30, groove=0.42,
    pel_pre=0.22, pel_post=0.36, pubis=True, spine=True, orbit=0.55, socket="proud", jaw=True,
    tail=1, tail_len=0.10, tail_dir=(0.45, 1.0), pygo=1.0, tail_aim=None,
)

FAMILY = {
    # fowl: few chunky pieces, since on a body 0.2 cells wide the 0.03 outline of every thin rod merged into
    # one dark mass round white flecks (most visibly on the side, as a dried corpse lies). The keel is part of
    # the ribcage's loft (its lower half narrows into the blade), each folded wing is one flat plate lying on
    # the ribcage's upper side, no spine rod (the ribcage and the pelvis cover the back), no shoulder rods,
    # pubis rods or jaw rami, a short neck of chunky vertebrae and a pygostyle reaching into the living tail
    "fowl": dict(orbit=0.48, keel="loft", spine=False, pubis=False, shoulders=False, furcula=False, jaw=False,
                 wings="plate", neck=4, neck_r=0.50, neck_sides=8, neck_loft=True, leg_r=1.6,
                 toe_h=(0.85, 1.35, 1.9), groove=0.32, tail_aim=(0.62, 0.5), pygo=1.0),
    # ratites: no keel (a flat raft sternum), no furcula, vestigial wings, long necks, long legs with a high
    # intertarsal joint, short tails
    "ratite": dict(knee=(0.88, -0.14), ankle=(0.50, 0.07), toes=TOES_THREE, keel="flat", wings="stub_plate",
                   furcula=False, neck=10, neck_r=0.28, neck_bow=0.15, neck_low=(0.06, 0.5), ribs=4,
                   sternum=0.14, pel_pre=0.24, pel_post=0.34, orbit=0.42, socket="flush", tail=1, tail_len=0.07,
                   tail_dir=(0.3, 1.0), pygo=0.7, pubis=False, shoulders=False),
    # the Penguin: upright, flippers, a deep keel, short legs (only the tarsometatarsus shows below the body)
    "penguin": dict(toes=TOES_PENGUIN, wings="flipper", neck=3, neck_bow=0.0, keel_depth=0.16, ribs=4,
                    rib_frac=0.58, rib_w=0.74, sternum=0.24, pel_pre=0.34, pel_post=0.10, orbit=0.50, tail=1,
                    tail_len=0.06, tail_dir=(1.0, 0.35), pygo=0.8),
}

OVERRIDES = {
    "Chicken": dict(neck=3, ribs=3, orbit=0.42),
    "Duck": dict(knee=(0.80, -0.24), ankle=(0.36, 0.11), ribs=3),
    "Goose": dict(neck_bow=0.12, ribs=3),
    # the Turkey's living tail is an upright fan: aim low into it
    "Turkey": dict(ribs=3, tail_aim=(0.55, 0.22), pygo=1.3),
    # the Ostrich's living model has real folded wings
    "Ostrich": dict(wings="plate", toes=TOES_TWO, neck=8),
    "Cassowary": dict(neck=9),
}


def params(name):
    p = dict(BASE)
    p.update(FAMILY[SPECIES_FAMILY[name]])
    p.update(OVERRIDES.get(name, {}))
    p["family"] = SPECIES_FAMILY[name]
    return p


def build(animal):
    p = params(animal.name)
    animal.overrides = p
    b = Bird(animal, p)
    out = {"body": b.body()}
    if animal.leg is not None:
        out["leg"] = b.leg()
    return out


# ---------------------------------------------------------------- small helpers

def lerp(a, b, t):
    return a + (b - a) * t


def catmull_rom(points, per=8):
    """A smooth curve through `points` (uniform Catmull-Rom, ends doubled), `per` samples per span."""
    Pt = [np.asarray(q, dtype=np.float64) for q in points]
    Pt = [Pt[0]] + Pt + [Pt[-1]]
    out = []
    for i in range(1, len(Pt) - 2):
        p0, p1, p2, p3 = Pt[i - 1], Pt[i], Pt[i + 1], Pt[i + 2]
        for k in range(per):
            t = k / per
            t2, t3 = t * t, t * t * t
            out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2
                              + (-p0 + 3 * p1 - 3 * p2 + p3) * t3))
    out.append(Pt[-2])
    return np.array(out)


def rod(a, b, r0, r1=None, sides=4, caps=None, name=""):
    """A thin straight bone: a `sides`-sided prism from a to b with short pointed ends."""
    r1 = r0 if r1 is None else r1
    caps = caps if caps is not None else (0.5 * r0, 0.5 * r1)
    return P.loft_path([a, b], [r0, r1], [r0, r1], sides=sides, caps=caps, name=name)


def surface_point(part, target):
    """The point of `part`'s surface nearest `target` on the x >= 0 side and the outward normal there (parts are
    wound clockwise from outside, so the outward normal is -cross). As quadruped.surface_point."""
    V = part.verts
    T = part.tris
    a, b, c = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
    ws = []
    k = 6
    for i in range(k + 1):
        for j in range(k + 1 - i):
            ws.append((i / k, j / k, 1.0 - i / k - j / k))
    ws = np.array(ws)
    pts = (ws[:, 0][None, :, None] * a[:, None, :] + ws[:, 1][None, :, None] * b[:, None, :]
           + ws[:, 2][None, :, None] * c[:, None, :])
    d = np.linalg.norm(pts - np.asarray(target)[None, None, :], axis=2)
    d[pts[:, :, 0] < -1e-9] = np.inf
    ti, wi = np.unravel_index(int(np.argmin(d)), d.shape)
    nrm = -np.cross(b[ti] - a[ti], c[ti] - a[ti])
    return pts[ti, wi], P.unit(nrm)


def copy_comps(mesh, comps, keep_colour=False):
    """Living pieces as closed parts (welded, wound clockwise), as bone or with their living colours."""
    out = []
    for c in comps:
        idx = c.idx
        col = mesh.color[idx] if keep_colour else None
        part = P.Part(mesh.tris[idx].reshape(-1, 3), np.arange(len(idx) * 3).reshape(-1, 3), BONE, col)
        out.append(P.orient(P.weld(part)))
    return out


def paint(part, sides, band, sectors, mat):
    """Sets the material of some sectors of one band of a loft (P.loft writes each band's triangles in sector
    order, two per sector, and orient() keeps that order). Sector 0 is centred on the top with the default
    angle0, sector sides/2 on the bottom."""
    for k in sectors:
        t = 2 * (band * sides + k % sides)
        part.mat[t] = mat
        part.mat[t + 1] = mat
    return part


def ear_clip(poly):
    """Triangles (index triples, same turn as the polygon) covering a simple polygon given as (x, z) points."""
    pts = [np.asarray(q, dtype=np.float64) for q in poly]
    idx = list(range(len(pts)))

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    area = sum(cross(np.zeros(2), pts[i], pts[(i + 1) % len(pts)]) for i in range(len(pts)))
    sgn = 1.0 if area > 0 else -1.0
    out = []
    guard = 0
    while len(idx) > 3 and guard < 1000:
        guard += 1
        for j in range(len(idx)):
            i0, i1, i2 = idx[j - 1], idx[j], idx[(j + 1) % len(idx)]
            a, b, c = pts[i0], pts[i1], pts[i2]
            if sgn * cross(a, b, c) <= 1e-12:
                continue
            inside = False
            for o in idx:
                if o in (i0, i1, i2):
                    continue
                q = pts[o]
                if (sgn * cross(a, b, q) >= 0 and sgn * cross(b, c, q) >= 0 and sgn * cross(c, a, q) >= 0):
                    inside = True
                    break
            if inside:
                continue
            out.append((i0, i1, i2))
            idx.pop(j)
            break
    out.append(tuple(idx))
    return out


def frames_const(n, T, U, V):
    return (np.tile(np.asarray(T, dtype=np.float64), (n, 1)), np.tile(np.asarray(U, dtype=np.float64), (n, 1)),
            np.tile(np.asarray(V, dtype=np.float64), (n, 1)))


# ---------------------------------------------------------------- the bird

class Bird:
    def __init__(self, animal, p):
        self.a = animal
        self.p = p
        self.size = animal.size
        self.small = self.size == "small"
        self.LL = float(animal.d["legLength"])
        mesh = animal.body
        self.mesh = mesh
        an = animal.an
        comps = an.comps

        def is_beak(c):
            return any("beak" in m.lower() for m in c.mats)

        def is_casque(c):
            return any(analyze.is_extra(m) for m in c.mats)

        self.beak = [c for c in comps if is_beak(c)]
        self.casque = [c for c in comps if is_casque(c) and not is_beak(c)]
        cand = [c for c in comps if c.kind != "dark" and not is_beak(c) and not is_casque(c)]
        nmax = max(c.n for c in cand)
        # the trunk: the big centred pieces (the Penguin's belly and back are two)
        self.trunk = [c for c in cand if c.n >= 0.6 * nmax and abs(c.centre[0]) < 0.03]
        ttris = np.concatenate([c.tris for c in self.trunk])
        tp = ttris.reshape(-1, 3)
        tlo, thi = tp.min(axis=0), tp.max(axis=0)
        # the head: the analysis' head pieces without the beak
        hd = an.head
        hparts = [c for c in hd.parts if not is_beak(c)] or hd.parts
        self.head_tris = np.concatenate([c.tris for c in hparts])
        hp = self.head_tris.reshape(-1, 3)
        self.hlo, self.hhi = hp.min(axis=0), hp.max(axis=0)
        self.hc = (self.hlo + self.hhi) / 2
        self.eye = np.array([abs(hd.eye_x), hd.eye_y, hd.eye_z])
        hips = np.array(animal.hips_file, dtype=np.float64)
        hip_mid = hips.mean(axis=0)
        hip_mid[0] = 0.0

        # ---- the trunk frame
        self.upright = (thi[1] - tlo[1]) > 1.3 * (thi[2] - tlo[2])
        if self.upright:
            fwd = P.unit(self.hc - hip_mid)
            dors = np.array([0.0, 0.0, 1.0])
            dors = P.unit(dors - (dors @ fwd) * fwd)
        else:
            fwd = np.array([0.0, 0.0, -1.0])
            dors = np.array([0.0, 1.0, 0.0])
        lat = P.unit(np.cross(dors, -fwd))
        self.M = np.stack([lat, dors, -fwd], axis=1)   # file = local @ M.T, local = file @ M (det +1)

        lt = ttris @ self.M
        lp = lt.reshape(-1, 3)
        self.Zf = float(lp[:, 2].min())
        self.Zr = float(lp[:, 2].max())
        self.L = self.Zr - self.Zf
        self.sl = analyze.Slices(lt, self.Zf + 0.003, self.Zr - 0.003, 40)
        self.W = float(np.max(self.sl.hw))
        H = float(np.max(self.sl.ymax) - np.min(self.sl.ymin))
        hl = hips @ self.M
        self.Zh = float(hl[:, 2].mean())
        self.Yh = float(hl[:, 1].mean())
        self.Hx = float(np.mean(np.abs(hl[:, 0])))
        self.rs = max(P.MIN_R + 0.002, min(0.14 * self.W, 0.1 * H))
        li = animal.leg_info
        self.legr = max(P.MIN_R, p["leg_r"] * (li.hip_r if li else 0.02))

        # ---- the living wing: the biggest side piece (as the right one, x > 0, in the trunk frame)
        self.wing = None
        side = [c for c in cand if c not in self.trunk and c.n >= 12 and abs(c.centre[0]) > 0.6 * self.W]
        if side:
            w = max(side, key=lambda c: (c.n, float(c.centre[0])))
            wt = w.tris @ self.M
            if w.centre[0] < 0:
                wt = wt * np.array([-1.0, 1.0, 1.0])
            self.wing = wt

        # ---- the living tail: the centred pieces behind the trunk (trunk frame box), for tail_aim
        self.tail_box = None
        back = []
        for c in cand:
            if c in self.trunk or c.n < 6 or abs(c.centre[0]) > 0.3 * self.W:
                continue
            q = (c.tris @ self.M).reshape(-1, 3)
            if float(q[:, 2].mean()) > self.Zr - 0.02:
                back.append(q)
        if back:
            q = np.concatenate(back)
            self.tail_box = (q.min(axis=0), q.max(axis=0))

    # ---------------- frame helpers
    def to_file(self, q):
        return np.asarray(q, dtype=np.float64) @ self.M.T

    def local(self, part):
        """A part built in the trunk frame, in file axes."""
        return P.transform(part, self.M)

    def prof(self, z):
        return self.sl.at(min(max(z, self.Zf + 0.01), self.Zr - 0.01))

    def spine_y(self, z):
        ymin, ymax, hw = self.prof(z)
        return ymax - max(1.3 * self.rs, 0.10 * (ymax - ymin))

    def sternum_y(self, z):
        ymin, ymax, hw = self.prof(z)
        return ymin + self.p["sternum"] * (ymax - ymin)

    def above_ground(self, q, r):
        """q (trunk frame) raised straight up in file axes until it is at least r above the ground (only
        matters for the upright Penguin, whose rear end is at its feet)."""
        q = np.asarray(q, dtype=np.float64)
        f = self.to_file(q)
        if f[1] < r + 0.004:
            f[1] = r + 0.004
            q = f @ self.M
        return q

    # ---------------- body
    def body(self):
        p = self.p
        L = self.L
        self.rib0 = self.Zf + 0.07 * L
        self.rib1 = min(self.Zf + p["rib_frac"] * L, self.Zh - 0.05 * L)
        self.Zp0 = self.Zh - p["pel_pre"] * L
        self.Zp1 = min(self.Zh + p["pel_post"] * L, self.Zr - 0.03 * L)
        trunk = [self.ribcage()]
        if p["keel"] not in ("loft", "flat"):
            trunk.append(self.keel() if p["keel"] else self.raft())
        if p["spine"]:
            trunk.append(self.spine())
        trunk.append(self.pelvis())
        trunk += self.tail()
        if p["shoulders"]:
            trunk += self.shoulders()
        trunk += self.wings()
        parts = [self.local(q) for q in trunk if q is not None and len(q)]
        parts += self.skull()
        parts.append(self.neck())
        return [q for q in parts if q is not None and len(q)]

    # ---------------- ribcage and sternum
    def ribcage(self):
        p = self.p
        n = int(p["ribs"])
        if self.small:
            n = min(n, 3)
            sides = 8
        else:
            sides = 10
        z0, z1 = self.rib0, self.rib1
        pitch = (z1 - z0) / (n + 0.6)
        rings = []
        rw = p["rib_w"]
        kl = p["keel"] == "loft"
        flat = p["keel"] == "flat"
        # keel in the loft: the ring's lower half reaches down to the keel's edge and narrows into it (taper
        # KT), its width scaled so the widest point stays the plain ribcage's
        KT = -0.6
        kw = 1.0 / 1.137

        def ring(z, s, bot_raise=0.0):
            ymin, ymax, hw = self.prof(z)
            top = self.spine_y(z) + 0.5 * self.rs
            bot = self.sternum_y(z)
            bot = bot + bot_raise * (top - bot)
            bot = min(bot, top - 0.04)
            hh = (top - bot) / 2.0
            if kl:
                cy = (top + bot) / 2.0
                return P.Ring((0.0, cy, z), kw * rw * hw * s, hh * s, max(hh, cy - self.keel_edge(z)), power=2.0,
                              taper=KT)
            if flat:
                # the raft sternum as the ribcage's own broad, flat floor
                return P.Ring((0.0, (top + bot) / 2.0, z), rw * hw * s, hh * s, hh, power=2.5, taper=0.24)
            return P.Ring((0.0, (top + bot) / 2.0, z), rw * hw * s, hh * s, hh, power=2.0, taper=0.18)

        small = self.small
        if not small:
            rings.append(ring(z0, 0.55, 0.2))
        z = z0 + 0.45 * pitch
        g = p["groove"]
        for i in range(n):
            f = i / max(1, n - 1)
            raise_ = 0.2 * max(0.0, f - 0.5) * 2
            s_main = 0.82 + 0.18 * min(1.0, (i + 1) / 2.0) - 0.08 * max(0.0, f - 0.6) / 0.4
            rings.append(ring(z, s_main * 1.04, raise_))
            rings.append(ring(z + g * pitch, s_main * 0.95, raise_))
            z += pitch
        rings.append(ring(z1, 0.62, 0.3))
        self.rib_rings = rings
        band = [] if small else [BONE]
        for i in range(n):
            band.append(SHADE)
            band.append(BONE)
        band = band[: len(rings) - 1]
        fr = frames_const(len(rings), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0))
        ext0 = (0.45 if small else 0.3) * pitch
        keep = {0, sides // 2}
        if kl or flat:
            # the keel's flanks or the raft's floor stay bone (the ribs end at the sternum)
            keep |= {sides // 2 - 1, sides // 2 + 1}
        return P.loft(rings, sides, frames=fr, caps=(ext0, 0.3 * pitch), band_mat=band, keep=keep,
                      name="ribcage")

    def keel_edge(self, z):
        """Trunk-frame y of the keel's lower edge at z, for a keel built into the ribcage's loft: deepest a
        quarter of the way back along the ribs, shallow at both ends (as keel())."""
        t = (z - self.rib0) / max(1e-6, self.rib1 - self.rib0)
        f = float(np.interp(t, [0.0, 0.22, 0.5, 0.8, 1.0], [0.40, 1.0, 0.80, 0.40, 0.10]))
        ymin, ymax, hw = self.prof(z)
        st = self.sternum_y(z)
        deep = ymin + self.p["keel_depth"] * (ymax - ymin)
        return st - f * (st - deep)

    def rib_at(self, z):
        """(centre y, half-width, top y, bottom y) of the ribcage loft at z (interpolated, clamped)."""
        rs = self.rib_rings
        zs = np.array([r.c[2] for r in rs])
        cy = float(np.interp(z, zs, [r.c[1] for r in rs]))
        hw = float(np.interp(z, zs, [r.hw for r in rs]))
        top = float(np.interp(z, zs, [r.c[1] + r.top for r in rs]))
        bot = float(np.interp(z, zs, [r.c[1] - r.bot for r in rs]))
        return cy, hw, top, bot

    def keel(self):
        """The carina: a blade under the sternum, deepest a quarter of the way back, its top inside the
        ribcage's floor; behind the ribs it runs on as the sternum's rear."""
        z0 = self.rib0 + 0.03 * (self.rib1 - self.rib0)
        z1 = min(self.rib1 + 0.12 * self.L, self.Zp0 + 0.35 * (self.Zh - self.Zp0))
        hw = max(0.013, 0.09 * self.W)
        prof = [(0.0, 0.40), (0.22, 1.0), (0.5, 0.80), (0.8, 0.40), (1.0, 0.10)]
        if self.small:
            prof = [(0.0, 0.45), (0.25, 1.0), (0.65, 0.6), (1.0, 0.1)]
        rings = []
        for t, f in prof:
            z = z0 + t * (z1 - z0)
            ymin, ymax, hw_t = self.prof(z)
            st = self.sternum_y(z)
            deep = ymin + self.p["keel_depth"] * (ymax - ymin)
            bottom = st - f * (st - deep)
            top = st + 0.35 * (self.rib_at(min(z, self.rib1))[2] - st) if z <= self.rib1 else st + 0.02
            bottom = min(bottom, top - 0.03)
            h = (top - bottom) / 2
            rings.append(P.Ring((0.0, (top + bottom) / 2, z), hw * (1.0 if t < 0.95 else 0.8), h, h))
        fr = frames_const(len(rings), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0))
        return P.loft(rings, 4, frames=fr, caps=(0.015, 0.02), angle0=math.pi / 2, name="keel")

    def raft(self):
        """The ratites' flat sternum (no keel): a broad shield under the front of the ribcage."""
        zc = self.rib0 + 0.42 * (self.rib1 - self.rib0)
        cy, hw, top, bot = self.rib_at(zc)
        th = max(0.026, 0.12 * self.W)
        return P.plate((0.0, bot + 0.2 * th, zc), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), 0.80 * hw,
                       0.45 * (self.rib1 - self.rib0), th, sides=8, power=2.2, name="sternum")

    # ---------------- spine
    def spine(self):
        z0 = self.rib0 - 0.02 * self.L
        z1 = self.Zp0 + 0.05 * self.L
        rs = self.rs
        zm = (z0 + z1) / 2
        pts = [(0.0, self.spine_y(z0), z0), (0.0, self.spine_y(zm), zm), (0.0, self.spine_y(z1), z1)]
        rad = [0.9 * rs, rs, rs]
        if self.small:
            pts, rad = [pts[0], pts[2]], [0.9 * rs, rs]
        return P.loft_path(pts, rad, rad, sides=6, caps=(0.6 * rs, 0.6 * rs), name="spine")

    # ---------------- pelvis, tail
    def pelvis(self):
        """The fused synsacrum and ilia as one broad block over the hips, deepest at the hip sockets (down to
        hip height, so the legs' tops sink into it), with bone_shade openings on its sides behind the sockets;
        thin pubis rods along its lower edge, trailing back."""
        zp0, zp1, zh = self.Zp0, self.Zp1, self.Zh
        legr = self.legr
        rs = self.rs
        span = zp1 - zp0
        zd = min(max(zh, zp0 + 0.35 * span), zp1 - 0.12 * span)   # the deepest ring
        hw_h = max(self.Hx + 0.35 * legr, 0.62 * self.W)
        depth = max(self.spine_y(zd) - self.Yh, 0.04)
        th = max(0.022, 1.2 * rs)
        prof = [(zp0, 0.45, 0.12), (zp0 + 0.55 * (zd - zp0), 0.82, 0.45), (zd, 1.0, 1.0),
                (zd + 0.55 * (zp1 - zd), 0.85, 0.55), (zp1, 0.42, 0.20)]
        if self.small:
            prof = [prof[0], prof[2], prof[4]]
        rings = []
        for z, fw, fd in prof:
            top = self.spine_y(z) + 0.3 * rs + 0.4 * th
            bottom = self.spine_y(z) - fd * (self.spine_y(z) - (self.Yh + 0.25 * legr))
            bottom = min(bottom, top - 0.03)
            h = (top - bottom) / 2
            rings.append(P.Ring((0.0, (top + bottom) / 2, z), hw_h * fw, h, h, power=2.6))
        n = len(rings)
        fr = frames_const(n, (0.0, 0.0, 1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0))
        sides = 8 if self.small else 10
        self.pel_rings = list(rings)
        band, cap1 = BONE, 0.5 * th
        if self.p["tail_aim"] and self.tail_box is not None:
            # the tail continues the pelvis' loft (a separate tail's root shows the pelvis' outline round it)
            trings, tframes, tband, cap1 = self.tail_aimed(zp1)
            rings = rings + trings
            fr = tuple(np.concatenate([fr[k], np.array([f[k] for f in tframes])]) for k in range(3))
            band = [BONE] * (n - 1) + tband
        block = P.loft(rings, sides, frames=fr, caps=(0.35 * th, cap1), band_mat=band, name="pelvis")
        # bone_shade: the groove between the ilia along the top in front of the sockets, the openings
        # (ilioischiadic foramina) on the sides behind them
        side = (2, 6) if self.small else (2, 3, 7, 8)
        front = range(0, (n - 1) // 2)
        for b in front:
            paint(block, sides, b, (0,), SHADE)
        for b in range((n - 1) // 2, (n - 1) // 2 + (1 if self.small else 2)):
            paint(block, sides, b, side, SHADE)
        parts = [block]
        if not self.small and self.p["pubis"]:
            for sgn in (1.0, -1.0):
                a = np.array([sgn * 0.75 * hw_h, self.Yh + 0.4 * depth, zd + 0.05 * span])
                b = np.array([sgn * 0.45 * hw_h, self.Yh + 0.2 * depth, zp1 + 0.05 * self.L])
                b = self.above_ground(b, P.MIN_R)
                parts.append(rod(a, b, P.MIN_R, sides=4, name="pubis"))
        return P.merge(parts, "pelvis")

    def tail(self):
        """Free tail vertebrae from the end of the synsacrum, then the pygostyle tipped up."""
        p = self.p
        if p["tail_aim"] and self.tail_box is not None:
            return []   # part of the pelvis' loft
        L = self.L
        z0 = self.Zp1 - 0.02 * L
        start = np.array([0.0, self.spine_y(min(z0, self.Zr - 0.01)) + 0.2 * self.rs, z0])
        dy, dz = p["tail_dir"]
        d = P.unit(np.array([0.0, dy, dz]))
        end = start + d * p["tail_len"] * L
        end = self.above_ground(end, 0.03)
        r0 = max(P.MIN_R, 0.75 * self.rs)
        parts = []
        n = int(p["tail"])
        if n > 0:
            parts.append(P.vertebra_chain([start, end], n, r0, max(P.MIN_R, 0.8 * r0), gap=0.006, sides=6,
                                          name="tail"))
        # pygostyle: a small blade, tipped up and back
        g = p["pygo"]
        e = P.unit(d + np.array([0.0, 0.6, 0.0]))
        ln = max(0.035, 0.08 * L * g)
        a = end - e * 0.2 * ln
        b = end + e * ln
        hh = max(0.018, 0.045 * L * g)
        rings = [P.Ring(a, max(0.012, 0.6 * r0), 0.4 * hh), P.Ring(b, 0.012, 0.6 * hh)]
        v = P.unit(np.cross(e, (1.0, 0.0, 0.0)))
        if v[2] > 0:
            v = -v
        fr = frames_const(2, e, (1.0, 0.0, 0.0), v)
        parts.append(P.loft(rings, 4, frames=fr, caps=(0.0, 0.3 * hh), angle0=math.pi / 2, name="pygostyle"))
        return parts

    def tail_aimed(self, z_after):
        """The free tail vertebrae and the pygostyle as rings continuing the pelvis' loft behind z_after, from
        the end of the synsacrum into the living tail (tail_aim: how far along the living tail and how high
        up in it the tip ends), so the bones end where the living bird does: a short round stem, a bone_shade
        joint, then the pygostyle widening into an upright blade and narrowing to its tip. Returns the rings,
        their (T, U, V) frames, the band materials leading into each, and the end cap's length."""
        p = self.p
        reach, up = p["tail_aim"]
        lo, hi = self.tail_box
        z0 = self.Zp1 - 0.02 * self.L
        start = np.array([0.0, self.spine_y(min(z0, self.Zr - 0.01)) + 0.2 * self.rs, z0])
        end = np.array([0.0, lo[1] + up * (hi[1] - lo[1]), lo[2] + reach * (hi[2] - lo[2])])
        end = self.above_ground(end, 0.03)
        d = end - start
        Lt = float(np.linalg.norm(d))
        rs = self.rs
        r0 = max(P.MIN_R + 0.006, 1.1 * rs)
        blade = p["pygo"] * max(1.5 * rs, 0.16 * Lt)
        T = P.unit(d)
        U = np.array([1.0, 0.0, 0.0])
        V = P.unit(np.cross(T, U))
        if V[1] < 0:
            V = -V
        spec = [(0.26, r0, r0, BONE), (0.34, 0.92 * r0, 0.95 * r0, SHADE),
                (0.70, max(P.MIN_R + 0.004, 0.8 * r0), blade, BONE), (1.0, P.MIN_R + 0.003, 0.55 * blade, BONE)]
        rings, frames, band = [], [], []
        for t, hw, ht, m in spec:
            c = start + t * d
            if c[2] <= z_after + 0.01:
                continue
            rings.append(P.Ring(c, hw, ht, ht, power=2.0))
            frames.append((T, U, V))
            band.append(m)
        return rings, frames, band, 0.5 * rings[-1].top

    # ---------------- shoulder girdle and wings
    def shoulder_joint(self):
        """Trunk-frame position of the right shoulder joint (front top side of the ribcage)."""
        z = self.rib0 + 0.12 * (self.rib1 - self.rib0)
        cy, hw, top, bot = self.rib_at(z)
        return np.array([0.92 * hw, top - 0.30 * (top - bot), z])

    def shoulders(self):
        """Furcula (wishbone) in front of the ribs, coracoids from the shoulders down to the front of the
        sternum, scapulae back along the top of the ribs. Thin 4-sided rods."""
        parts = []
        sj = self.shoulder_joint()
        zr = self.rib1 - self.rib0
        r = P.MIN_R
        cy, hw, top, bot = self.rib_at(self.rib0 + 0.02 * zr)
        apex = np.array([0.0, bot + 0.25 * (top - bot), self.rib0 - 0.03 * self.L])
        zc = self.rib0 + 0.18 * zr
        cyc, hwc, topc, botc = self.rib_at(zc)
        ze = sj[2] + 0.55 * zr
        cys, hws, tops, bots = self.rib_at(ze)
        for sgn in (1.0, -1.0):
            m = np.array([sgn, 1.0, 1.0])
            if self.p["furcula"]:
                parts.append(rod(sj * m, apex, r, name="furcula"))
            parts.append(rod(sj * m, np.array([0.55 * hwc, botc + 0.1 * (topc - botc), zc]) * m, 1.2 * r, 1.3 * r,
                             name="coracoid"))
            parts.append(rod(sj * m + np.array([0.0, 0.3 * r, 0.0]),
                             np.array([0.62 * hws, tops - 0.12 * (tops - bots), ze]) * m, 1.15 * r, r,
                             caps=(0.3 * r, 0.8 * r), name="scapula_bird"))
        return parts

    def wings(self):
        style = self.p["wings"]
        if style in ("plate", "stub_plate"):
            return self.wing_plates(style == "stub_plate" or self.wing is None)
        if style == "flipper" and self.wing is not None:
            return self.flippers()
        if style == "folded" and self.wing is not None:
            return self.folded()
        if style in ("stub", "folded"):
            return self.stubs()
        return []

    def folded(self):
        """Humerus back from the shoulder, forearm forward to the wrist (the bend of the wing), hand back
        again: nearly parallel, in the upper part of the living folded wing, outside the ribs."""
        wt = self.wing
        wp = wt.reshape(-1, 3)
        lo, hi = wp.min(axis=0), wp.max(axis=0)
        Lw = hi[2] - lo[2]
        Hw = hi[1] - lo[1]
        rh = max(P.MIN_R, 0.085 * Hw)
        rf = max(P.MIN_R, 0.8 * rh)
        rhand = max(P.MIN_R, 0.75 * rh)
        rib_hw = max(r.hw for r in self.rib_rings)
        xw = min(max(SHRINK * float((lo[0] + hi[0]) / 2), rib_hw + rh + 0.002), float(hi[0]) - rh)
        sj = self.shoulder_joint()
        sh = np.array([min(sj[0] + rh, xw), lo[1] + 0.80 * Hw, max(lo[2] + 0.03 * Lw, sj[2])])
        el = np.array([xw, lo[1] + 0.68 * Hw, lo[2] + 0.50 * Lw])
        wr = np.array([xw, lo[1] + 0.52 * Hw, lo[2] + 0.07 * Lw])
        tip = np.array([xw, lo[1] + 0.42 * Hw, lo[2] + 0.88 * Lw])
        sides = 4
        parts = []
        for sgn in (1.0, -1.0):
            m = np.array([sgn, 1.0, 1.0])
            parts.append(rod(sh * m, el * m, rh, 0.9 * rh, sides=sides, caps=(0.6 * rh, 0.8 * rh), name="humerus"))
            parts.append(rod(el * m, wr * m, rf, rf, sides=sides, caps=(0.6 * rf, 0.6 * rf), name="forearm"))
            parts.append(rod(wr * m, tip * m, rhand, P.MIN_R, sides=4, caps=(0.6 * rhand, 0.9 * P.MIN_R),
                             name="hand"))
        return parts

    def side_surface(self, y, z):
        """(x, outward normal (x, y, 0)) of the trunk's right side at height y and z: the outermost of the
        ribcage's and the pelvis' lofts (rings interpolated at z), or None where neither reaches."""
        best = None
        for rings in (self.rib_rings, getattr(self, "pel_rings", [])):
            zs = [r.c[2] for r in rings]
            if not rings or z < zs[0] or z > zs[-1]:
                continue

            def at(yy):
                cy = float(np.interp(z, zs, [r.c[1] for r in rings]))
                hw = float(np.interp(z, zs, [r.hw for r in rings]))
                top = float(np.interp(z, zs, [r.top for r in rings]))
                bot = float(np.interp(z, zs, [r.bot for r in rings]))
                pw, tp = rings[0].power, rings[0].taper
                t = (yy - cy) / (top if yy >= cy else bot)
                if abs(t) >= 0.98:
                    return None
                return hw * (1.0 - abs(t) ** pw) ** (1.0 / pw) * (1.0 - tp * t)

            x = at(y)
            if x is None:
                continue
            e = 0.003
            xa, xb = at(y + e), at(y - e)
            slope = 0.0 if xa is None or xb is None else (xa - xb) / (2 * e)
            n = P.unit(np.array([1.0, -slope, 0.0]))
            if best is None or x > best[0]:
                best = (x, n)
        return best

    def wing_plates(self, stub):
        """Each folded wing as one flat plate (0.03 thick) lying on the trunk's upper side, its outer face a
        little proud of it: from the wrist at the front, widest over the humerus and forearm, tapering back to
        the tip. One piece instead of three thin rods per side, so it reads as pale bone rather than as
        outline; sunk into the trunk, most of its own outline hides behind the ribs. stub: a short vestigial
        plate on the front of the ribcage's side (Emu, Cassowary), else the living folded wing's extent."""
        th = 0.015
        proud = 0.008
        if not stub:
            wp = self.wing.reshape(-1, 3)
            lo, hi = wp.min(axis=0), wp.max(axis=0)
            Lw, Hw = float(hi[2] - lo[2]), float(hi[1] - lo[1])
            # (t from the wrist at the front to the tip, centre and half height as shares of the wing's height)
            prof = [(0.06, 0.60, 0.10), (0.30, 0.64, 0.22), (0.58, 0.60, 0.18), (0.88, 0.48, 0.06)]
            samples = [(float(lo[2]) + t * Lw, float(lo[1]) + c * Hw, max(P.MIN_R + 0.002, h * Hw))
                       for t, c, h in prof]
        else:
            zr = self.rib1 - self.rib0
            samples = []
            for t, c, h in [(0.10, 0.30, 0.10), (0.28, 0.22, 0.15), (0.62, 0.02, 0.05)]:
                z = self.rib0 + t * zr
                cy, hw, top, bot = self.rib_at(z)
                samples.append((z, cy + c * (top - bot), max(P.MIN_R + 0.002, h * (top - bot))))
        rings, fr = [], []
        T = np.array([0.0, 0.0, 1.0])
        last = None
        for z, y, h in samples:
            s = self.side_surface(y, z)
            if s is None:
                if last is None:
                    continue
                s = last
            last = s
            x, n = s
            c = np.array([x, y, z]) + n * (proud - th)
            rings.append(P.Ring(c, h, th, th, power=2.6))
            fr.append((T, np.array([-n[1], n[0], 0.0]), n))
        if len(rings) < 2:
            return []
        frames = tuple(np.array([f[i] for f in fr]) for i in range(3))
        plate = P.loft(rings, 8, frames=frames, caps=(0.8 * rings[0].hw, 0.7 * rings[-1].hw), name="wing")
        return [plate, P.mirror_x(plate)]

    def stubs(self):
        """Vestigial wings (Emu, Cassowary): a short humerus and forearm hanging along the ribcage's side."""
        sj = self.shoulder_joint()
        zr = self.rib1 - self.rib0
        r = P.MIN_R + 0.002
        cy, hw, top, bot = self.rib_at(sj[2] + 0.2 * zr)
        x = hw + r + 0.004
        sh = np.array([max(sj[0], x - r), sj[1], sj[2]])
        el = np.array([x, sj[1] - 0.22 * (top - bot), sj[2] + 0.22 * zr])
        tip = np.array([x, el[1] - 0.12 * (top - bot), el[2] + 0.2 * zr])
        parts = []
        for sgn in (1.0, -1.0):
            m = np.array([sgn, 1.0, 1.0])
            parts.append(rod(sh * m, el * m, 1.15 * r, r, name="humerus"))
            parts.append(rod(el * m, tip * m, r, 0.8 * r, caps=(0.4 * r, 0.8 * r), name="forearm"))
        return parts

    def flippers(self):
        """Penguin flippers: a flat humerus, the radius and ulna side by side, and a tapering hand, along the
        living flipper's centreline (sliced across the trunk axis), flat in the flipper's plane, never inside
        the ribcage."""
        wt = self.wing
        wp = wt.reshape(-1, 3)
        z0, z1 = float(wp[:, 2].min()), float(wp[:, 2].max())
        zs, cx, cy, wy = [], [], [], []
        for z in np.linspace(z0 + 0.004, z1 - 0.004, 12):
            b = analyze.slice_bounds(wt, z)
            if b is None:
                continue
            zs.append(z)
            cy.append((b[0] + b[1]) / 2)
            wy.append((b[1] - b[0]) / 2)
            cx.append((b[2] + b[3]) / 2)
        zs, cx, cy, wy = map(np.array, (zs, cx, cy, wy))
        th = P.MIN_R + 0.001

        def at(t):
            z = z0 + t * (z1 - z0)
            x = float(np.interp(z, zs, cx))
            if self.rib0 <= z <= self.rib1:
                x = max(x, self.rib_at(z)[1] + th + 0.004)
            return np.array([x, float(np.interp(z, zs, cy)), z]), float(np.interp(z, zs, wy))

        sj = self.shoulder_joint()
        segs = [(0.02, 0.36, 0.55, 0.62, 0.0, "humerus"), (0.40, 0.62, 0.26, 0.26, -0.45, "radius"),
                (0.40, 0.62, 0.30, 0.30, 0.45, "ulna"), (0.65, 0.97, 0.55, 0.15, 0.0, "hand")]
        parts = []
        for t0, t1, f0, f1, off, nm in segs:
            a, wa = at(t0)
            b, wb = at(t1)
            if nm == "humerus":
                a = np.array([max(sj[0], a[0] - 0.02), a[1], a[2]])
            a = a + np.array([0.0, off * wa, 0.0])
            b = b + np.array([0.0, off * wb, 0.0])
            T = P.unit(b - a)
            Vv = P.unit(np.cross(T, (1.0, 0.0, 0.0)))
            Uu = P.unit(np.cross(Vv, T))
            rings = [P.Ring(a, th, max(P.MIN_R, f0 * wa)), P.Ring(b, th, max(P.MIN_R, f1 * wb))]
            part = P.loft(rings, 4, frames=frames_const(2, T, Uu, Vv), caps=(0.3 * th, 0.6 * th), name=nm)
            parts += [part, P.mirror_x(part)]
        return parts

    # ---------------- skull and neck (file axes)
    def beak_base(self):
        """Centre and half sizes of the living beak's base (its rear end), or None."""
        if not self.beak:
            return None
        bp = np.unique(np.round(np.concatenate([c.tris for c in self.beak]).reshape(-1, 3), 4), axis=0)
        zmax, zmin = bp[:, 2].max(), bp[:, 2].min()
        base = bp[bp[:, 2] > zmax - 0.35 * (zmax - zmin)]
        return (base.min(axis=0) + base.max(axis=0)) / 2, (base.max(axis=0) - base.min(axis=0)) / 2

    def skull(self):
        """The living head scaled to 0.85 about its centre, lofted through its cross-sections from just in
        front of its back end to the beak's base; rounder at the back, meeting the beak at the front. Two of
        its rings bracket the living eye, so a small skull's socket can be painted on the band between them."""
        p = self.p
        hc = self.hc
        hz0, hz1 = float(self.hlo[2]), float(self.hhi[2])
        hlen = hz1 - hz0
        bb = self.beak_base()
        if bb is not None:
            bb_c, bb_h = bb
            zf_l = float(bb_c[2]) + 0.004
        else:
            bb_c = np.array([0.0, hc[1], hz0])
            bb_h = np.array([0.2 * (self.hhi[0] - self.hlo[0]), 0.2 * (self.hhi[1] - self.hlo[1]), 0.0])
            zf_l = hz0 + 0.1 * hlen
        ez = float(self.eye[2])
        zf_l = min(zf_l, ez - 0.01)
        zb_l = hz1 - 0.12 * hlen
        # the socket's half-length along the head (living units), the rings in front of and behind the eye
        d = p["orbit"] * 0.8 * (self.hhi[1] - self.hlo[1]) / 2
        ze1 = min(ez + 1.3 * d, zb_l - 0.3 * (zb_l - ez))
        ze0 = max(ez - d, zf_l + 0.004)
        zl_list = [zb_l, ze1, ze0, zf_l] if self.small else [zb_l, 0.5 * (zb_l + ze1), ze1, ze0, zf_l]
        self.eye_band = 1 if self.small else 2
        fprof = [1.0] * len(zl_list)
        fprof[0] = 0.85
        fprof[-1] = 0.9
        rings = []
        for i, zl in enumerate(zl_list):
            ymin, ymax, hw = self.head_slice(zl)
            c = hc[1] + SHRINK * ((ymin + ymax) / 2 - hc[1])
            h = SHRINK * (ymax - ymin) / 2 * fprof[i]
            w = SHRINK * hw * fprof[i]
            z = hc[2] + SHRINK * (zl - hc[2])
            if i == len(zl_list) - 1:
                # meet the beak's base: centred on it, at least a little bigger
                c = float(bb_c[1])
                h = max(h, 1.2 * bb_h[1])
                w = max(w, 1.2 * bb_h[0])
                z = zf_l
            rings.append(P.Ring((0.0, c, z), w, h, h, power=2.3))
        n = len(rings)
        self.skull_rings = rings
        sides = 8 if self.small else 10
        fr = frames_const(n, (0.0, 0.0, -1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0))
        cap_b = 0.25 * SHRINK * hlen
        cap_f = 0.6 * min(rings[-1].hw, rings[-1].top)
        skull = P.loft(rings, sides, frames=fr, caps=(cap_b, cap_f), name="skull")
        parts = [skull]
        parts += self.sockets(skull, sides)
        parts += [P.Part(q.verts, q.tris, q.mat, None, "beak") for q in copy_comps(self.mesh, self.beak)]
        if self.casque:
            # the casque, with its horn colour, its base sunk 0.012 into the skull's top
            cq = copy_comps(self.mesh, self.casque, keep_colour=True)
            v = np.concatenate([q.verts for q in cq])
            low = v[int(np.argmin(v[:, 1]))]
            cy, hw, ht = self.skull_at(low[2])
            d = (cy + ht - 0.012) - low[1]
            parts += [P.Part(q.verts + np.array([0.0, d, 0.0]), q.tris, q.mat, q.col, "casque") for q in cq]
        if p["jaw"] and bb is not None:
            # lower jaw: two thin rami from under the beak's base back to the jaw joints under the orbits
            r = P.MIN_R
            front = np.array([0.0, bb_c[1] - 0.7 * bb_h[1], bb_c[2] - 0.004])
            zj = self.eye[2] + 0.30 * (rings[0].c[2] - self.eye[2])
            cyj, hwj, htj = self.skull_at(zj)
            for sgn in (1.0, -1.0):
                back = np.array([sgn * 0.7 * hwj, cyj - 0.85 * htj, zj])
                parts.append(rod(front + np.array([sgn * 0.9 * r, 0.0, 0.0]), back, r, name="jaw"))
        return parts

    def head_slice(self, z):
        """(ymin, ymax, half-width) of the living head's cross-section at file z (clamped into the head)."""
        z = min(max(z, float(self.hlo[2]) + 0.002), float(self.hhi[2]) - 0.002)
        b = analyze.slice_bounds(self.head_tris, z)
        return b[0], b[1], max(abs(b[2]), abs(b[3]))

    def skull_at(self, z):
        """(centre y, half-width, half-height) of the skull loft at z (interpolated, clamped)."""
        rs = self.skull_rings
        zs = np.array([r.c[2] for r in rs])
        order = np.argsort(zs)
        zs = zs[order]
        cy = float(np.interp(z, zs, [rs[i].c[1] for i in order]))
        hw = float(np.interp(z, zs, [rs[i].hw for i in order]))
        ht = float(np.interp(z, zs, [rs[i].top for i in order]))
        return cy, hw, ht

    def sockets(self, skull, sides):
        """Large an_dark orbits at the living eyes, facing out to the sides. On a big skull, lenses standing a
        little proud like the people's; on a small one (half-width under 0.05) the skull's own faces round
        the eye are painted an_dark instead: a separate socket gets its own outline hull, which round a
        socket on a skull only 0.06 across paints the whole head and a rim above it black."""
        cy, hw, ht = self.skull_at(float(self.hc[2]) + SHRINK * (float(self.eye[2]) - float(self.hc[2])))
        if hw < 0.05:
            # the band between the two rings that bracket the eye; the two sectors nearest the eye's direction
            r = self.skull_rings[self.eye_band]
            e = self.hc + SHRINK * (self.eye - self.hc)
            th = math.degrees(math.atan2((e[1] - r.c[1]) / max(r.top, 1e-3), abs(e[0]) / max(r.hw, 1e-3)))
            a0 = 90.0 - 180.0 / sides
            step = 360.0 / sides
            k = int(math.floor(((th - a0) % 360.0) / step))
            centre = a0 + step * (k + 0.5)
            k2 = k + 1 if th > centre else k - 1
            right = [k % sides, k2 % sides]
            # the loft's U axis is file +x: a sector at angle a mirrors to the one at 180 - a
            left = [int(math.floor(((180.0 - (a0 + step * (j + 0.5)) - a0) % 360.0) / step)) for j in right]
            paint(skull, sides, self.eye_band, right + left, DARK)
            return []
        q, n = surface_point(skull, self.eye)
        cy, hw, ht = self.skull_at(q[2])
        r = self.p["orbit"] * min(hw, ht)
        r = max(P.MIN_R, min(r, 0.8 * ht, 0.9 * hw))
        nrm = P.unit(n * 0.8 + np.array([0.3, 0.0, -0.1]))
        out = []
        for sgn in (-1.0, 1.0):
            s_ = q * [sgn, 1, 1]
            nn = nrm * [sgn, 1, 1]
            if self.p["socket"] == "flush":
                # sunk: the full-width ring just under the skull's surface, so only the lens' outer cone shows,
                # as a dark patch with a pale rim of skull round it and no outline ring of its own
                hl = 0.012
                c = s_ - nn * (0.35 * hl + 0.003)
            else:
                hl = max(0.012, 0.45 * r)
                c = s_ - nn * (0.35 * hl) + nn * 0.003
            out.append(P.lens(c, nn, r, hl, sides=6, name="socket"))
        return out

    def neck(self):
        """Vertebrae on an S curve: from the back of the skull down the living neck (bowed a little back) to a
        low bend just in front of the ribcage, then back up into the spine at the shoulders."""
        p = self.p
        rb = self.skull_rings[0]
        O = np.array([0.0, rb.c[1] - 0.45 * rb.top, rb.c[2] - 0.1 * rb.top])
        L = self.L
        dz, hb = p["neck_low"]
        zB = self.rib0 - dz * L
        B = self.to_file((0.0, lerp(self.sternum_y(zB), self.spine_y(zB), hb), zB))
        zS = self.rib0 + 0.05 * L
        S = self.to_file((0.0, self.spine_y(zS) + 0.2 * self.rs, zS))
        B[0] = S[0] = 0.0
        rn = max(P.MIN_R, p["neck_r"] * (self.hhi[0] - self.hlo[0]) / 2)
        if np.linalg.norm(O - B) > 4 * rn and np.linalg.norm(B - S) > 2 * rn:
            back = np.array([0.0, 0.0, 1.0]) * p["neck_bow"] * float(np.linalg.norm(O - B))
            M = (O + B) / 2 + back
            pts = catmull_rom([O, M, B, S], 8)
        else:
            pts = [O, S]
        Ln = P.path_length(pts)
        gap = 0.006 if self.small else 0.008
        n = max(2, min(int(p["neck"]), int(Ln / (1.9 * rn + gap))))
        if p["neck_loft"]:
            return self.neck_loft(pts, n, rn)
        return P.vertebra_chain(pts, n, 0.9 * rn, 1.1 * rn, gap=gap, sides=int(p["neck_sides"]), name="neck")

    def neck_loft(self, pts, n, rn):
        """The neck as one loft: n straight vertebrae along the curve joined by short bone_shade bands, so
        the joints show as grooves without a gap (each gap between separate vertebrae is an outline)."""
        P0 = P.resample(pts, 64)
        seg = np.linalg.norm(np.diff(P0, axis=0), axis=1)
        cum = np.concatenate([[0.0], np.cumsum(seg)])
        Ln = float(cum[-1])

        def at(s):
            return np.array([float(np.interp(s, cum, P0[:, k])) for k in range(3)])

        step = Ln / n
        j = min(0.16 * step, 0.5 * rn)
        rings, rad, band = [], [], []
        for i in range(n):
            r = 0.9 * rn + 0.2 * rn * (i + 0.5) / n
            s0 = i * step + (j / 2 if i > 0 else 0.0)
            s1 = (i + 1) * step - (j / 2 if i < n - 1 else 0.0)
            rings += [at(s0), at(s1)]
            rad += [r, r]
            band += [BONE, SHADE]
        band = band[:-1]
        return P.loft_path(rings, rad, rad, sides=int(self.p["neck_sides"]), caps=(0.5 * rad[0], 0.5 * rad[-1]),
                           band_mat=band, name="neck")

    # ---------------- leg (leg file: hip at y = legLength, centred on x = z = 0)
    def leg(self):
        p = self.p
        li = self.a.leg_info
        LL = self.LL
        r = self.legr
        k = max(P.MIN_R * 1.3, 1.4 * r)
        toe_z = li.toe_z if li else -0.3 * LL
        heel_z = li.heel_z if li else 0.1 * LL
        # where the toes meet, under the end of the tarsometatarsus
        zb = heel_z - 0.35 * (heel_z - toe_z)
        foot_y = 1.4 * r
        sides = 5 if self.small else 6
        if p["family"] == "penguin":
            pts = [(0.0, LL - 0.7 * k, 0.0), (0.0, 0.55 * LL, 0.3 * zb), (0.0, foot_y, zb)]
            rad = [k, 0.95 * r, 1.15 * r]
        else:
            kn = np.array([0.0, LL * p["knee"][0], LL * p["knee"][1]])
            an = np.array([0.0, LL * p["ankle"][0], LL * p["ankle"][1]])
            ft = np.array([0.0, foot_y, zb])
            hip = np.array([0.0, LL - 0.75 * k, 0.0])
            if p["family"] == "fowl":
                # the femur stays inside the body: the hip knob straight down and back into the drumstick
                # (tibiotarsus), one bend fewer for the outline to fill
                pts = [hip, an, ft]
                rad = [k, 1.25 * r, 1.1 * r]
            elif self.small:
                pts = [hip, kn, an, ft]
                rad = [k, 1.2 * r, 1.25 * r, 1.1 * r]
            else:
                pts = [hip, kn, lerp(kn, an, 0.5), an, ft]
                rad = [k, 1.25 * r, 0.9 * r, 1.3 * r, 1.15 * r]
        pts = np.array(pts, dtype=np.float64)
        # the hip knob's top 0.015 above legLength (into the pelvis)
        for _ in range(3):
            shaft = P.loft_path(pts, rad, rad, sides=sides, caps=(0.6 * k, 0.35 * r), name="leg")
            top = float(shaft.verts[:, 1].max())
            if abs(top - (LL + 0.015)) < 0.002:
                break
            pts[0, 1] += LL + 0.015 - top
        return [shaft] + self.toes(foot_y, zb, toe_z, heel_z)

    def toes(self, foot_y, zb, toe_z, heel_z):
        """The foot as one closed star-shaped shell on the ground under the end of the tarsometatarsus: a
        pointed toe per entry of the plan's toe set, notches between the toes, a flat sole, the top rising
        to a hub inside the bone's end. (Separate pointed toes put outline spikes round the foot: the outline
        hull grows every pole along its axis, and a toe's base pole poked out the far side of the foot.)"""
        p = self.p
        li = self.a.leg_info
        mid_len = max(0.03, zb - toe_z)
        hind_len = max(0.02, heel_z - zb)
        rt = max(P.MIN_R, 0.55 * self.legr)
        foot_hw = li.foot_hw if li else 0.05
        th = p["toe_h"]
        toes = []
        for ang, f, hind in p["toes"]:
            ln = min(f * mid_len, hind_len + 0.005) if hind else f * mid_len
            toes.append((float(ang), ln))
        toes.sort(key=lambda t: t[0])
        fwd_min = min(ln for ang, ln in toes if abs(ang) < 90.0)
        rn = max(rt, 0.38 * fwd_min)
        ring = []      # (x, z, top y) round the outline, by angle
        n = len(toes)
        for i in range(n):
            a0, l0 = toes[i]
            a1, l1 = toes[(i + 1) % n]
            ra = math.radians(a0)
            x = max(-foot_hw - 0.01, min(foot_hw + 0.01, math.sin(ra) * l0))
            ring.append((x, -math.cos(ra) * l0, th[0] * rt))
            gap = (a1 - a0) % 360.0 or 360.0
            am = math.radians(a0 + gap / 2.0)
            r = rn if gap < 120.0 else max(0.6 * rn, 0.5 * rt)
            ring.append((math.sin(am) * r, -math.cos(am) * r, th[1] * rt))
        m = len(ring)
        verts = []
        for x, z, ty in ring:
            verts.append((x, 0.001, zb + z))
            verts.append((x, ty, zb + z))
        hub_top = len(verts)
        verts.append((0.0, max(foot_y, th[2] * rt), zb))
        tris = []
        for k in range(m):
            k1 = (k + 1) % m
            b0, t0, b1, t1 = 2 * k, 2 * k + 1, 2 * k1, 2 * k1 + 1
            tris += [(b0, b1, t1), (b0, t1, t0), (hub_top, t0, t1)]
        # the flat sole: the outline polygon cut into m - 2 triangles
        for a, b, c in ear_clip([(x, z) for x, z, ty in ring]):
            tris.append((2 * c, 2 * b, 2 * a))
        foot = P.Part(np.array(verts, dtype=np.float64), np.array(tris), BONE, None, "toes")
        return [P.orient(foot)]
