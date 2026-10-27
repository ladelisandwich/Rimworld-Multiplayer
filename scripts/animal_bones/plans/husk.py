"""Husk plan: dried husks and shelled skeletons (Multiplayer port, not shipped).

Animals without a skeleton worth drawing (insects, crabs, the lava snail, dryads) dry to a husk: the living
model's triangles copied exactly (same positions, same winding, so every pose IsoRim applies to the living
model fits), each kept with a colour of its own (per-vertex colours under `usemtl bone`, see objio):

  insects   Megascarab Spelopede Megaspider Locust Larva HiveQueen: pale grey-tan chitin (the game draws a dried
            insect with its own dried sprite tinted 0.8 grey). Every colour is mapped by its lightness against
            the body's main colour, so the light and dark markings keep their contrast; eyes stay dark. A light
            insect's chitin is lightened so the husk is clearly paler than the living one (chitin_for: the tan
            Megaspider). Legs and the Locust's wing are recoloured copies of the living leg and wing files.
  crabs     HermitCrab StoneCrab: the shell and body bleached pale (desaturated and lightened, contrast kept).
  snail     LavaSnail: only its shell and bands (each a closed shell on its own), slightly bleached; the soft
            foot and the eyes on its stalks are gone.
  dryads    the 8 Dryad_*: withered, a desaturated grey-brown wood keeping a trace of the caste's own colour;
            leaves, flowers and the crown browned like dead leaves, berries dropped, and nothing glows (the
            Medicinemaker's glow flowers become plain dried ones: no material of theirs survives).

Three shelled animals keep their shell and get bones from the core primitives (no ribcage under a shell):

  Tortoise (shell = its `acc` group), SeaTurtle (shell + plate), Armadillo (shell + bands): the shell copied
  and bleached about halfway to bone (desaturated, lightened up to the bones' own lightness, see SHELL_BLEACH);
  a skull with eye sockets and a jaw through the living head (quadruped.Skeleton.skull with
  turtle / armadillo proportions); neck vertebrae from the back of the skull into the shell's front, bent like
  the living neck; tail vertebrae from inside the shell's rear along the living tail (the Armadillo's armoured
  tail, a piece of its shell group, gives way to them like the others' soft tails); bone legs for the
  Tortoise (stout stumps with a blunt head and a flat foot, ShellBones.leg) and the Armadillo (paws, the
  quadruped's leg builder); the SeaTurtle's four flippers as a fan: an arm bone from inside the shell to the
  wrist and digit bones spreading to the flipper's edge.

What a copy has to fix to meet the file conventions (closed shells wound clockwise from outside):
  - open shells are closed with a flat fan over each hole (the eye boxes sunk into the Megascarab's and
    Spelopede's heads, 2 triangles each, and the open tops of their legs, 3 triangles), so those husks have a
    few more triangles than the living files;
  - shells wound the other way are turned round (the Megaspider's file is inside out; IsoRim's loader turns the
    living one round when it loads it, so the drawn result is the same).
The Megaspider's legs are legFromHip legs (hip at the origin, the leg reaching out and down), so the
validator skips its hip-at-the-top leg checks for them (validate.py).
"""

import numpy as np

from .. import prims as P
from ..prims import BONE
from . import quadruped as Q

INSECTS = {"Megascarab", "Spelopede", "Megaspider", "Locust", "Larva", "HiveQueen"}
CRABS = {"HermitCrab", "StoneCrab"}
SNAILS = {"LavaSnail"}
DRYADS = {"Dryad_Basic", "Dryad_Woodmaker", "Dryad_Berrymaker", "Dryad_Medicinemaker", "Dryad_Gaumaker",
          "Dryad_Carrier", "Dryad_Clawer", "Dryad_Barkskin"}
SHELLED = {"Tortoise", "SeaTurtle", "Armadillo"}

SPECIES = INSECTS | CRABS | SNAILS | DRYADS | SHELLED

# ---------------------------------------------------------------- colours

CHITIN = (0.730, 0.700, 0.632)      # an insect husk's main colour (the body's own colour maps to this)
CHITIN_RANGE = (0.50, 1.13)          # lightness factors kept (darkest marking, lightest horn)
CHITIN_GAMMA = 0.6                   # < 1 eases the living model's contrast a little
CHITIN_LIFT = 0.15                   # a husk's main colour at least this much lighter than the living one's,
CHITIN_MAX_LUM = 0.78                # but no lighter than this (the bones' own lightness)
EYE_DRY = (0.150, 0.135, 0.125)      # eyes and eye boxes of a husk (an_dark stays near an_dark)

