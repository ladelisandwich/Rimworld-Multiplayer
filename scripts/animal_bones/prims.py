"""Closed mesh primitives for the animal skeleton generator (Multiplayer port, not shipped).

Everything here is in FILE axes (x = width, y = up, ground at y = 0, the animal faces file -z) and every
primitive returns a Part whose shells are closed and wound clockwise seen from outside in file axes, i.e. with
a NEGATIVE signed volume, like IsoRim's human skeleton files (the loader flips z, which turns that into
outward normals in game axes).

A Part is an indexed triangle mesh: verts (V, 3), tris (T, 3) and one material name per triangle; `col` is
an optional (T, 3) colour per triangle (NaN rows: none) that objio writes as per-vertex colours.

Lofts are the workhorse: a list of rings, each an (optionally superelliptic, tapered, asymmetric) ellipse in
the plane spanned by its frame (U = sideways, V = "up" of the cross-section), joined by bands of quads split
into two triangles, closed at both ends by a pole (a cone tip, or a flat fan when the pole length is 0).
"""

import math

import numpy as np

BONE = "bone"
SHADE = "bone_shade"
DARK = "an_dark"

BONE_RGB = (0.800, 0.785, 0.745)
SHADE_RGB = (0.520, 0.505, 0.480)
DARK_RGB = (0.110, 0.100, 0.100)

MIN_R = 0.012      # thinnest bone radius the outline lets through (spec: >= 0.012)


def sides_for(r, lo=6, hi=16, edge=0.07):
    """The spec's side count: clamp(round(2*pi*r / 0.07), 6, 16)."""
    return int(min(hi, max(lo, round(2.0 * math.pi * float(r) / edge))))


def unit(v):
    v = np.asarray(v, dtype=np.float64)
    n = float(np.linalg.norm(v))
    return v / n if n > 1e-12 else v * 0.0


class Part:
    def __init__(self, verts, tris, mat, col=None, name=""):
        self.verts = np.asarray(verts, dtype=np.float64).reshape(-1, 3)
        self.tris = np.asarray(tris, dtype=np.int64).reshape(-1, 3)
        if isinstance(mat, str):
            mat = [mat] * len(self.tris)
        self.mat = list(mat)
        self.col = None if col is None else np.asarray(col, dtype=np.float64).reshape(-1, 3)
        self.name = name
        assert len(self.mat) == len(self.tris)

    def __len__(self):
        return len(self.tris)

    def copy(self):
        return Part(self.verts.copy(), self.tris.copy(), list(self.mat),
                    None if self.col is None else self.col.copy(), self.name)

    def bounds(self):
        return self.verts.min(axis=0), self.verts.max(axis=0)


# ---------------------------------------------------------------- helpers

def merge(parts, name=""):
    """Concatenates parts (their shells stay separate)."""
    parts = [p for p in parts if p is not None and len(p) > 0]
    if not parts:
        return Part(np.zeros((0, 3)), np.zeros((0, 3), np.int64), [], None, name)
    verts, tris, mat, cols = [], [], [], []
    any_col = any(p.col is not None for p in parts)
    base = 0
    for p in parts:
        verts.append(p.verts)
        tris.append(p.tris + base)
        mat.extend(p.mat)
        if any_col:
            cols.append(p.col if p.col is not None else np.full((len(p.tris), 3), np.nan))
        base += len(p.verts)
    return Part(np.concatenate(verts), np.concatenate(tris), mat, np.concatenate(cols) if any_col else None,
                name or parts[0].name)


def transform(part, M=None, t=None):
    """New part with verts @ M.T + t; a mirroring M (det < 0) also flips the winding to stay closed outward."""
    p = part.copy()
    if M is not None:
        M = np.asarray(M, dtype=np.float64)
        p.verts = p.verts @ M.T
        if np.linalg.det(M) < 0:
            p.tris = p.tris[:, ::-1].copy()
    if t is not None:
        p.verts = p.verts + np.asarray(t, dtype=np.float64)
    return p


def translate(part, t):
    return transform(part, None, t)


def mirror_x(part):
    return transform(part, np.diag([-1.0, 1.0, 1.0]))


