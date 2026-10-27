"""Legless plan: skeletons for the pinnipeds and the cobra (Multiplayer port, not shipped).

None of these defs has a leg model, so everything is in the body file (FILE axes, the animal faces -z). A dead
one lies on its side like the other animals; the runtime takes the rest width from these bones.

Pinnipeds (Seal, SeaLion, Walrus), built along the living body's centre line in the side (y-z) plane:
  centre line  the skeleton (thinning) of the body's mid-plane section (x = 0; head and flippers left out),
               the path through it from the rear end to the point nearest the back of the skull, which starts
               it, smoothed. The dorsal direction is the path's normal in that plane, so a raised chest and a
               neck going up to the head (the sea lion) keep their shape. Cross-sections of the living body
               square to the path give the back's and the belly's reach and the half-width at every station.
  skull        quadruped.Skeleton.skull() on the living head (eye sockets, jaw, nasal opening); the walrus's
               tusks copied from its tusk group and moved in with the skull (quadruped.Skeleton.extras())
  neck, tail   separate vertebrae along the centre line
  spine        a loft just under the back, with lumbar vertebra bumps behind the ribs
  ribcage      a loft over the front of the trunk following the centre line, its rings square to it (rib
               ridges and bone_shade grooves, spine and sternum sectors kept), as the quadrupeds' ribcage
  pelvis       a small pair of hip bones from the sacrum out to the hip sockets and back
  flippers     the living model's paired low side pieces (the Walrus's `flipper` group), long axis, width and
               plane by PCA. Front: a short arm from a shoulder knob on the ribcage's side to the wrist at the
               flipper's root, then a fan of finger bones across the flipper to its far edge (no scapula: on
               these round ribcages a blade only showed as a ring of outline where it crossed the rib ridges).
               Rear: a short leg from the hip socket to the hind flipper's root and a fan of toe bones.

Cobra, along the centre line of the living loft it is made of: the living cobra is one loft of 6-sided rings
from a pole at the snout tip to a flat cap at the tail tip, so the n-th layer of vertices away from the pole
(graph distance) is its n-th ring; ring centres joined by a Catmull-Rom spline, the dorsal side of each ring
away from its belly faces (the raised neck's back faces backward, not up):
  skull        a small snake skull (flat braincase, narrow snout) over the head's rings, an_dark eye sockets at
               the living eyes (kept small and low: their outline would cover so small a skull), a V-shaped
               lower jaw from behind the skull to the chin
  ribs         the trunk over the front 80% behind the skull as one ribbed loft (the pinnipeds' ribcage: per rib
               a ridge ring and a bone_shade groove, the top sector kept bone as the spine): the ribs reach 0.8
               of the living half-width and curve down round the body; in the hood (rings much wider than deep)
               the loft is a wide flat plate. At map zoom a cobra is a few pixels wide, so separate vertebrae
               and rib rods came out as a dark band of outlines with white dashes in it.
  tail         the tail's vertebrae as one tapering rod just under the trunk's top, pinched in bone_shade at
               each joint
  Both lofts follow a copy of the centre line relaxed where it bends tighter than their width (the hairpin
  where the raised neck meets the coil), their rings turning toward the dorsal side a little at a time.

Budgets (spec): Seal medium 700-1000, SeaLion/Walrus/Cobra large up to ~1400 triangles. Thin rods use flat
polygon caps (no pole) to save triangles: a 6-sided two-ring rod is 20 triangles.
Deterministic: no randomness (skeletonize, not skimage's medial_axis, which breaks ties at random).
"""

import math

import numpy as np

from .. import analyze
from .. import prims as P
from ..prims import BONE, SHADE
from . import quadruped as Q

SPECIES = {"Seal", "SeaLion", "Walrus", "Cobra"}

SHRINK = Q.SHRINK

# Per pinniped (on top of quadruped.BASE). rib0 / rib1 / sac: the ribcage's start (the neck's end), the
# ribcage's end and the sacrum, as shares of the centre line from the back of the skull (0) to the tail end
# (1). skull_size: the size class quadruped.Skeleton.skull() uses for its ring, side and socket counts (the
# budget). digits: finger bones per front / hind flipper. lumbar: vertebra bumps between ribcage and sacrum.
# head_box: analysis override of the head pieces (analyze.Analysis._find_head).
PINNIPED = {
    "Seal": dict(
        skull=[(0.0, 0.80, 0.85), (0.18, 1.0, 1.0), (0.45, 0.95, 0.92), (0.75, 0.70, 0.72), (1.0, 0.55, 0.58)],
        jaw=0.34, orbit=0.46, fwd=0.25, nose=0.30, skull_size="small", neck=2, ribs=5, rib_sides=8,
        rib0=0.13, rib1=0.58, sac=0.80, digits=(4, 3), spine_r=0.15, rib_depth=0.92, lumbar=1),
    "SeaLion": dict(
        skull=[(0.0, 0.80, 0.90), (0.2, 1.0, 1.0), (0.45, 0.95, 0.95), (0.72, 0.70, 0.74), (1.0, 0.55, 0.58)],
        jaw=0.36, orbit=0.44, fwd=0.3, nose=0.30, skull_size="medium", neck=4, ribs=6, rib_sides=10,
        rib0=0.17, rib1=0.60, sac=0.84, digits=(5, 4), spine_r=0.15, rib_depth=0.92, lumbar=2,
        # the dark dome on the head is fur, not skull: the skull is the head and muzzle pieces only
        head_box=((-0.25, 0.6, -0.6), (0.25, 0.87, -0.15))),
    "Walrus": dict(
        # a walrus's face is short and deep (the tusk roots), not a snout: tall to the front, and the skull
        # ends where the muzzle is still tall (head_box), so its front isn't the muzzle's rounded tip
        skull=[(0.0, 0.80, 0.85), (0.18, 1.0, 1.0), (0.45, 1.0, 1.02), (0.75, 1.0, 1.12), (1.0, 0.95, 1.15)],
        jaw=0.18, orbit=0.30, fwd=0.2, nose=0.28, skull_size="medium", neck=4, ribs=6, rib_sides=10,
        rib0=0.12, rib1=0.58, sac=0.82, digits=(5, 4), spine_r=0.14, rib_depth=0.92, lumbar=2,
        head_box=((-0.3, 0.5, -0.62), (0.3, 1.1, -0.2))),
}