WOOD = (0.530, 0.475, 0.400)         # a withered dryad's main colour (grey-brown dead wood)
WOOD_RANGE = (0.62, 1.22)
WOOD_GAMMA = 0.7
WOOD_HUE = 0.12                      # share of the caste's own hue kept in the dead wood
LEAF_DRY = (0.470, 0.375, 0.265)     # dead leaves, dried flowers and crowns
LEAF_RANGE = (0.80, 1.12)

DARK_LUM = 0.13                      # a colour this dark is an eye or a nostril
SHELL_TINT = (1.0, 0.99, 0.90)       # bleached shells: a dry tan, not the mauve a reddish brown fades to


def lum(c):
    c = np.asarray(c, dtype=np.float64)
    return c[..., 0] * 0.299 + c[..., 1] * 0.587 + c[..., 2] * 0.114


def is_eye(name, colour):
    n = name.lower()
    return n == "an_dark" or n.endswith("_eye") or float(lum(colour)) < DARK_LUM


def bleach(c, desat, lift, tint=(1.0, 1.0, 1.0)):
    """A dried, sun-bleached colour: desaturated toward its own grey by `desat`, lightened toward white by `lift`,
    then tinted."""
    c = np.asarray(c, dtype=np.float64)
    g = float(lum(c))
    c1 = c + (np.array([g, g, g]) - c) * desat
    c2 = c1 + (1.0 - c1) * lift
    return np.clip(c2 * np.asarray(tint), 0.0, 1.0)


def by_lightness(c, ref, target, rng, gamma):
    """`target` scaled by how light `c` is against the reference lightness `ref` (contrast eased by gamma and
    clamped to rng), so markings keep their relative light and dark."""
    s = (max(float(lum(c)), 1e-3) / max(ref, 1e-3)) ** gamma
    s = min(max(s, rng[0]), rng[1])
    return np.clip(np.asarray(target, dtype=np.float64) * s, 0.0, 1.0)


def main_lightness(mesh):
    """Lightness of the model's main colour: the material covering the most surface (eyes left out)."""
    a, b, c = mesh.tris[:, 0], mesh.tris[:, 1], mesh.tris[:, 2]
    area = 0.5 * np.linalg.norm(np.cross(b - a, c - a), axis=1)
    best, best_area = None, -1.0
    for g in mesh.groups():
        m = np.array([x == g for x in mesh.mat])
        col = mesh.color[m][0]
        if is_eye(g, col):
            continue
        s = float(area[m].sum())
        if s > best_area:
            best, best_area = col, s
    return float(lum(best)) if best is not None else 0.5


# ---------------------------------------------------------------- copying the living model

def _boundary_loops(part):
    """Closed loops of the directed edges that have no partner running the other way (the rims of holes)."""
    from collections import Counter
    e = Counter()
    for a, b, c in part.tris:
        for u, v in ((a, b), (b, c), (c, a)):
            e[(int(u), int(v))] += 1
    nxt = {}
    for (u, v), n in e.items():
        if n == 1 and e.get((v, u), 0) == 0:
            if u in nxt:
                raise ValueError("hole rims touch at vertex %d" % u)
            nxt[u] = v
    loops = []
    seen = set()
    for start in sorted(nxt):
        if start in seen:
            continue
        loop = [start]
        seen.add(start)
        v = nxt[start]
        while v != start:
            if v in seen or v not in nxt:
                raise ValueError("an open rim that isn't a loop")
            loop.append(v)
            seen.add(v)
            v = nxt[v]
        loops.append(loop)
    return loops


def close_holes(part):
    """Closes every hole of `part` with a flat fan from the rim's first vertex, wound to match its neighbours;
    each new triangle takes the colour of the triangle beside the rim."""
    loops = _boundary_loops(part)
    if not loops:
        return part
    edge_tri = {}
    for t, (a, b, c) in enumerate(part.tris):
        for u, v in ((a, b), (b, c), (c, a)):
            edge_tri[(int(u), int(v))] = t
    tris = [part.tris]
    mats = list(part.mat)
    cols = [part.col] if part.col is not None else None
    for loop in loops:
        # the rim runs v0 -> v1 -> ... ; the cap must run each rim edge backwards: (v0, v[i+1], v[i])
        k = len(loop)
        new = [(loop[0], loop[i + 1], loop[i]) for i in range(1, k - 1)]
        t0 = edge_tri[(loop[0], loop[1])]
        tris.append(np.array(new, dtype=np.int64).reshape(-1, 3))
        mats += [part.mat[t0]] * len(new)
        if cols is not None:
            cols.append(np.tile(part.col[t0], (len(new), 1)))
    return P.Part(part.verts, np.concatenate(tris), mats, None if cols is None else np.concatenate(cols), part.name)


