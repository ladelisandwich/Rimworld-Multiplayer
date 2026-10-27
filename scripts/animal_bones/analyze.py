"""What the skeleton generator learns from a living animal model (Multiplayer port, not shipped).

All positions are FILE axes (x = width, y = up, the animal faces file -z). The living OBJs are built from
separate closed lofts (trunk, neck, head, muzzle, ears, eyes, tail...), so most of the analysis works on
connected components ("comps": triangles welded by position), classified by their material name and place:

  eyes   symmetric pairs of `an_dark` comps, the highest pair (nostrils and mouths sit lower or centred)
  head   the comp whose box holds the point between the eyes (the one centred nearest it), plus comps
         centred on x = 0 that overlap it (muzzle, chin, nose); ears (paired, off-centre) and horns are left
         out, so does the Elephant's trunk (it hangs below and barely overlaps the head's box)
  trunk  comps that span at least half the distance between the front and rear hips (soft extras like
         manes, skirts, sacs and fur coats are left out), sliced across z for top / bottom / half-width;
         when no piece is left (a one-piece body taken as the head), the largest piece
  tail   the `_tail` group, else thin centred comps reaching behind the trunk
  neck   comps between the head and the trunk, for the neck path's bend
  extras horn / antler / tusk / tooth groups, kept as they are (they are closed shells already)
  legs   the living leg OBJ: ring levels, centre and radius per level, hoof top

Everything is plain numpy and deterministic (no sets of strings are iterated).
"""

import numpy as np

SOFT_WORDS = ("acc", "skirt", "mane", "ruff", "quill", "coat", "sac", "patch", "stripe", "ridge")
HEAD_WORDS = ("_head", "_muzzle", "_face", "_mask", "_nose", "_chin", "_beak", "_throat", "_white", "_black",
              "_tan", "_pink")
EXTRA_WORDS = ("horn", "antler", "tusk", "tooth", "casque")


def is_dark(name):
    return name.lower() == "an_dark"


def is_extra(name):
    n = name.lower()
    return any(w in n for w in EXTRA_WORDS)


def is_tail_name(name):
    return name.lower().endswith("_tail")


def is_soft(name):
    n = name.lower()
    return any(n.endswith("_" + w) or ("_" + w + "_") in n for w in SOFT_WORDS)


# ---------------------------------------------------------------- components

class Comp:
    """A connected piece of the living body (triangles welded by position)."""

    def __init__(self, mesh, idx):
        self.idx = np.asarray(idx, dtype=np.int64)
        self.tris = mesh.tris[self.idx]
        p = self.tris.reshape(-1, 3)
        self.lo = p.min(axis=0)
        self.hi = p.max(axis=0)
        self.size = self.hi - self.lo
        self.centre = (self.lo + self.hi) / 2.0
        names = []
        for i in self.idx:
            m = mesh.mat[i]
            if m not in names:
                names.append(m)
        self.mats = names
        self.n = len(self.idx)
        self.box_volume = float(np.prod(np.maximum(self.size, 1e-3)))
        a, b, c = self.tris[:, 0], self.tris[:, 1], self.tris[:, 2]
        self.volume = abs(float(np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6.0))
        self.kind = ""

    @property
    def main_mat(self):
        return self.mats[0]

    def has(self, pred):
        return any(pred(m) for m in self.mats)

    def contains(self, p, margin=0.0):
        p = np.asarray(p)
        return bool(np.all(p >= self.lo - margin) and np.all(p <= self.hi + margin))

    def overlap_fraction(self, other):
        """Overlap of the two boxes as a fraction of THIS box's volume."""
        lo = np.maximum(self.lo, other.lo)
        hi = np.minimum(self.hi, other.hi)
        if np.any(hi <= lo):
            return 0.0
        return float(np.prod(hi - lo) / np.prod(np.maximum(self.size, 1e-4)))

    def __repr__(self):
        return "Comp(%d %s x%.2f:%.2f y%.2f:%.2f z%.2f:%.2f %s)" % (
            self.n, self.mats[0], self.lo[0], self.hi[0], self.lo[1], self.hi[1], self.lo[2], self.hi[2], self.kind)