def build(animal):
    if animal.name == "Cobra":
        return {"body": Snake(animal).body()}
    p = dict(Q.BASE)
    p.update(PINNIPED[animal.name])
    p["family"] = "pinniped"
    return {"body": Pinniped(animal, p).body()}


# ---------------------------------------------------------------- closed lofts with flat caps

def ring_points(c, U, V, hw, top, bot=None, sides=6, angle0=None):
    """One ring: an ellipse of half-width hw along U and half-heights top / bot along +V / -V round c. Sector 0
    is centred on +V (the top) with the default angle0, as in prims.loft."""
    bot = top if bot is None else bot
    if angle0 is None:
        angle0 = math.pi / 2.0 - math.pi / sides
    th = angle0 + 2.0 * math.pi * np.arange(sides) / sides
    co, si = np.cos(th), np.sin(th)
    y = np.where(si >= 0, si * top, si * bot)
    return (np.asarray(c, dtype=np.float64)[None, :] + (hw * co)[:, None] * np.asarray(U)[None, :]
            + y[:, None] * np.asarray(V)[None, :])


def closed_loft(rings, band_mat=BONE, keep=(), name=""):
    """A closed shell through `rings` (list of (sides, 3) arrays, one side count), joined by quad bands and
    closed by flat polygon fans (sides - 2 triangles each, no pole), wound clockwise from outside.
    band_mat: one material or one per band; keep: sectors kept BONE inside a non-BONE band (as prims.loft)."""
    n = len(rings)
    s = len(rings[0])
    verts = np.concatenate([np.asarray(r, dtype=np.float64) for r in rings])
    bm = band_mat if isinstance(band_mat, (list, tuple)) else [band_mat] * (n - 1)
    keep = set(keep)
    tris, mats = [], []
    for i in range(n - 1):
        a0, b0 = i * s, (i + 1) * s
        for k in range(s):
            k1 = (k + 1) % s
            m = bm[i]
            if m != BONE and k in keep:
                m = BONE
            tris += [(a0 + k, b0 + k, b0 + k1), (a0 + k, b0 + k1, a0 + k1)]
            mats += [m, m]
    e = (n - 1) * s
    for k in range(1, s - 1):
        tris.append((0, k, k + 1))
        tris.append((e, e + k + 1, e + k))
        mats += [BONE, BONE]
    return P.orient(P.Part(verts, np.array(tris), mats, None, name))


def frames_sagittal(points):
    """Tangent T, side U (+x) and dorsal V = T x U along a path in the x = 0 plane, without flips: for a path
    running head to tail the dorsal side is up where it runs back and backward where it runs down."""
    Pt = np.asarray(points, dtype=np.float64)
    T = np.zeros_like(Pt)
    for i in range(len(Pt)):
        a, b = max(0, i - 1), min(len(Pt) - 1, i + 1)
        T[i] = P.unit(Pt[b] - Pt[a])
    U = np.tile([1.0, 0.0, 0.0], (len(Pt), 1))
    V = np.stack([np.zeros(len(Pt)), T[:, 2], -T[:, 1]], axis=1)
    V = V / np.maximum(np.linalg.norm(V, axis=1, keepdims=True), 1e-9)
    return T, U, V


def rod_path(points, radii, sides=6, side=(1.0, 0.0, 0.0), up=(0.0, 1.0, 0.0), name=""):
    """A round tube along a polyline, one ring per point (radius per point, at least MIN_R), flat caps."""
    Pt = np.asarray(points, dtype=np.float64)
    _, U, V = P.path_frames(Pt, side, up)
    r = np.broadcast_to(np.asarray(radii, dtype=np.float64), (len(Pt),))
    rings = [ring_points(Pt[i], U[i], V[i], max(P.MIN_R, r[i]), max(P.MIN_R, r[i]), sides=sides)
             for i in range(len(Pt))]
    return closed_loft(rings, name=name)


def rod(p0, p1, r0, r1=None, sides=6, name=""):
    return rod_path([p0, p1], [r0, r0 if r1 is None else r1], sides=sides, name=name)


# ---------------------------------------------------------------- living mesh helpers

def unique_points(tris):
    return np.unique(np.round(np.asarray(tris).reshape(-1, 3), 4), axis=0) + 0.0


def plane_section(tris, p, n, radius):
    """Points where the triangle soup crosses the plane through p with normal n, within `radius` of p."""
    d = (tris - p[None, None, :]) @ n
    out = []
    for i, j in ((0, 1), (1, 2), (2, 0)):
        di, dj = d[:, i], d[:, j]
        m = di * dj < 0
        if np.any(m):
            t = di[m] / (di[m] - dj[m])
            a, b = tris[m, i], tris[m, j]
            out.append(a + (b - a) * t[:, None])
    if not out:
        return np.zeros((0, 3))
    q = np.concatenate(out)
    return q[np.linalg.norm(q - p[None, :], axis=1) <= radius]


def inside_x0(tris, ys, zs):
    """Grid (len(ys), len(zs)) of booleans: is the point (0, y, z) inside the closed shell `tris` (an odd number
    of its triangles crossed by the ray from there toward +x)."""
    cnt = np.zeros((len(ys), len(zs)), dtype=np.int32)
    for A, B, C in tris:
        i0, i1 = np.searchsorted(ys, min(A[1], B[1], C[1])), np.searchsorted(ys, max(A[1], B[1], C[1]), side="right")
        j0, j1 = np.searchsorted(zs, min(A[2], B[2], C[2])), np.searchsorted(zs, max(A[2], B[2], C[2]), side="right")
        if i0 >= i1 or j0 >= j1:
            continue
        e0y, e0z = B[1] - A[1], B[2] - A[2]
        e1y, e1z = C[1] - A[1], C[2] - A[2]
        den = e0y * e1z - e1y * e0z
        if abs(den) < 1e-14:
            continue
        Z, Y = np.meshgrid(zs[j0:j1], ys[i0:i1])
        wy, wz = Y - A[1], Z - A[2]
        u = (wy * e1z - e1y * wz) / den
        v = (e0y * wz - wy * e0z) / den
        x = A[0] + u * (B[0] - A[0]) + v * (C[0] - A[0])
        cnt[i0:i1, j0:j1] += ((u >= 0) & (v >= 0) & (u + v < 1) & (x > 0)).astype(np.int32)
    return (cnt % 2) == 1