def copy_mesh(mesh, colours, keep=None, name="husk"):
    """The living triangles (all, or those `keep` selects) as one Part with a colour per triangle: welded (shared
    vertices, so holes and shells can be found), holes closed, each shell wound clockwise from outside."""
    idx = np.arange(len(mesh.mat)) if keep is None else np.nonzero(np.asarray(keep))[0]
    tris = mesh.tris[idx]
    part = P.Part(tris.reshape(-1, 3), np.arange(len(idx) * 3).reshape(-1, 3), BONE,
                  np.asarray(colours, dtype=np.float64)[idx], name)
    part = P.weld(part, 4)
    if len(part) != len(idx):
        raise ValueError("%s: %d triangle(s) collapse when welded" % (name, len(idx) - len(part)))
    part = close_holes(part)
    return P.orient(part)


def chitin_for(ref):
    """The colour an insect's main colour (lightness ref) dries to: CHITIN, lightened when the living colour is
    already nearly as light, so the husk is at least CHITIN_LIFT lighter (up to CHITIN_MAX_LUM). The tan
    Megaspider (0.63) came out only 0.07 lighter and about as tan, so a dried one looked like a fresh corpse."""
    base = float(lum(CHITIN))
    want = max(base, min(ref + CHITIN_LIFT, CHITIN_MAX_LUM))
    return np.asarray(CHITIN, dtype=np.float64) * (want / base)


def colours_insect(mesh, ref):
    target = chitin_for(ref)
    out = np.zeros((len(mesh.mat), 3))
    for i, (m, c) in enumerate(zip(mesh.mat, mesh.color)):
        out[i] = EYE_DRY if is_eye(m, c) else by_lightness(c, ref, target, CHITIN_RANGE, CHITIN_GAMMA)
    return out


def colours_bleached(mesh, desat, lift, tint):
    out = np.zeros((len(mesh.mat), 3))
    for i, (m, c) in enumerate(zip(mesh.mat, mesh.color)):
        out[i] = EYE_DRY if is_eye(m, c) else bleach(c, desat, lift, tint)
    return out


LEAFY = ("leaf", "flower", "crown")
DROPPED = ("berry",)


def dryad_part(name):
    """'drop', 'leaf' (browned) or 'wood' for a dryad's material, by its last word (an_dryad_berrymaker_berry:
    the caste's name may hold the word too); a glow material is always a leaf, never kept glowing."""
    n = name.lower()
    last = n.rsplit("_", 1)[-1]
    if last in DROPPED:
        return "drop"
    if last in LEAFY or n.startswith("glow"):
        return "leaf"
    return "wood"


def colours_dryad(mesh, ref):
    out = np.zeros((len(mesh.mat), 3))
    for i, (m, c) in enumerate(zip(mesh.mat, mesh.color)):
        kind = dryad_part(m)
        if is_eye(m, c):
            out[i] = EYE_DRY
        elif kind == "leaf":
            out[i] = by_lightness(c, ref, LEAF_DRY, LEAF_RANGE, 0.5)
        else:
            w = by_lightness(c, ref, WOOD, WOOD_RANGE, WOOD_GAMMA)
            # a trace of the caste's own hue, at the dead wood's lightness
            own = np.asarray(c) * (float(lum(w)) / max(float(lum(c)), 1e-3))
            out[i] = np.clip(w + (own - w) * WOOD_HUE, 0.0, 1.0)
    return out


# ---------------------------------------------------------------- the build