def components(mesh, mask=None):
    idx = np.arange(len(mesh.mat)) if mask is None else np.nonzero(mask)[0]
    if len(idx) == 0:
        return []
    pts = np.round(mesh.tris[idx].reshape(-1, 3), 4) + 0.0
    _, inv = np.unique(pts, axis=0, return_inverse=True)
    inv = inv.reshape(-1, 3)
    parent = list(range(len(idx)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    first = {}
    for t in range(len(idx)):
        for k in range(3):
            v = int(inv[t, k])
            if v in first:
                ra, rb = find(t), find(first[v])
                if ra != rb:
                    parent[ra] = rb
            else:
                first[v] = t
    groups = {}
    for t in range(len(idx)):
        groups.setdefault(find(t), []).append(int(idx[t]))
    out = [Comp(mesh, g) for g in groups.values()]
    out.sort(key=lambda c: (-c.n, float(c.lo[2]), float(c.lo[1]), float(c.lo[0])))
    return out


# ---------------------------------------------------------------- slices

def slice_bounds(tris, z):
    """(ymin, ymax, xmin, xmax) of the cross-section of a triangle soup with the plane file z = const, or None."""
    if len(tris) == 0:
        return None
    d = tris[:, :, 2] - z
    pts = []
    for (i, j) in ((0, 1), (1, 2), (2, 0)):
        di, dj = d[:, i], d[:, j]
        m = (di * dj < 0)
        if np.any(m):
            t = di[m] / (di[m] - dj[m])
            a, b = tris[m, i], tris[m, j]
            pts.append(a + (b - a) * t[:, None])
        on = np.abs(di) < 1e-9
        if np.any(on):
            pts.append(tris[on, i])
    if not pts:
        return None
    q = np.concatenate(pts)
    return float(q[:, 1].min()), float(q[:, 1].max()), float(q[:, 0].min()), float(q[:, 0].max())


class Slices:
    """Cross-sections along z: z, ymin, ymax, half-width (max |x|), centre y."""

    def __init__(self, tris, z0, z1, n):
        zs = np.linspace(z0, z1, n)
        rows = []
        for z in zs:
            b = slice_bounds(tris, z)
            if b is None:
                continue
            rows.append((z, b[0], b[1], max(abs(b[2]), abs(b[3]))))
        a = np.array(rows, dtype=np.float64).reshape(-1, 4)
        self.z, self.ymin, self.ymax, self.hw = a[:, 0], a[:, 1], a[:, 2], a[:, 3]

    def __len__(self):
        return len(self.z)

    def at(self, z):
        """Interpolated (ymin, ymax, hw) at z (clamped to the sliced range)."""
        if len(self.z) == 0:
            return None
        return (float(np.interp(z, self.z, self.ymin)), float(np.interp(z, self.z, self.ymax)),
                float(np.interp(z, self.z, self.hw)))


# ---------------------------------------------------------------- eyes

def find_eyes(comps_dark):
    """The highest symmetric pair of dark comps: (left, right) centres (x < 0 first), or None."""
    pairs = []
    cs = [c for c in comps_dark]
    for i in range(len(cs)):
        for j in range(i + 1, len(cs)):
            a, b = cs[i].centre, cs[j].centre
            if abs(a[0] + b[0]) < 0.02 and abs(a[1] - b[1]) < 0.02 and abs(a[2] - b[2]) < 0.03 \
                    and abs(a[0]) > 0.012:
                l, r = (a, b) if a[0] < b[0] else (b, a)
                pairs.append((float((a[1] + b[1]) / 2), l, r))
    if not pairs:
        return None
    pairs.sort(key=lambda t: -t[0])
    return np.array(pairs[0][1]), np.array(pairs[0][2])


# ---------------------------------------------------------------- the analysis

class Analysis:
    """Lazily computed facts about one living animal; see the module docstring."""

    def __init__(self, animal):
        self.a = animal
        m = animal.body
        self.mesh = m
        self.comps = components(m)
        for c in self.comps:
            if all(is_dark(x) for x in c.mats):
                c.kind = "dark"
            elif c.has(is_extra):
                c.kind = "extra"
            elif c.has(is_tail_name):
                c.kind = "tail"
        self.hips = animal.hips_file
        self._head = None
        self._trunk = None
        self._tail = None
        self._eyes = None

    # ---------------- hips
    @property
    def front_hip_z(self):
        return min(h[2] for h in self.hips) if self.hips else None

    @property
    def rear_hip_z(self):
        return max(h[2] for h in self.hips) if self.hips else None

    def hip_x(self, front):
        hs = [h for h in self.hips if (h[2] < 0) == front] or self.hips
        return float(np.mean([abs(h[0]) for h in hs])) if hs else 0.0

    # ---------------- eyes
    @property
    def eyes(self):
        if self._eyes is None:
            ov = self.a.override("eyes")
            if ov is not None:
                l = np.array(ov, dtype=np.float64)
                self._eyes = (np.array([-abs(l[0]), l[1], l[2]]), np.array([abs(l[0]), l[1], l[2]]))
            else:
                e = find_eyes([c for c in self.comps if c.kind == "dark"])
                if e is None:
                    e = False
                self._eyes = e
        return self._eyes if self._eyes is not False else None

    # ---------------- head
    @property
    def head(self):
        if self._head is None:
            self._head = self._find_head()
        return self._head

    def _find_head(self):
        eyes = self.eyes
        cands = [c for c in self.comps if c.kind == ""]
        box = self.a.override("head_box")
        if box is not None:
            lo, hi = np.array(box[0], dtype=np.float64), np.array(box[1], dtype=np.float64)
            parts = [c for c in cands if np.all(c.centre >= lo) and np.all(c.centre <= hi)]
            parts.sort(key=lambda c: -c.box_volume)
            for c in parts:
                c.kind = "head"
            return HeadInfo(self, parts, eyes, (lo, hi))
        H = None
        if eyes is not None:
            E = (eyes[0] + eyes[1]) / 2.0
            E[0] = 0.0
            inside = [c for c in cands if c.contains(E, 0.015) and c.n >= 8]

            def off(c):
                return float(np.linalg.norm((c.centre - E)[1:]) / max(1e-3, np.linalg.norm(c.size[1:])))

            if inside:
                # the biggest piece that has the eyes well inside it (a neck reaching up to the eyes has
                # them at its edge; a thin face band has them in its middle but is small)
                good = [c for c in inside if off(c) < 0.35]
                if good:
                    good.sort(key=lambda c: (-c.box_volume, off(c)))
                    H = good[0]
                else:
                    inside.sort(key=lambda c: (off(c), c.n))
                    H = inside[0]
        if H is None:
            # the frontmost sizeable comp
            fr = [c for c in cands if c.n >= 8]
            fr.sort(key=lambda c: float(c.lo[2]))
            H = fr[0] if fr else None
        if H is None:
            return None
        H.kind = "head"
        parts = [H]
        hw = max(H.size[0] / 2, 1e-3)
        for c in cands:
            if c is H or c.kind:
                continue
            if abs(c.centre[0]) > 0.25 * hw:
                continue
            if c.box_volume > 1.5 * H.box_volume:
                continue
            if c.overlap_fraction(H) < 0.3:
                continue
            # behind the head's middle and taller than it, or centred behind it: a neck or the back
            if c.centre[2] > H.centre[2] + 0.25 * H.size[2] and c.size[1] > 0.9 * H.size[1]:
                continue
            if c.centre[2] > H.hi[2]:
                continue
            c.kind = "head"
            parts.append(c)
        return HeadInfo(self, parts, eyes)

    # ---------------- trunk
    @property
    def trunk(self):
        if self._trunk is None:
            self._trunk = self._find_trunk()
        return self._trunk

    def _find_trunk(self):
        _ = self.head
        if self.hips:
            zf, zr = self.front_hip_z, self.rear_hip_z
        else:
            b = self.mesh.bounds()
            zf, zr = b[0][2] + 0.3 * (b[1][2] - b[0][2]), b[1][2] - 0.3 * (b[1][2] - b[0][2])
        span = max(zr - zf, 0.05)
        parts = []
        for c in self.comps:
            if c.kind:
                continue
            if c.has(is_soft) and not self.a.override("trunk_soft", False):
                continue
            ov = min(c.hi[2], zr) - max(c.lo[2], zf)
            if ov >= 0.5 * span:
                parts.append(c)
        if not parts:
            parts = [c for c in self.comps if not c.kind][:1]
        if not parts:
            # every piece already has a kind (a one-piece body, e.g. the Cobra's, is all head): the largest
            # piece (most triangles, eyes only when nothing else), keeping the kind it has
            if not self.comps:
                raise ValueError("%s: the living body has no triangles" % self.a.name)
            parts = ([c for c in self.comps if c.kind != "dark"] or self.comps)[:1]
        for c in parts:
            if not c.kind:
                c.kind = "trunk"
        return TrunkInfo(self, parts)

    # ---------------- tail
    @property
    def tail(self):
        if self._tail is None:
            self._tail = self._find_tail()
        return self._tail

    def _find_tail(self):
        tr = self.trunk
        _ = self.head
        ov = self.a.override("tail")
        if ov is not None:
            if ov is False:
                return None
            return TailInfo(self, [], np.array(ov, dtype=np.float64))
        parts = [c for c in self.comps if c.kind == "tail"]
        if not parts:
            W = tr.half_width
            for c in self.comps:
                if c.kind:
                    continue
                soft = c.has(is_soft)
                if soft and not all(m.lower().endswith("_acc") or not is_soft(m) for m in c.mats):
                    continue  # quills, manes, coats, ridges...: never a tail (an "acc" piece can be one)
                thin = c.size[0] < (0.6 if all(is_soft(m) for m in c.mats) else 1.8) * W
                centred = abs(c.centre[0]) < 0.04
                behind = c.hi[2] > tr.z_rear + 0.03 and c.lo[2] > tr.z_front + 0.6 * tr.length
                if thin and centred and behind:
                    c.kind = "tail"
                    parts.append(c)
        if not parts:
            return None
        return TailInfo(self, parts, None)

    # ---------------- neck comps
    def neck_comps(self):
        hd, tr = self.head, self.trunk
        out = []
        if hd is None:
            return out
        for c in self.comps:
            if c.kind:
                continue
            if c.centre[2] > hd.z_back - 0.05 and c.centre[2] < tr.z_front + 0.4 * tr.length and abs(c.centre[0]) < 0.05:
                out.append(c)
        return out

    def extras(self):
        return [c for c in self.comps if c.kind == "extra"]


class HeadInfo:
    def __init__(self, an, parts, eyes, box=None):
        self.parts = parts
        tris = np.concatenate([c.tris for c in parts])
        self.tris = tris
        lo = np.min([c.lo for c in parts], axis=0)
        hi = np.max([c.hi for c in parts], axis=0)
        if box is not None:
            lo = np.maximum(lo, box[0])
            hi = np.minimum(hi, box[1])
        self.lo, self.hi = lo, hi
        self.z_front, self.z_back = float(lo[2]), float(hi[2])
        self.length = self.z_back - self.z_front
        self.slices = Slices(tris, self.z_front + 0.004, self.z_back - 0.004, 24)
        self.eyes = eyes
        if eyes is not None:
            self.eye_y = float((eyes[0][1] + eyes[1][1]) / 2)
            self.eye_z = float((eyes[0][2] + eyes[1][2]) / 2)
            self.eye_x = float(abs(eyes[1][0] - eyes[0][0]) / 2)
        else:
            self.eye_z = self.z_front + 0.4 * self.length
            s = self.slices.at(self.eye_z)
            self.eye_y = s[1] - 0.3 * (s[1] - s[0])
            self.eye_x = 0.6 * s[2]
        self.main = parts[0]


class TrunkInfo:
    def __init__(self, an, parts):
        self.parts = parts
        tris = np.concatenate([c.tris for c in parts])
        self.tris = tris
        lo = np.min([c.lo for c in parts], axis=0)
        hi = np.max([c.hi for c in parts], axis=0)
        self.lo, self.hi = lo, hi
        self.z_front, self.z_rear = float(lo[2]), float(hi[2])
        self.length = self.z_rear - self.z_front
        self.slices = Slices(tris, self.z_front + 0.003, self.z_rear - 0.003, 40)
        self.half_width = float(np.max(self.slices.hw)) if len(self.slices) else float(an.a.d["bodyHalfWidth"])
        self.top = float(np.max(self.slices.ymax)) if len(self.slices) else float(hi[1])
        self.bottom = float(np.min(self.slices.ymin)) if len(self.slices) else float(lo[1])

    def at(self, z):
        return self.slices.at(z)


class TailInfo:
    """The tail's centreline (file axes, root first) and a typical radius.

    The path follows the tail's surface from the root: geodesic distances (Dijkstra over the welded mesh
    edges, separate pieces such as a tuft linked at their nearest vertices) are binned, and each bin's
    vertex centroid is a path point, so curled and hanging tails keep their shape."""

    def __init__(self, an, parts, path=None):
        self.parts = parts
        if path is not None:
            self.path = path
            self.radius = 0.02
            return
        from scipy.sparse import coo_matrix
        from scipy.sparse.csgraph import connected_components, dijkstra
        tr = an.trunk
        # only the pieces behind the trunk's middle (a tail group may also hold a crest further forward)
        parts = [c for c in parts if c.centre[2] > tr.z_front + 0.5 * tr.length] or parts
        self.parts = parts
        tris = np.concatenate([c.tris for c in parts])
        key = np.round(tris.reshape(-1, 3), 4) + 0.0
        pts, inv = np.unique(key, axis=0, return_inverse=True)
        inv = inv.reshape(-1, 3)
        n = len(pts)
        a = np.concatenate([inv[:, 0], inv[:, 1], inv[:, 2]])
        b = np.concatenate([inv[:, 1], inv[:, 2], inv[:, 0]])
        # link separate pieces (a tuft, a tip) to the rest at their nearest vertices
        g = coo_matrix((np.ones(len(a)), (a, b)), shape=(n, n))
        ncomp, lab = connected_components(g, directed=False)
        extra_a, extra_b = [], []
        if ncomp > 1:
            linked = {int(lab[0])}
            while len(linked) < ncomp:
                inl = np.nonzero(np.isin(lab, sorted(linked)))[0]
                out = np.nonzero(~np.isin(lab, sorted(linked)))[0]
                d = np.linalg.norm(pts[inl][:, None, :] - pts[out][None, :, :], axis=2)
                i, j = np.unravel_index(int(np.argmin(d)), d.shape)
                extra_a.append(int(inl[i]))
                extra_b.append(int(out[j]))
                linked.add(int(lab[out[j]]))
        a = np.concatenate([a, np.array(extra_a, dtype=a.dtype)])
        b = np.concatenate([b, np.array(extra_b, dtype=b.dtype)])
        w = np.linalg.norm(pts[a] - pts[b], axis=1) + 1e-9
        G = coo_matrix((np.concatenate([w, w]), (np.concatenate([a, b]), np.concatenate([b, a]))),
                       shape=(n, n)).tocsr()
        # root: the tail vertex nearest the top of the trunk's rear end (tails leave the rump high, at the
        # sacrum)
        s = tr.at(tr.z_rear - 0.02)
        root_guess = np.array([0.0, s[1] - 0.25 * (s[1] - s[0]) if s else tr.top, tr.z_rear])
        root = int(np.argmin(np.linalg.norm(pts - root_guess, axis=1)))
        dist = dijkstra(G, directed=False, indices=root)
        dist[~np.isfinite(dist)] = 0.0
        dmax = float(dist.max())
        # rough radius: half the mean width of the tail across x
        rads = []
        nb = 8
        cents = []
        for i in range(nb):
            lo = dmax * i / nb
            hi = dmax * (i + 1) / nb
            m = (dist >= lo) & (dist <= hi + 1e-9)
            if m.sum() == 0:
                continue
            q = pts[m]
            cents.append(q.mean(axis=0))
            rads.append(0.5 * float(np.median(np.linalg.norm(q - q.mean(axis=0), axis=1))) + 0.004)
        cents = np.array(cents)
        r0 = pts[root].copy()
        tip = pts[int(np.argmax(dist))].copy()
        path = np.vstack([r0[None, :], cents, tip[None, :]])
        # light smoothing, ends kept
        sm = path.copy()
        for i in range(1, len(path) - 1):
            sm[i] = 0.25 * path[i - 1] + 0.5 * path[i] + 0.25 * path[i + 1]
        sm[:, 0] = 0.0
        # drop points closer than a third of a bin to the previous one
        keep = [sm[0]]
        for q in sm[1:]:
            if np.linalg.norm(q - keep[-1]) > dmax / nb / 3:
                keep.append(q)
        if len(keep) < 2:
            keep.append(sm[-1])
        self.path = np.array(keep)
        self.radius = float(np.median(rads)) if rads else 0.02


# ---------------------------------------------------------------- legs

class LegInfo:
    """The living leg: ring levels (y), their centres (x, z) and half sizes, the hoof top (or None), the
    foot's forward reach (most negative z at the ground) and the hip radius."""

    def __init__(self, mesh, leg_length):
        p = mesh.points()
        ys = np.unique(np.round(p[:, 1], 3))
        levels = []
        for y in ys:
            q = p[np.abs(p[:, 1] - y) < 6e-4]
            q = np.unique(np.round(q, 4), axis=0)
            if len(q) < 3:
                continue
            c = q.mean(axis=0)
            levels.append((float(y), float(c[0]), float(c[2]),
                           float((q[:, 0].max() - q[:, 0].min()) / 2), float((q[:, 2].max() - q[:, 2].min()) / 2)))
        self.levels = levels
        self.length = float(leg_length)
        top = [l for l in levels if l[0] > 0.8 * leg_length]
        self.hip_r = float(np.mean([min(l[3], l[4]) for l in top])) if top else 0.05
        mids = [l for l in levels if 0.2 * leg_length < l[0] < 0.8 * leg_length]
        self.shin_r = float(np.mean([min(l[3], l[4]) for l in mids])) if mids else 0.8 * self.hip_r
        hoof = mesh.mask_of(lambda n: "hoof" in n)
        self.hoof_top = float(mesh.tris[hoof][:, :, 1].max()) if hoof.any() else None
        low = p[p[:, 1] < 0.02 + 0.05 * leg_length]
        self.toe_z = float(low[:, 2].min()) if len(low) else 0.0
        self.heel_z = float(low[:, 2].max()) if len(low) else 0.0
        self.foot_hw = float(np.abs(low[:, 0]).max()) if len(low) else self.hip_r
        claw = mesh.mask_of(lambda n: "claw" in n)
        self.claws = claw.any()


def leg_info(animal):
    if animal.leg is None:
        return None
    return LegInfo(animal.leg, animal.d["legLength"])