def smooth_path(points, step, sigma):
    """Resamples a polyline every `step` and smooths it with a Gaussian of `sigma` (a length); the first and
    last points are kept."""
    Pt = np.asarray(points, dtype=np.float64)
    n = max(2, int(round(P.path_length(Pt) / step)))
    R = P.resample(Pt, n)
    k = max(1, int(round(sigma / step)))
    w = np.exp(-0.5 * (np.arange(-3 * k, 3 * k + 1) / k) ** 2)
    w /= w.sum()
    pad = np.concatenate([np.repeat(R[:1], 3 * k, axis=0), R, np.repeat(R[-1:], 3 * k, axis=0)])
    S = np.stack([np.convolve(pad[:, c], w, mode="valid") for c in range(3)], axis=1)
    S[0], S[-1] = R[0], R[-1]
    return S


class Path:
    """A smoothed centre line: points, arc length s, frames, and a lookup by s."""

    def __init__(self, pts, frames):
        self.p = np.asarray(pts, dtype=np.float64)
        seg = np.linalg.norm(np.diff(self.p, axis=0), axis=1)
        self.s = np.concatenate([[0.0], np.cumsum(seg)])
        self.L = float(self.s[-1])
        self.T, self.U, self.V = frames(self.p)

    def at(self, s):
        """(point, T, U, V) at arc length s (clamped to the line)."""
        s = min(max(float(s), 0.0), self.L)
        i = int(np.searchsorted(self.s, s, side="right") - 1)
        i = min(max(i, 0), len(self.s) - 2)
        f = (s - self.s[i]) / max(self.s[i + 1] - self.s[i], 1e-12)
        p = self.p[i] + (self.p[i + 1] - self.p[i]) * f
        T = P.unit(self.T[i] * (1 - f) + self.T[i + 1] * f)
        U = self.U[i] * (1 - f) + self.U[i + 1] * f
        U = P.unit(U - (U @ T) * T)
        V = np.cross(T, U)
        V = P.unit(V) if np.linalg.norm(V) > 1e-9 else self.V[i]
        if V @ (self.V[i] * (1 - f) + self.V[i + 1] * f) < 0:
            V = -V
        return p, T, U, V

    def nearest_s(self, q, dims=(0, 1, 2)):
        """Arc length of the line's point nearest q, measured in the given axes only."""
        d = np.linalg.norm((self.p - np.asarray(q)[None, :])[:, list(dims)], axis=1)
        return float(self.s[int(np.argmin(d))])


# ---------------------------------------------------------------- pinnipeds

class Flipper:
    """A living flipper piece (the right one, x > 0): centre c, unit long axis a1 from the root (at the body)
    toward the tip, width axis a2, plane normal n (up), half length hl, half width hw."""

    def __init__(self, comp, front):
        q = unique_points(comp.tris)
        m = q.mean(axis=0)
        _, _, vt = np.linalg.svd(q - m)
        a1, a2, a3 = vt[0], vt[1], vt[2]
        p1, p2 = (q - m) @ a1, (q - m) @ a2
        c = m + a1 * 0.5 * (p1.max() + p1.min()) + a2 * 0.5 * (p2.max() + p2.min())
        self.hl = 0.5 * float(p1.max() - p1.min())
        self.hw = 0.5 * float(p2.max() - p2.min())
        e0, e1 = c - a1 * self.hl, c + a1 * self.hl
        # the root: the inner end of a front flipper, the front end of a hind one
        if (abs(e0[0]) < abs(e1[0])) if front else (e0[2] < e1[2]):
            self.root, self.tip, self.a1 = e0, e1, a1
        else:
            self.root, self.tip, self.a1 = e1, e0, -a1
        key = a2[2] if abs(a2[2]) > 1e-6 else a2[0]      # a fixed sign, so the fan's order is stable
        self.a2 = a2 if key >= 0 else -a2
        self.n = a3 if a3[1] >= 0 else -a3
        self.c = c