def build(animal):
    name = animal.name
    if name in SHELLED:
        return build_shelled(animal)
    body = animal.body
    if name in INSECTS:
        ref = main_lightness(body)

        def colour(m):
            return colours_insect(m, ref)
        keep = None
    elif name in CRABS:
        def colour(m):
            return colours_bleached(m, 0.75, 0.45, (1.0, 0.97, 0.91))
        keep = None
    elif name in SNAILS:
        def colour(m):
            return colours_bleached(m, 0.5, 0.25, SHELL_TINT)
        keep = body.mask_of(lambda n: n.endswith("_shell") or n.endswith("_band"))
    elif name in DRYADS:
        ref = main_lightness(body)

        def colour(m):
            return colours_dryad(m, ref)
        keep = np.array([dryad_part(m) != "drop" for m in body.mat])
    else:
        raise ValueError("husk plan doesn't know %s" % name)
    out = {"body": copy_mesh(body, colour(body), keep, name="husk")}
    if animal.leg is not None:
        out["leg"] = copy_mesh(animal.leg, colour(animal.leg), name="husk_leg")
    if animal.wing is not None:
        out["wing"] = copy_mesh(animal.wing, colour(animal.wing), name="husk_wing")
    return out


# ---------------------------------------------------------------- shelled animals with bones

SHELL_GROUPS = {
    "Tortoise": ("_acc",),
    "SeaTurtle": ("_shell", "_plate"),
    "Armadillo": ("_shell", "_band"),
}

TURTLE_SKULL = [(0.0, 0.78, 0.82), (0.18, 1.0, 1.0), (0.5, 0.95, 0.92), (0.8, 0.80, 0.76), (1.0, 0.66, 0.60)]

SHELLED_P = {
    # stout stumps under a domed shell (ShellBones.leg); a blunt skull with big orbits
    "Tortoise": dict(skull=TURTLE_SKULL, jaw=0.36, orbit=0.44, fwd=0.2, neck=4, tail_max=3, leg="stump",
                     leg_r=0.60),
    # flippers instead of legs
    "SeaTurtle": dict(skull=TURTLE_SKULL, jaw=0.36, orbit=0.44, fwd=0.15, neck=5, tail_max=3),
    # a long narrow snout, digging paws, a long tail
    "Armadillo": dict(skull=[(0.0, 0.72, 0.80), (0.15, 1.0, 1.0), (0.42, 0.85, 0.85), (0.7, 0.60, 0.62),
                             (1.0, 0.46, 0.48)], jaw=0.30, orbit=0.36, fwd=0.1, neck=3, tail_max=8, leg="paw",
                      leg_r=0.34),
}

# Kept shells: desaturated and lightened about halfway to bone (the Tortoise's olive 0.44 0.40 0.24 to about
# 0.69 0.68 0.59), so a dried tortoise doesn't look like a living one with a few white pegs; the lift stops at
# SHELL_MAX_LUM, so an already pale shell (the Armadillo's) only loses its colour and never outshines the bones.
SHELL_BLEACH = (0.60, 0.48, SHELL_TINT)
SHELL_MAX_LUM = 0.78                 # the bones' own lightness (bone Kd .800 .785 .745)


def colours_shell(mesh):
    desat, lift, tint = SHELL_BLEACH
    out = np.zeros((len(mesh.mat), 3))
    for i, (m, c) in enumerate(zip(mesh.mat, mesh.color)):
        if is_eye(m, c):
            out[i] = EYE_DRY
            continue
        g = float(lum(c))
        out[i] = bleach(c, desat, min(lift, max(0.0, (SHELL_MAX_LUM - g) / max(1.0 - g, 1e-6))), tint)
    return out


def build_shelled(animal):
    name = animal.name
    p = dict(Q.BASE)
    p.update(SHELLED_P[name])
    p["family"] = "shelled"
    animal.overrides = p
    an = animal.an
    body = animal.body
    ends = SHELL_GROUPS[name]
    in_shell = body.mask_of(lambda n: any(n.endswith(e) for e in ends))
    # the shell's pieces, but not a tail that happens to be in a shell group (the Armadillo's armoured tail:
    # it gets vertebrae like the others)
    _ = an.tail          # marks the tail pieces (comp.kind "tail")
    tail_idx = set()
    for c in an.comps:
        if c.kind == "tail":
            tail_idx.update(int(i) for i in c.idx)
    keep = in_shell & ~np.isin(np.arange(len(body.mat)), sorted(tail_idx))
    shell = copy_mesh(body, colours_shell(body), keep, name="shell")
    sk = ShellBones(animal, p, keep)
    parts = [shell]
    parts += sk.skull()
    parts.append(sk.shell_neck())
    t = sk.shell_tail()
    if t is not None:
        parts.append(t)
    if not animal.d["legs"]:
        parts += sk.flippers()
    out = {"body": [q for q in parts if q is not None and len(q)]}
    if animal.leg is not None:
        out["leg"] = sk.leg()
    return out