def scale_about(part, centre, s):
    c = np.asarray(centre, dtype=np.float64)
    M = np.diag(np.broadcast_to(np.asarray(s, dtype=np.float64), (3,)))
    return transform(translate(part, -c), M, c)


def rot_x(deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]], dtype=np.float64)


def rot_y(deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]], dtype=np.float64)


def rot_z(deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]], dtype=np.float64)


def signed_volume(part, tri_idx=None):
    t = part.tris if tri_idx is None else part.tris[tri_idx]
    a, b, c = part.verts[t[:, 0]], part.verts[t[:, 1]], part.verts[t[:, 2]]
    return float(np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6.0)


def shells(part):
    """Lists of triangle indices, one per connected shell (triangles sharing a vertex index)."""
    n = len(part.verts)
    parent = list(range(n))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for a, b, c in part.tris:
        for u, v in ((a, b), (a, c)):
            ru, rv = find(int(u)), find(int(v))
            if ru != rv:
                parent[rv] = ru
    groups = {}
    for i, t in enumerate(part.tris):
        groups.setdefault(find(int(t[0])), []).append(i)
    return [np.array(g, dtype=np.int64) for g in sorted(groups.values(), key=lambda g: g[0])]


def orient(part, sign=-1.0):
    """Flips each shell whose signed volume doesn't have `sign` (default negative = clockwise from outside)."""
    p = part.copy()
    for s in shells(p):
        v = signed_volume(p, s)
        if v * sign < 0:
            p.tris[s] = p.tris[s][:, ::-1]
    return p


def open_edges(part):
    """Number of directed edges without exactly one opposite partner (0 for a closed, consistently wound
    manifold)."""
    from collections import Counter
    e = Counter()
    for a, b, c in part.tris:
        for u, v in ((a, b), (b, c), (c, a)):
            e[(int(u), int(v))] += 1
    bad = 0
    for (u, v), n in e.items():
        if n != 1 or e.get((v, u), 0) != 1:
            bad += 1
    return bad


def weld(part, decimals=4):
    """Merges vertices that round to the same position (and drops unused ones)."""
    key = np.round(part.verts, decimals) + 0.0
    uniq, inv = np.unique(key, axis=0, return_inverse=True)
    inv = inv.reshape(-1)
    # keep first-seen order for determinism of the written file
    order = {}
    remap = np.zeros(len(uniq), dtype=np.int64)
    new_verts = []
    for i in range(len(part.verts)):
        u = int(inv[i])
        if u not in order:
            order[u] = len(new_verts)
            new_verts.append(part.verts[i])
    for u, j in order.items():
        remap[u] = j
    tris = remap[inv[part.tris]]
    keep = (tris[:, 0] != tris[:, 1]) & (tris[:, 1] != tris[:, 2]) & (tris[:, 0] != tris[:, 2])
    col = None if part.col is None else part.col[keep]
    return Part(np.array(new_verts), tris[keep], [m for m, k in zip(part.mat, keep) if k], col, part.name)


def recolor(part, rgb=None, mat=None):
    p = part.copy()
    if mat is not None:
        p.mat = [mat] * len(p.tris)
    if rgb is None:
        p.col = None
    else:
        p.col = np.tile(np.asarray(rgb, dtype=np.float64), (len(p.tris), 1))
    return p


# ---------------------------------------------------------------- frames

def path_frames(points, side=(1.0, 0.0, 0.0), up=(0.0, 1.0, 0.0)):
    """Tangent T, sideways U and cross-section up V per point of a polyline. U follows `side` projected off
    the tangent (falls back to `up` when the path runs along `side`), V = U x T flipped toward `up`."""
    P = np.asarray(points, dtype=np.float64)
    n = len(P)
    T = np.zeros_like(P)
    for i in range(n):
        if i == 0:
            d = P[1] - P[0]
        elif i == n - 1:
            d = P[-1] - P[-2]
        else:
            d = unit(P[i + 1] - P[i]) + unit(P[i] - P[i - 1])
            if np.linalg.norm(d) < 1e-9:
                d = P[i + 1] - P[i]
        T[i] = unit(d)
    side = unit(side)
    up = unit(up)
    U = np.zeros_like(P)
    V = np.zeros_like(P)
    for i in range(n):
        t = T[i]
        u = side - (side @ t) * t
        if np.linalg.norm(u) < 0.3:
            v = up - (up @ t) * t
            v = unit(v)
            u = unit(np.cross(t, v))
            if u @ side < 0:
                u = -u
        u = unit(u)
        v = unit(np.cross(u, t))
        if v @ up < 0:
            v = -v
        U[i], V[i] = u, v
    return T, U, V