class Pinniped(Q.Skeleton):
    """quadruped.Skeleton for the skull, its sockets and the tusks; everything else along the sagittal centre
    line. Skeleton.__init__ needs hips, so the attributes it sets are set here (from the trunk where they are
    about hips, for any Skeleton method that reads them)."""

    def __init__(self, animal, p):
        animal.overrides = p
        self.a = animal
        self.p = p
        self.an = animal.an
        self.hd = self.an.head
        self.tr = self.an.trunk
        self.size = animal.size
        self.LL = 0.0
        self.W = float(self.tr.half_width)
        self.zfh, self.zrh = float(self.tr.z_front), float(self.tr.z_rear)
        self.Hf = self.Hr = 0.0
        h = self.tr.top - self.tr.bottom
        self.rs = max(P.MIN_R + 0.002, min(p["spine_r"] * self.W, 0.11 * h))
        self._classify()

    def _classify(self):
        """Flippers: pairs of mirrored pieces (same triangle count) low on the body; the frontmost pair is the
        front flippers, the rearmost the hind ones. The body for the centre line and the cross-sections:
        everything else but the head, the eyes and the tusks."""
        comps = self.an.comps
        lo, hi = self.a.body.bounds()
        H = float(hi[1] - lo[1])
        rest = [c for c in comps if c.kind == ""]
        right, paired = [], set()
        for i, c in enumerate(rest):
            if c.centre[0] <= 0.004 or i in paired:
                continue
            for j, m in enumerate(rest):
                if j != i and j not in paired and m.n == c.n and \
                        np.linalg.norm(m.centre - c.centre * [-1.0, 1.0, 1.0]) < 0.02:
                    paired |= {i, j}
                    right.append(c)
                    break
        low = sorted([c for c in right if c.centre[1] < lo[1] + 0.3 * H], key=lambda c: float(c.centre[2]))
        self.front = Flipper(low[0], True) if low else None
        self.rear = Flipper(low[-1], False) if len(low) > 1 else None
        mid = [c for c in comps if c.kind == "trunk"]
        mid += [c for i, c in enumerate(rest) if i not in paired]
        self.mid_comps = mid
        self.mid_tris = np.concatenate([c.tris for c in mid])

    # ---------------- the centre line
    def occiput(self):
        rb = self.skull_rings[0]
        return np.array([0.0, rb.c[1] - 0.15 * rb.top, rb.c[2] - 0.05 * rb.top])

    def centre_line(self):
        from scipy.ndimage import distance_transform_edt
        from scipy.sparse import coo_matrix
        from scipy.sparse.csgraph import dijkstra
        from skimage.morphology import skeletonize
        res = 0.004
        pts3 = self.mid_tris.reshape(-1, 3)
        lo, hi = pts3.min(axis=0), pts3.max(axis=0)
        ys = np.arange(lo[1] - 2 * res, hi[1] + 2 * res, res)
        zs = np.arange(lo[2] - 2 * res, hi[2] + 2 * res, res)
        mask = np.zeros((len(ys), len(zs)), dtype=bool)
        for c in self.mid_comps:
            mask |= inside_x0(c.tris, ys, zs)
        sk = skeletonize(mask)
        dist = distance_transform_edt(mask) * res
        idx = np.argwhere(sk)
        n = len(idx)
        id_of = -np.ones(mask.shape, dtype=np.int64)
        id_of[idx[:, 0], idx[:, 1]] = np.arange(n)
        a, b, w = [], [], []
        for di, dj in ((0, 1), (1, -1), (1, 0), (1, 1)):
            r, c = idx[:, 0] + di, idx[:, 1] + dj
            k = np.nonzero((r >= 0) & (r < mask.shape[0]) & (c >= 0) & (c < mask.shape[1]))[0]
            nb = id_of[r[k], c[k]]
            good = nb >= 0
            a.append(k[good])
            b.append(nb[good])
            w.append(np.full(int(good.sum()), math.hypot(di, dj)))
        a, b, w = np.concatenate(a), np.concatenate(b), np.concatenate(w)
        G = coo_matrix((np.concatenate([w, w]), (np.concatenate([a, b]), np.concatenate([b, a]))),
                       shape=(n, n)).tocsr()
        world = np.stack([np.zeros(n), ys[idx[:, 0]], zs[idx[:, 1]]], axis=1)
        start = int(np.lexsort((idx[:, 0], -idx[:, 1]))[0])     # the rearmost skeleton pixel (the tail end)
        D, pred = dijkstra(G, directed=False, indices=start, return_predecessors=True)
        occ = self.occiput()
        cand = np.nonzero(np.isfinite(D))[0]
        tgt = int(cand[np.argmin(np.linalg.norm(world[cand] - occ, axis=1))])
        chain = [tgt]
        while chain[-1] != start:
            chain.append(int(pred[chain[-1]]))
        raw = np.vstack([occ[None, :], world[chain]])
        md = dist[idx[chain, 0], idx[chain, 1]]
        md = np.concatenate([md[:1], md])
        self.path = Path(smooth_path(raw, 0.005, 0.035), frames_sagittal)
        # the medial half-thickness along the line (by the raw points' share of the length)
        raw_s = np.concatenate([[0.0], np.cumsum(np.linalg.norm(np.diff(raw, axis=0), axis=1))])
        self.md_s, self.md = raw_s * (self.path.L / max(raw_s[-1], 1e-9)), md

    def section(self, s):
        """(point, T, U, V, hw, top, bot): the living body (head and flippers left out) cut square to the centre
        line at s; its half-width, and the reach of the back (top) and of the belly (bot) along V."""
        p, T, U, V = self.path.at(s)
        md = float(np.interp(s, self.md_s, self.md))
        q = plane_section(self.mid_tris, p, T, 2.6 * md + 0.04)
        if len(q) < 3:
            return p, T, U, V, md, md, md
        rel = q - p
        hw = float(np.abs(rel[:, 0]).max())
        return p, T, U, V, hw, max(float((rel @ V).max()), 0.02), max(float((-(rel @ V)).max()), 0.02)

    def spine_at(self, s):
        """The spine's point at s: under the back by max(1.2 rs, 10% of the thickness)."""
        p, T, U, V, hw, top, bot = self.section(s)
        return p + V * (top - max(1.2 * self.rs, 0.10 * (top + bot))), T, U, V, hw, top, bot

    def rib_ring(self, s, bot_raise=0.0):
        """The ribcage's cross-section at s before the rib ridges: centre, frame, half-width, half-height (from
        just over the spine down to 10% over the belly, times rib_depth), as quadruped.Skeleton.ribcage."""
        p, T, U, V, hw, top, bot = self.section(s)
        t_ = (top - max(1.2 * self.rs, 0.10 * (top + bot))) + 0.4 * self.rs
        b_ = min(-bot + (0.10 + bot_raise) * (top + bot), t_ - 0.04)
        hh = 0.5 * (t_ - b_) * self.p["rib_depth"]
        return p + V * (t_ - hh), T, U, V, SHRINK * hw, hh

    # ---------------- the body
    def body(self):
        p = self.p
        size = self.size
        self.size = p.get("skull_size", size)
        parts = self.skull()
        tusks = self.extras()
        self.size = size
        for q in tusks:
            q.name = "tusk"
        self.centre_line()
        L = self.path.L
        self.s_rib0, self.s_rib1, self.s_sac = p["rib0"] * L, p["rib1"] * L, p["sac"] * L
        # the spine's radius from the trunk at the middle of the ribcage
        _, _, _, _, hw, top, bot = self.section(0.5 * (self.s_rib0 + self.s_rib1))
        self.rs = max(P.MIN_R + 0.004, min(p["spine_r"] * hw, 0.11 * (top + bot)))
        parts.append(self.ribcage_path())
        parts.append(self.spine_path())
        parts.append(self.neck_chain())
        parts += self.pelvis_pair()
        parts.append(self.tail_chain())
        parts += self.front_flippers()
        parts += self.rear_flippers()
        parts += tusks
        return [q for q in parts if q is not None and len(q)]

    def ribcage_path(self):
        """Rings square to the centre line from rib0 to rib1: a small front ring, then per rib a ridge ring and
        a groove ring (bone_shade band, spine and sternum sectors kept bone), the last ribs ending higher."""
        p = self.p
        n = int(p["ribs"])
        sides = int(p["rib_sides"])
        s0, s1 = self.s_rib0, self.s_rib1
        pitch = (s1 - s0) / (n + 0.6)

        def ring(s, f, bot_raise=0.0):
            c, _, U, V, hw, hh = self.rib_ring(s, bot_raise)
            return ring_points(c, U, V, hw * f, hh * f, hh * f, sides=sides)

        rings = [ring(s0, 0.55, 0.25)]
        s = s0 + 0.45 * pitch
        for i in range(n):
            f = i / max(1, n - 1)
            raise_ = 0.25 * max(0.0, f - 0.5) * 2
            s_main = 0.80 + 0.2 * min(1.0, (i + 1) / 2.0) - 0.08 * max(0.0, f - 0.6) / 0.4
            rings.append(ring(s, s_main * 1.04, raise_))
            rings.append(ring(s + 0.42 * pitch, s_main * 0.95, raise_))
            s += pitch
        rings.append(ring(s1, 0.62, 0.35))
        band = [BONE] + [SHADE, BONE] * n
        return closed_loft(rings, band_mat=band[: len(rings) - 1], keep={0, sides // 2}, name="ribcage")

    def spine_path(self):
        """Thoracic spine (a plain rod under the ribcage's top), then lumbar bumps to the sacrum."""
        rs = self.rs
        s0, s1, s2 = self.s_rib0 - 0.02, self.s_rib1, self.s_sac
        pts, rad = [], []
        for s, r in ((s0, 0.9), (0.5 * (s0 + s1), 1.0), (s1, 1.0)):
            pts.append(self.spine_at(s)[0])
            rad.append(rs * r)
        nl = int(self.p["lumbar"])
        for i in range(nl):
            for f, r in ((0.35, 1.18), (0.85, 0.82)):
                pts.append(self.spine_at(s1 + (s2 - s1) * (i + f) / nl)[0])
                rad.append(rs * r)
        pts.append(self.spine_at(s2 + 0.3 * rs)[0])
        rad.append(rs * 0.7)
        sides = 6 if rs < 0.05 else 8
        _, U, V = frames_sagittal(pts)
        return closed_loft([ring_points(pts[i], U[i], V[i], rad[i], rad[i], sides=sides)
                            for i in range(len(pts))], name="spine")

    def neck_chain(self):
        """Neck vertebrae from inside the back of the skull to the front of the spine."""
        rn = self.rs * 1.05
        L = self.s_rib0
        n = max(2, min(int(self.p["neck"]), int(L / (1.3 * rn + 0.008))))
        pts = [self.path.p[0]] + [self.spine_at(s)[0] for s in np.linspace(0.35 * L, L, 6)]
        R = P.resample(smooth_path(pts, 0.005, 0.02), n)
        g = min(0.012, 0.15 * L / n)
        out = []
        for i in range(n):
            d = P.unit(R[i + 1] - R[i])
            out.append(rod(R[i] + d * g / 2, R[i + 1] - d * g / 2, rn, rn))
        return P.merge(out, "neck")

    def tail_chain(self):
        """A few tail vertebrae from the sacrum to the end of the centre line, between the hind flippers."""
        s0 = self.s_sac + 0.3 * self.rs
        s1 = self.path.L - 0.01
        if s1 - s0 < 0.05:
            return None
        r0 = max(P.MIN_R, 0.7 * self.rs)
        r1 = max(P.MIN_R, 0.5 * r0)
        n = max(2, min(4, int((s1 - s0) / (3.0 * r0 + 0.012))))
        R = P.resample([self.spine_at(s)[0] for s in np.linspace(s0, s1, 4 * n + 1)], n)
        out = []
        for i in range(n):
            d = P.unit(R[i + 1] - R[i])
            g = min(0.008, 0.3 * float(np.linalg.norm(R[i + 1] - R[i])))
            r = r0 + (r1 - r0) * (i + 0.5) / n
            out.append(rod(R[i] + d * g / 2, R[i + 1] - d * g / 2, r, r))
        return P.merge(out, "tail")

    def pelvis_pair(self):
        """Two hip bones: from the sacrum out and down to the hip socket (in line with the hind flipper's
        root), then back toward the tail. The socket is where the hind leg starts."""
        sp, T, U, V, hw, top, bot = self.spine_at(self.s_sac)
        rs = self.rs
        if self.rear is not None:
            sock_x = max(rs * 1.6, min(0.75 * abs(self.rear.root[0]), SHRINK * hw * 0.7))
        else:
            sock_x = SHRINK * hw * 0.6
        th = max(0.026, 0.9 * rs)
        h = 0.45 * (top + bot)
        A = sp - T * 2.25 * rs + U * 0.9 * rs - V * 0.2 * rs
        B = sp + T * 0.6 * rs - V * 0.55 * h + U * sock_x
        C = sp + T * 3.0 * rs - V * 0.35 * h + U * 0.6 * sock_x
        self.socket = B
        out = []
        for sgn in (1.0, -1.0):
            m = np.array([sgn, 1.0, 1.0])
            out.append(rod_path([A * m, B * m, C * m], [0.5 * th, 0.75 * th, 0.45 * th], side=(0.0, 1.0, 0.0),
                                up=(1.0, 0.0, 0.0), name="pelvis"))
        return out

    # ---------------- flippers
    def fan(self, fl, count, wrist_f=0.16, spread=1.15):
        """Finger bones in the flipper's plane, from a wrist near its root fanning out to its far edge (the
        ellipse's points between +-spread radians of its tip, at 0.86 of its size)."""
        out = []
        wrist = fl.root + fl.a1 * (wrist_f * 2 * fl.hl)
        r = max(P.MIN_R, min(0.022, 0.18 * fl.hw))
        for i in range(count):
            t = (i / (count - 1) - 0.5) * 2 if count > 1 else 0.0
            ang = t * spread
            end = fl.c + 0.86 * (fl.a1 * fl.hl * math.cos(ang) + fl.a2 * fl.hw * math.sin(ang))
            base = wrist + fl.a2 * (0.30 * fl.hw * t)
            out.append(rod_path([base, end], [1.1 * r, 0.85 * r], side=tuple(fl.n), up=tuple(fl.a2),
                                name="digit"))
        return out, wrist, r

    def front_flippers(self):
        """Per side: an arm from a shoulder knob on the ribcage's side, a little under its middle (bent back at
        the elbow), to the wrist at the living flipper's root, and the finger fan."""
        fl = self.front
        if fl is None:
            return []
        s = self.path.nearest_s(fl.root, dims=(2,))
        s = min(max(s, self.s_rib0 + 0.1 * (self.s_rib1 - self.s_rib0)), self.s_rib1)
        c, T, U, V, rhw, rhh = self.rib_ring(s)
        digits, wrist, r = self.fan(fl, self.p["digits"][0])
        ra = max(P.MIN_R + 0.004, 1.5 * r)
        th = math.radians(25.0)
        J = c + U * (rhw * math.cos(th) + 0.4 * ra) - V * rhh * math.sin(th)
        E = 0.5 * (J + wrist) + T * 0.25 * float(np.linalg.norm(J - wrist))
        right = [rod_path([J, E, wrist], [1.35 * ra, 1.1 * ra, 0.9 * ra], name="arm")] + digits
        return right + [P.mirror_x(q) for q in right]

    def rear_flippers(self):
        """Per side: a leg from the hip socket (bent out at the knee) to the hind flipper's root, and the toe
        fan to the flipper's far edge."""
        fl = self.rear
        if fl is None:
            return []
        digits, ankle, r = self.fan(fl, self.p["digits"][1], wrist_f=0.12, spread=1.0)
        ra = max(P.MIN_R + 0.004, 1.5 * r)
        B = self.socket
        K = 0.5 * (B + ankle) + np.array([0.25 * (ankle[0] - B[0]) + 0.02, -0.15 * abs(B[1] - ankle[1]), 0.0])
        right = [rod_path([B, K, ankle], [1.35 * ra, 1.1 * ra, 0.9 * ra], name="leg")] + digits
        return right + [P.mirror_x(q) for q in right]


# ---------------------------------------------------------------- cobra

def catmull_rom(points, per_seg=8):
    """A uniform Catmull-Rom spline through the points, per_seg samples per span (each span starting at its
    control point, so sample i * per_seg is point i), ends extended linearly."""
    Pt = np.asarray(points, dtype=np.float64)
    ext = np.vstack([2 * Pt[0] - Pt[1], Pt, 2 * Pt[-1] - Pt[-2]])
    out = []
    for i in range(1, len(ext) - 2):
        p0, p1, p2, p3 = ext[i - 1], ext[i], ext[i + 1], ext[i + 2]
        for t in np.linspace(0.0, 1.0, per_seg, endpoint=False):
            t2, t3 = t * t, t * t * t
            out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2
                              + (-p0 + 3 * p1 - 3 * p2 + p3) * t3))
    out.append(Pt[-1])
    return np.array(out)