class ShellBones(Q.Skeleton):
    """The quadruped skeleton's skull and leg builders for an animal whose trunk is a shell; its own neck, tail
    and flippers. Works without legs (the SeaTurtle)."""

    def __init__(self, animal, p, shell_mask):
        self.a = animal
        self.p = p
        self.an = animal.an
        self.tr = self.an.trunk
        self.hd = self.an.head
        self.shell_mask = np.asarray(shell_mask)
        d = animal.d
        self.LL = float(d["legLength"])
        self.W = float(self.tr.half_width)
        self.size = animal.size
        tr = self.tr
        hips = animal.hips_file
        if hips:
            self.zfh = min(h[2] for h in hips)
            self.zrh = max(h[2] for h in hips)
            self.Hf = float(np.mean([abs(h[0]) for h in hips if abs(h[2] - self.zfh) < 1e-6]))
            self.Hr = float(np.mean([abs(h[0]) for h in hips if abs(h[2] - self.zrh) < 1e-6]))
        else:
            self.zfh = tr.z_front + 0.25 * tr.length
            self.zrh = tr.z_rear - 0.25 * tr.length
            self.Hf = self.Hr = 0.6 * self.W
        h = tr.top - tr.bottom
        self.rs = max(P.MIN_R + 0.002, min(p["spine_r"] * self.W, 0.11 * h))
        sv = animal.body.tris[self.shell_mask].reshape(-1, 3)
        self.shell_lo, self.shell_hi = sv.min(axis=0), sv.max(axis=0)

    def soft_comps(self):
        """Living pieces that are neither shell, head, trunk, tail nor eyes (neck, flippers, ears)."""
        out = []
        for c in self.an.comps:
            if c.kind:
                continue
            if self.shell_mask[c.idx].any():
                continue
            out.append(c)
        return out

    def leg(self):
        """The quadruped's leg, but for "stump" (the Tortoise): one stout bone the way the living stubby leg
        goes, a blunt head under the shell (a low dome, not the quadruped's hip-knob cone: on so short a leg
        that cone showed as a pointed white peg), a waist, a knob low down, and a broad flat foot reaching a
        little forward. The runtime splays it outward by legSplay like the living leg."""
        if self.p["leg"] != "stump":
            return Q.Skeleton.leg(self)
        li = self.a.leg_info
        LL = self.LL
        R = max(P.MIN_R + 0.006, self.p["leg_r"] * (li.hip_r if li else 0.05))
        foot = max(1.1 * R, 0.85 * (li.foot_hw if li else R))
        toe = li.toe_z if li else -0.04
        down = ((0.0, -1.0, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, -1.0))   # T, U, V of a horizontal ring
        rings = [P.Ring((0.0, LL + 0.012, 0.0), R, R, R),
                 P.Ring((0.0, LL - 0.014, 0.0), R, R, R),
                 P.Ring((0.0, 0.58 * LL, 0.0), 0.74 * R, 0.74 * R, 0.74 * R),
                 P.Ring((0.0, 0.26 * LL, 0.08 * toe), 0.95 * R, 0.95 * R, 0.95 * R),
                 P.Ring((0.0, 0.0, 0.25 * toe), foot, 1.15 * foot, 0.9 * foot)]
        T, U, V = (np.array([f] * len(rings), dtype=np.float64) for f in down)
        return [P.loft(rings, 8, frames=(T, U, V), caps=(0.006, 0.0), name="leg")]

    def shell_neck(self):
        """Vertebrae from the back of the skull into the shell's front, bent through the living neck."""
        hd, tr = self.hd, self.tr
        rb = self.skull_rings[0]
        start = np.array([0.0, rb.c[1] - 0.15 * rb.top, rb.c[2] - 0.05 * rb.top])
        z_end = max(self.shell_lo[2], tr.z_front) + 0.12 * tr.length
        necks = [c for c in self.soft_comps() if abs(c.centre[0]) < 0.03
                 and hd.z_back - 0.05 < c.centre[2] < tr.z_front + 0.2 * tr.length]
        if necks:
            pts = np.concatenate([c.tris.reshape(-1, 3) for c in necks])
            mid = pts.mean(axis=0)
            back = pts[pts[:, 2] > pts[:, 2].min() + 0.7 * np.ptp(pts[:, 2])]
            y_end = float(back[:, 1].mean())
        else:
            mid = None
            y_end = float(start[1])
        end = np.array([0.0, y_end, z_end])
        if mid is not None:
            mid = mid.copy()
            mid[0] = 0.0
            straight = (start + end) / 2
            ctrl = straight + 0.6 * (mid - straight)
        else:
            ctrl = (start + end) / 2
        path = [start * (1 - t) ** 2 + 2 * ctrl * (1 - t) * t + end * t ** 2 for t in np.linspace(0, 1, 9)]
        L = P.path_length(path)
        rn = min(max(0.45 * rb.top, P.MIN_R + 0.003), 0.035)
        n = min(int(self.p["neck"]), {"small": 2, "medium": 4, "large": 6}[self.size])
        n = max(2, min(n, int(L / (1.3 * rn + 0.008))))
        gap = min(0.010, 0.15 * L / n)
        return P.vertebra_chain(path, n, rn, rn * 1.05, gap=gap, sides=6, name="neck")

    def shell_tail(self):
        """Vertebrae from inside the shell's rear along the living tail."""
        tl = self.an.tail
        if tl is None or self.p["tail_max"] <= 0:
            return None
        path = np.array(tl.path, dtype=np.float64)
        z_root = min(float(path[0][2]), self.shell_hi[2]) - 0.05
        root = np.array([0.0, float(path[0][1]), z_root])
        path = np.vstack([root[None, :], path])
        r0 = min(max(0.8 * tl.radius + 0.008, P.MIN_R + 0.004), 0.03)
        r1 = max(P.MIN_R, 0.65 * r0)
        path[:, 1] = np.maximum(path[:, 1], r0 + 0.004)
        L = P.path_length(path)
        n = int(min(self.p["tail_max"], max(2, round(L / (3.0 * r0 + 0.012)))))
        return P.vertebra_chain(path, n, r0, r1, gap=0.008, sides=6, name="tail")

    def flippers(self):
        """Each flipper (a flat living piece reaching out from under the shell): an arm bone from inside the
        shell to the wrist, then a fan of digit bones to the flipper's far edge (4 in a long flipper, 3 in a
        short one)."""
        parts = []
        flips = [c for c in self.soft_comps() if abs(c.centre[0]) > 0.08 and c.size[1] < 0.5 * max(c.size[0], c.size[2])]
        for c in flips:
            pts = np.unique(np.round(c.tris.reshape(-1, 3), 4), axis=0)
            ctr = pts.mean(axis=0)
            xz = pts[:, [0, 2]] - ctr[[0, 2]]
            w, v = np.linalg.eigh(xz.T @ xz)
            ax = v[:, int(np.argmax(w))]
            axis = np.array([ax[0], 0.0, ax[1]])
            if axis[0] * np.sign(ctr[0]) < 0:      # root (near the body) to tip (away from it)
                axis = -axis
            perp = np.array([-axis[2], 0.0, axis[0]])
            t = (pts - ctr) @ axis
            s = (pts - ctr) @ perp
            span = float(t.max() - t.min())
            root = pts[t < t.min() + 0.15 * span].mean(axis=0)
            tip = pts[t > t.max() - 0.15 * span].mean(axis=0)
            y = float(ctr[1])
            root[1] = tip[1] = y
            half_th = 0.5 * float(np.ptp(pts[:, 1]))
            far = t > t.min() + 0.5 * span
            hw = float(np.max(np.abs(s[far]))) if far.any() else 0.3 * span
            long_ = span > 0.2
            r_arm = max(P.MIN_R + 0.002, min(0.75 * half_th, 0.03))
            r_dig = max(P.MIN_R, 0.8 * r_arm)
            shoulder = root - axis * 0.05 + np.array([0.0, 0.01, 0.0])
            wrist = root + (tip - root) * (0.38 if long_ else 0.32)
            parts.append(P.bone(shoulder, wrist, r_arm, 1.25 * r_arm, 1.35 * r_arm, sides=6, name="flipper"))
            k = 4 if long_ else 3
            for j in range(k):
                f = -1.0 + 2.0 * j / (k - 1)
                reach = 0.93 - 0.14 * abs(f)
                end = root + (tip - root) * reach + perp * (f * 0.62 * hw)
                a = wrist + (end - wrist) * 0.08
                parts.append(P.tube(a, end, r_dig, max(P.MIN_R, 0.85 * r_dig), sides=6,
                                    caps=(0.6 * r_dig, 0.9 * r_dig), name="flipper"))
        return parts