# ---------------------------------------------------------------- the loft

class Ring:
    """One cross-section of a loft.

    c: centre; hw: half-width (along U); top/bot: half-heights above/below the centre (along V);
    power: superellipse exponent (2 = ellipse, higher = boxier); taper: width factor 1 - taper*s (s = +1 at
    the top, -1 at the bottom: taper > 0 widens the bottom); dx/dy: extra offset of this ring in U/V."""

    __slots__ = ("c", "hw", "top", "bot", "power", "taper")

    def __init__(self, c, hw, top, bot=None, power=2.0, taper=0.0):
        self.c = np.asarray(c, dtype=np.float64)
        self.hw = max(float(hw), 1e-4)
        self.top = max(float(top), 1e-4)
        self.bot = max(float(bot if bot is not None else top), 1e-4)
        self.power = float(power)
        self.taper = float(taper)


def _ring_points(r, U, V, sides, angle0):
    k = np.arange(sides)
    th = angle0 + 2.0 * math.pi * k / sides
    c, s = np.cos(th), np.sin(th)
    if abs(r.power - 2.0) > 1e-9:
        e = 2.0 / r.power
        c = np.sign(c) * np.abs(c) ** e
        s = np.sign(s) * np.abs(s) ** e
    x = r.hw * c * (1.0 - r.taper * s)
    y = np.where(s >= 0, s * r.top, s * r.bot)
    return r.c[None, :] + x[:, None] * U[None, :] + y[:, None] * V[None, :]


def loft(rings, sides, frames=None, caps=(0.0, 0.0), band_mat=BONE, keep=None, cap_mat=None,
         angle0=None, side=(1.0, 0.0, 0.0), up=(0.0, 1.0, 0.0), name=""):
    """A closed loft through `rings` (list of Ring).

    frames: (T, U, V) arrays per ring; default from the ring centres with path_frames(side, up).
    caps: pole length at the start and the end along the path (0 = flat fan; None = leave open, only for
          parts closed some other way, never used for output parts).
    band_mat: one material for all bands, or a list (len(rings) - 1).
    keep: sector indices kept as BONE inside a non-BONE band (sector 0 is centred on the top, sector sides/2
          on the bottom, with the default angle0).
    cap_mat: material of the two caps (default: the neighbouring band's, or BONE when that is a shade band).
    """
    n = len(rings)
    assert n >= 2
    if frames is None:
        T, U, V = path_frames([r.c for r in rings], side, up)
    else:
        T, U, V = frames
    if angle0 is None:
        angle0 = math.pi / 2.0 - math.pi / sides
    verts = []
    for i, r in enumerate(rings):
        verts.append(_ring_points(r, U[i], V[i], sides, angle0))
    verts = list(np.concatenate(verts))
    tris = []
    mats = []
    bm = band_mat if isinstance(band_mat, (list, tuple)) else [band_mat] * (n - 1)
    keep = set(keep or ())
    for i in range(n - 1):
        a0 = i * sides
        b0 = (i + 1) * sides
        for k in range(sides):
            k1 = (k + 1) % sides
            m = bm[i]
            if m != BONE and k in keep:
                m = BONE
            tris.append((a0 + k, b0 + k, b0 + k1))
            tris.append((a0 + k, b0 + k1, a0 + k1))
            mats += [m, m]
    for end in (0, 1):
        ext = caps[end]
        if ext is None:
            continue
        i = 0 if end == 0 else n - 1
        t = T[i] * (-1.0 if end == 0 else 1.0)
        pole = rings[i].c + t * float(ext)
        pi = len(verts)
        verts.append(pole)
        base = i * sides
        m = cap_mat or (bm[0] if end == 0 else bm[-1])
        if m == SHADE:
            m = BONE
        for k in range(sides):
            k1 = (k + 1) % sides
            if end == 0:
                tris.append((pi, base + k, base + k1))
            else:
                tris.append((pi, base + k1, base + k))
            mats.append(m)
    p = Part(np.array(verts), np.array(tris), mats, None, name)
    return orient(p)