class Snake:
    """The cobra's skeleton along the centre line of its living loft (see the module docstring)."""

    RIB_SHARE = 0.8         # the ribbed trunk over this share of the body behind the skull; the tail after it
    RIB_PITCH = 0.06        # one rib ridge per this length of the trunk
    TAIL_PITCH = 0.08       # one tail vertebra per this length
    PER_SPAN = 10           # spline samples between two living rings

    def __init__(self, animal):
        self.a = animal
        self._rings()

    def _rings(self):
        m = self.a.body
        comps = analyze.components(m)
        dark = [c for c in comps if all(analyze.is_dark(x) for x in c.mats)]
        body = max([c for c in comps if c not in dark], key=lambda c: c.n)
        pts, inv = np.unique(np.round(body.tris.reshape(-1, 3), 4) + 0.0, axis=0, return_inverse=True)
        inv = inv.reshape(-1, 3)
        nb = [set() for _ in range(len(pts))]
        on_belly = np.zeros(len(pts), dtype=bool)
        for t, ti in zip(inv, body.idx):
            a, b, c = (int(x) for x in t)
            nb[a] |= {b, c}
            nb[b] |= {a, c}
            nb[c] |= {a, b}
            if "belly" in m.mat[ti].lower():
                on_belly[[a, b, c]] = True
        eyes = [c.centre for c in dark if abs(c.centre[0]) > 0.01]
        self.eyes = sorted(eyes, key=lambda e: float(e[0]))
        # the snout tip: the frontmost vertex near the eyes (the loft's pole)
        E = np.mean(eyes, axis=0) if eyes else pts[int(np.argmax(pts[:, 1]))]
        near = np.nonzero(np.linalg.norm(pts - E, axis=1) < 0.15)[0]
        pole = int(near[np.argmin(pts[near, 2])])
        layers = [[pole]]
        seen = {pole}
        while True:
            nxt = sorted({j for i in layers[-1] for j in nb[i]} - seen)
            if not nxt:
                break
            layers.append(nxt)
            seen |= set(nxt)
        sizes = sorted({len(L) for L in layers[1:]})
        if len(layers) < 6 or len(sizes) != 1:
            raise ValueError("the living cobra isn't one loft of equal rings from a snout pole (ring sizes %s)"
                             % sizes)
        cent, dors = [], []
        for L in layers:
            q = pts[L]
            c = q.mean(axis=0)
            cent.append(c)
            bq = q[on_belly[L]]
            dors.append(P.unit(c - bq.mean(axis=0)) if len(bq) and len(L) > 1 else np.array([0.0, 1.0, 0.0]))
        self.cent = np.array(cent)
        self.dors = np.array(dors)
        self.line = catmull_rom(self.cent, self.PER_SPAN)
        seg = np.linalg.norm(np.diff(self.line, axis=0), axis=1)
        self.ls = np.concatenate([[0.0], np.cumsum(seg)])
        self.L = float(self.ls[-1])
        self.cs = np.array([self.ls[min(i * self.PER_SPAN, len(self.ls) - 1)] for i in range(len(self.cent))])
        # each ring's half-width (along U) and half-height (along V) in its own frame
        hw, hh = [], []
        for i, L in enumerate(layers):
            _, _, U, V = self.frame(self.cs[i])
            rel = pts[L] - self.cent[i]
            hw.append(float(np.abs(rel @ U).max()))
            hh.append(float(np.abs(rel @ V).max()))
        self.hw = np.array(hw)
        self.hh = np.array(hh)

    def frame(self, s):
        """(point, T toward the tail, U sideways, V dorsal) at arc length s."""
        s = min(max(float(s), 0.0), self.L)
        i = int(np.searchsorted(self.ls, s, side="right") - 1)
        i = min(max(i, 0), len(self.ls) - 2)
        f = (s - self.ls[i]) / max(self.ls[i + 1] - self.ls[i], 1e-12)
        p = self.line[i] + (self.line[i + 1] - self.line[i]) * f
        T = P.unit(self.line[i + 1] - self.line[i])
        dv = np.array([np.interp(s, self.cs, self.dors[:, k]) for k in range(3)])
        V = dv - (dv @ T) * T
        if np.linalg.norm(V) < 1e-6:
            V = np.array([0.0, 1.0, 0.0]) - T[1] * T
        V = P.unit(V)
        return p, T, P.unit(np.cross(V, T)), V

    def size_at(self, s):
        return float(np.interp(s, self.cs, self.hw)), float(np.interp(s, self.cs, self.hh))

    def nearest_s(self, q):
        return float(self.ls[int(np.argmin(np.linalg.norm(self.line - np.asarray(q)[None, :], axis=1)))])

    def body(self):
        parts = self.skull() + self.spine_and_ribs()
        return [q for q in parts if q is not None and len(q)]

    def skull(self):
        """Snake skull from the snout tip to a quarter of the way past the head's back ring (the hood starts
        there): flat braincase, narrow snout; sockets; lower jaw."""
        s_back = self.cs[2] + 0.25 * (self.cs[3] - self.cs[2])
        self.s_skull = s_back
        prof = [(0.0, 0.55, 0.55), (0.25, 0.90, 0.80), (0.55, 1.10, 1.0), (0.8, 1.15, 1.1), (1.0, 0.95, 0.95)]
        ref_hw = self.size_at(self.cs[2])[0]
        rings, info = [], []
        for t, fw, fh in prof:
            s = 0.012 + t * (s_back - 0.012)
            p, _, U, V = self.frame(s)
            hw, hh = self.size_at(max(s, self.cs[1]))
            hw = SHRINK * max(hw, 0.6 * ref_hw) * fw
            top = SHRINK * hh * fh
            c = p + V * 0.12 * hh
            rings.append(ring_points(c, U, V, hw, top, 0.8 * top, sides=8))
            info.append((s, c, hw, top))
        out = [closed_loft(rings, name="skull")]
        # eye sockets at the living eyes, on the skull's flanks a little under the middle, facing out: small,
        # since a socket's outline (0.03 all round) would otherwise cover the top of so small a skull
        for e in self.eyes:
            s = self.nearest_s(e)
            p, T, U, V = self.frame(s)
            _, c, hw, top = min(info, key=lambda r: abs(r[0] - s))
            side = 1.0 if (np.asarray(e) - p) @ U > 0 else -1.0
            out.append(P.lens(c + U * side * hw * 0.88 - V * 0.1 * top, P.unit(U * side + V * 0.1), P.MIN_R, 0.010,
                              sides=6, name="socket"))
        # lower jaw: a V from under the back of the skull on each side to the chin
        p0, T0, _, V0 = self.frame(0.02)
        hw0, hh0 = self.size_at(self.cs[1])
        chin = p0 - V0 * 0.55 * hh0 * SHRINK
        pb, _, Ub, Vb = self.frame(s_back + 0.02)
        hwb, hhb = self.size_at(s_back)
        ends = [pb - Vb * 0.7 * hhb * SHRINK + Ub * sgn * 0.75 * SHRINK * max(hwb, hw0) for sgn in (-1.0, 1.0)]
        jr = P.MIN_R
        out.append(rod_path([ends[0], chin, ends[1]], [jr, 1.2 * jr, jr], side=tuple(V0), up=tuple(-T0),
                            name="jaw"))
        return out

    def spine_and_ribs(self):
        """The ribbed trunk from inside the back of the skull over RIB_SHARE of the body, then the tail's
        vertebrae to the tail tip. Both are single lofts: at map zoom a snake is a few pixels wide, so separate
        vertebrae and rib rods came out as a dark band of outlines with white dashes in it."""
        s0 = self.s_skull - 0.012
        s1 = self.L - 0.004
        rib_end = s0 + self.RIB_SHARE * (s1 - s0)
        return [self.rib_trunk(s0, rib_end), self.tail_spine(rib_end, s1)]

    MAX_TURN = math.radians(20.0)   # most a loft's ring may turn about the line against the ring before it
    LOFT_BEND = 0.034               # tightest bend radius of the line the lofts follow (over their half-width)

    def loft_line(self):
        """The centre line for the trunk and tail lofts: resampled every 0.005 and relaxed (a few neighbour
        averages at a time) only where it bends tighter than LOFT_BEND, ends kept. The living line turns by 120
        degrees within 0.04 where the raised neck meets the coil; rings square to it there folded through each
        other, and the outline drew the fold as a spiky knot. Cutting that corner moves the line at most ~0.03
        toward the inside of the hairpin, where the living body overlaps itself anyway."""
        if getattr(self, "_loft", None) is None:
            n = max(3, int(round(self.L / 0.005)))
            S = P.resample(self.line, n)
            for _ in range(2000):
                a, b, c = S[:-2], S[1:-1], S[2:]
                cr = np.linalg.norm(np.cross(b - a, c - b), axis=1)
                rad = (np.linalg.norm(b - a, axis=1) * np.linalg.norm(c - b, axis=1) * np.linalg.norm(c - a, axis=1)
                       / np.maximum(2.0 * cr, 1e-12))
                tight = np.zeros(len(S), dtype=bool)
                tight[1:-1] = rad < self.LOFT_BEND
                if not tight.any():
                    break
                tight[1:-1] |= tight[:-2] | tight[2:]
                tight[0] = tight[-1] = False
                S2 = S.copy()
                S2[1:-1] = 0.5 * S[1:-1] + 0.25 * (S[:-2] + S[2:])
                S = np.where(tight[:, None], S2, S)
            self._loft = S
        return self._loft

    def loft_point(self, s):
        """(point, T) on loft_line where the living line's point at s went: its samples keep their index when
        relaxed, so a ring stays beside its living ring (the cut corner bunches them a little)."""
        S = self.loft_line()
        u = min(max(float(s) / self.L, 0.0), 1.0) * (len(S) - 1)
        i = min(int(u), len(S) - 2)
        f = u - i
        return S[i] + (S[i + 1] - S[i]) * f, P.unit(S[i + 1] - S[i])

    def loft_frames(self, ss):
        """(point, U, V) at the increasing arc lengths ss for a loft along loft_line. Each ring's frame is the
        previous one carried on along the line (turned with the tangent), then turned toward that point's own
        dorsal frame by at most MAX_TURN about the tangent, so the rings never twist against each other where
        the dorsal side swings round quickly."""
        out = []
        T0 = U0 = None
        for s in ss:
            _, _, _, Vd = self.frame(s)
            p, T = self.loft_point(s)
            V = Vd - (Vd @ T) * T
            V = P.unit(V) if np.linalg.norm(V) > 1e-6 else P.unit(np.array([0.0, 1.0, 0.0]) - T[1] * T)
            U = P.unit(np.cross(V, T))
            if U0 is not None:
                # the previous U turned by the rotation that takes the previous tangent to this one (the line
                # can turn by more than 90 degrees between two rings there, so not just projected)
                k = np.cross(T0, T)
                sn, cs = float(np.linalg.norm(k)), float(T0 @ T)
                Uc = U0
                if sn > 1e-9:
                    k = k / sn
                    Uc = U0 * cs + np.cross(k, U0) * sn + k * float(k @ U0) * (1.0 - cs)
                Uc = Uc - (Uc @ T) * T
                if np.linalg.norm(Uc) > 1e-6:
                    Uc = P.unit(Uc)
                    Vc = np.cross(T, Uc)
                    ang = math.atan2(float(np.cross(Vc, V) @ T), float(Vc @ V))
                    th = min(max(ang, -self.MAX_TURN), self.MAX_TURN)
                    U = Uc * math.cos(th) + np.cross(T, Uc) * math.sin(th)
                    V = Vc * math.cos(th) + np.cross(T, Vc) * math.sin(th)
            out.append((p, U, V))
            T0, U0 = T, U
        return out

    def trunk_ring(self, s, frame, f):
        """The ribbed trunk's cross-section at s, scaled by f: at f = 1 the ribs reach 0.8 of the living
        half-width and curve down to 0.68 of its half-height under the centre, which sits a little over the
        body's (the spine along the top). In the hood (rings much wider than deep) that is a wide flat plate."""
        p, U, V = frame
        hw, hh = self.size_at(s)
        return ring_points(p + V * 0.05 * hh, U, V, 0.80 * hw * f, 0.80 * hh * f, 0.68 * hh * f, sides=6)

    def rib_trunk(self, s0, s1):
        """Rings square to the centre line: a small front ring, then per rib a ridge ring and a groove ring
        (bone_shade band, the top sector kept bone so the spine runs on unbroken), a small end ring, as the
        pinnipeds' ribcage."""
        n = max(2, int(round((s1 - s0) / self.RIB_PITCH)))
        pitch = (s1 - s0) / (n + 0.6)
        ss, fs = [s0], [0.6]
        for i in range(n):
            s = s0 + (0.5 + i) * pitch
            ss += [s, s + 0.42 * pitch]
            fs += [1.0, 0.84]
        ss.append(s1)
        fs.append(0.6)
        rings = [self.trunk_ring(s, fr, f) for s, fr, f in zip(ss, self.loft_frames(ss), fs)]
        band = [BONE] + [SHADE, BONE] * n
        return closed_loft(rings, band_mat=band, keep={0}, name="ribs")

    def tail_spine(self, s_root, s1):
        """The tail's vertebrae as one rod from inside the ribbed trunk's end to the tail tip, just under the
        trunk's top, tapering: per vertebra a full-width middle between pinched joints in bone_shade."""
        s0 = s_root - 0.04
        n = max(2, int(round((s1 - s0) / self.TAIL_PITCH)))
        pitch = (s1 - s0) / n
        ss, fs = [s0], [0.8]
        for i in range(n):
            a = s0 + i * pitch
            ss += [a + 0.18 * pitch, a + 0.82 * pitch, a + pitch]
            fs += [1.0, 1.0, 0.78 if i < n - 1 else 0.7]
        rings = []
        for s, (p, U, V), f in zip(ss, self.loft_frames(ss), fs):
            hw, hh = self.size_at(s)
            t = (s - s0) / max(s1 - s0, 1e-9)
            r = max(P.MIN_R, max(P.MIN_R + 0.002, 0.45 * min(hw, hh) * (1.0 - 0.3 * t)) * f)
            rings.append(ring_points(p + V * 0.35 * hh, U, V, r, r, sides=6))
        band = [SHADE, BONE, SHADE] * n
        band[-1] = BONE
        return closed_loft(rings, band_mat=band, name="spine")