def loft_path(points, hw, top, bot=None, sides=8, caps=(0.0, 0.0), power=2.0, taper=0.0, band_mat=BONE,
              keep=None, side=(1.0, 0.0, 0.0), up=(0.0, 1.0, 0.0), name=""):
    """A loft along a 3D polyline; hw/top/bot/power/taper are scalars or one value per point."""
    P = np.asarray(points, dtype=np.float64)
    n = len(P)

    def per(v):
        if v is None:
            return [None] * n
        a = np.atleast_1d(np.asarray(v, dtype=np.float64))
        return list(a) if len(a) == n else [float(a[0])] * n

    hw, top, bot, power, taper = per(hw), per(top), per(bot), per(power), per(taper)
    rings = [Ring(P[i], hw[i], top[i], bot[i], power[i], taper[i]) for i in range(n)]
    return loft(rings, sides, caps=caps, band_mat=band_mat, keep=keep, side=side, up=up, name=name)


# ---------------------------------------------------------------- simple solids

def tube(p0, p1, r0, r1=None, sides=None, caps=(0.0, 0.0), mat=BONE, side=(1.0, 0.0, 0.0), name=""):
    """A capped prism or frustum from p0 to p1 (radius r0 -> r1)."""
    r1 = r0 if r1 is None else r1
    sides = sides or sides_for(max(r0, r1))
    return loft_path([p0, p1], [r0, r1], [r0, r1], sides=sides, caps=caps, band_mat=mat, side=side, name=name)


def lens(centre, axis, radius, half_len, sides=8, mat=DARK, side=None, name=""):
    """A bicone lens (eye socket, nasal opening): two rings at +-0.35 half_len with the full radius and poles
    at +-half_len along `axis`."""
    a = unit(axis)
    c = np.asarray(centre, dtype=np.float64)
    if side is None:
        side = (0.0, 1.0, 0.0) if abs(a[1]) < 0.9 else (1.0, 0.0, 0.0)
    p0, p1 = c - a * 0.35 * half_len, c + a * 0.35 * half_len
    ext = 0.65 * half_len
    return loft_path([p0, p1], radius, radius, sides=sides, caps=(ext, ext), band_mat=mat, side=side,
                     up=np.cross(a, unit(side)) if abs(a @ unit(side)) < 0.99 else (0, 1, 0), name=name)


def ellipsoid(centre, rx, ry, rz, sides=8, rings=3, mat=BONE, name=""):
    """An axis-aligned ellipsoid lofted along z: `rings` latitude rings plus poles."""
    c = np.asarray(centre, dtype=np.float64)
    rs = []
    for i in range(rings):
        lat = -0.5 * math.pi + math.pi * (i + 1) / (rings + 1)
        f = math.cos(lat)
        rs.append(Ring(c + np.array([0.0, 0.0, rz * math.sin(lat)]), rx * f, ry * f))
    if rings == 1:
        rs = [Ring(c - [0, 0, 1e-3], rx, ry), Ring(c + [0, 0, 1e-3], rx, ry)]
    ext0 = rz - abs(rs[0].c[2] - c[2])
    ext1 = rz - abs(rs[-1].c[2] - c[2])
    return loft(rs, sides, caps=(ext0, ext1), band_mat=mat,
                frames=(np.tile([0.0, 0.0, 1.0], (len(rs), 1)), np.tile([1.0, 0.0, 0.0], (len(rs), 1)),
                        np.tile([0.0, 1.0, 0.0], (len(rs), 1))), name=name)


def plate(centre, u, v, hu, hv, thick, sides=6, power=2.0, mat=BONE, name=""):
    """A flat polygon prism: a (superelliptic) polygon of half sizes hu x hv in the plane (u, v), extruded
    `thick` along u x v. Closed by flat caps."""
    u, v = unit(u), unit(v)
    n = unit(np.cross(u, v))
    c = np.asarray(centre, dtype=np.float64)
    rs = [Ring(c - n * thick / 2, hu, hv, power=power), Ring(c + n * thick / 2, hu, hv, power=power)]
    fr = (np.tile(n, (2, 1)), np.tile(u, (2, 1)), np.tile(v, (2, 1)))
    return loft(rs, sides, frames=fr, caps=(0.0, 0.0), band_mat=mat, name=name)


def box(centre, hx, hy, hz, mat=BONE, name=""):
    """An axis-aligned box (a 4-sided loft along z with square corners)."""
    c = np.asarray(centre, dtype=np.float64)
    rs = [Ring(c - [0, 0, hz], hx * math.sqrt(2), hy * math.sqrt(2)),
          Ring(c + [0, 0, hz], hx * math.sqrt(2), hy * math.sqrt(2))]
    fr = (np.tile([0.0, 0.0, 1.0], (2, 1)), np.tile([1.0, 0.0, 0.0], (2, 1)), np.tile([0.0, 1.0, 0.0], (2, 1)))
    return loft(rs, 4, frames=fr, caps=(0.0, 0.0), band_mat=mat, angle0=math.pi / 4, name=name)


def bone(p0, p1, r, knob0=None, knob1=None, sides=None, mat=BONE, side=(1.0, 0.0, 0.0), name=""):
    """A long bone from p0 to p1: knobbed ends (radius knob0/knob1, default 1.45 r) and a shaft of radius r,
    one closed loft."""
    p0 = np.asarray(p0, dtype=np.float64)
    p1 = np.asarray(p1, dtype=np.float64)
    k0 = knob0 if knob0 is not None else 1.45 * r
    k1 = knob1 if knob1 is not None else 1.45 * r
    L = float(np.linalg.norm(p1 - p0))
    a = unit(p1 - p0)
    sides = sides or sides_for(r)
    e0 = min(0.3 * L, 1.1 * k0)
    e1 = min(0.3 * L, 1.1 * k1)
    pts = [p0, p0 + a * e0, p1 - a * e1, p1]
    rad = [k0, r, r, k1]
    return loft_path(pts, rad, rad, sides=sides, caps=(0.45 * k0, 0.45 * k1), band_mat=mat, side=side, name=name)


def resample(points, n):
    """n + 1 points evenly spaced by arc length along a polyline."""
    P = np.asarray(points, dtype=np.float64)
    seg = np.linalg.norm(np.diff(P, axis=0), axis=1)
    s = np.concatenate([[0.0], np.cumsum(seg)])
    total = s[-1]
    out = []
    for i in range(n + 1):
        t = total * i / n
        j = int(np.searchsorted(s, t, side="right") - 1)
        j = min(max(j, 0), len(seg) - 1)
        f = 0.0 if seg[j] < 1e-12 else (t - s[j]) / seg[j]
        out.append(P[j] + (P[j + 1] - P[j]) * f)
    return np.array(out)


def path_length(points):
    P = np.asarray(points, dtype=np.float64)
    return float(np.linalg.norm(np.diff(P, axis=0), axis=1).sum())


def vertebra_chain(points, count, r0, r1=None, gap=0.008, sides=6, mat=BONE, flat=1.0, waist=0.0,
                   side=(1.0, 0.0, 0.0), up=(0.0, 1.0, 0.0), name=""):
    """`count` separate vertebrae along a polyline, radius r0 at the start to r1 at the end, `gap` between.
    flat < 1 flattens them vertically; waist > 0 adds a middle ring pinched by that fraction (a spool)."""
    r1 = r0 if r1 is None else r1
    P = resample(points, count)
    parts = []
    for i in range(count):
        a, b = P[i], P[i + 1]
        d = unit(b - a)
        L = float(np.linalg.norm(b - a))
        g = min(gap, 0.3 * L)
        a2, b2 = a + d * g / 2, b - d * g / 2
        r = max(MIN_R, r0 + (r1 - r0) * (i + 0.5) / count)
        if waist > 0:
            pts = [a2, (a2 + b2) / 2, b2]
            rad = [r, r * (1 - waist), r]
        else:
            pts = [a2, b2]
            rad = [r, r]
        top = [x * flat for x in rad]
        parts.append(loft_path(pts, rad, top, sides=sides, caps=(0.0, 0.0), band_mat=mat, side=side, up=up))
    return merge(parts, name)
