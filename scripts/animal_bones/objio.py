"""OBJ/MTL reading and writing for the animal skeleton generator (Multiplayer port, not shipped).

Reading follows IsoRim's ObjLoader.Load (IsoRim/Source/Models.cs): mtllib (several, relative to the OBJ),
usemtl, `v` with 3 or 7 numbers (the per-vertex colour wins over Kd), `f` corners as p, p/t or p/t/n split into
a fan from corner 0, negative indices, degenerate triangles (|cross| < 1e-6) dropped, material names matched
ignoring case, an unknown material grey 0.8. Unlike the loader, positions stay in FILE axes (no z flip): x =
width, y = up, the animal faces file -z.

Writing produces the skeleton files the spec asks for: the header, `mtllib isorim.mtl` and `mtllib
skeleton.mtl`, one block of shared vertices per part (`v x y z` with 4 decimals, or `v x y z r g b` when the
part's triangle carries a colour of its own), and `usemtl` runs of triangle faces.
"""

import os
from pathlib import Path

import numpy as np

DEFAULT_KD = (0.8, 0.8, 0.8)


class Mesh:
    """A triangle soup in file axes.

    tris:   (T, 3, 3) float64 corner positions
    mat:    list of T material names, as written in the OBJ (the group: one usemtl name)
    color:  (T, 3) float64 colour per triangle (Kd of the material, or the mean of its corners' vertex colours)
    corner_color: (T, 3, 3) float64 colour per corner (Kd or the vertex colour)
    """

    def __init__(self, tris, mat, color, corner_color, path=None):
        self.tris = tris
        self.mat = mat
        self.color = color
        self.corner_color = corner_color
        self.path = path

    def __len__(self):
        return len(self.mat)

    def select(self, mask):
        mask = np.asarray(mask)
        if mask.dtype != bool:
            idx = mask
        else:
            idx = np.nonzero(mask)[0]
        return Mesh(self.tris[idx], [self.mat[i] for i in idx], self.color[idx], self.corner_color[idx], self.path)

    def groups(self):
        """Material names in first-use order."""
        seen = []
        for m in self.mat:
            if m not in seen:
                seen.append(m)
        return seen

    def mask_of(self, pred):
        """Boolean mask of the triangles whose lower-case material name satisfies pred(name)."""
        cache = {}
        out = np.zeros(len(self.mat), bool)
        for i, m in enumerate(self.mat):
            v = cache.get(m)
            if v is None:
                v = bool(pred(m.lower()))
                cache[m] = v
            out[i] = v
        return out

    def points(self):
        return self.tris.reshape(-1, 3)

    def bounds(self):
        if len(self.mat) == 0:
            return None
        p = self.points()
        return p.min(axis=0), p.max(axis=0)


def read_mtl(path, into):
    """Reads newmtl/Kd from one MTL file into `into` (lower-case name -> (r, g, b)); missing file ignored."""
    if not os.path.exists(path):
        return
    current = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for raw in f:
            line = raw.strip()
            if not line or line[0] == "#":
                continue
            t = line.split()
            if t[0] == "newmtl" and len(t) > 1:
                current = t[1].lower()
                into.setdefault(current, DEFAULT_KD)
            elif t[0] == "Kd" and len(t) >= 4 and current is not None:
                into[current] = (_num(t[1]), _num(t[2]), _num(t[3]))


def _num(s):
    try:
        return float(s)
    except ValueError:
        return 0.0


def _index(s, count):
    try:
        i = int(s)
    except ValueError:
        return -1
    if i < 0:
        i = count + i
    else:
        i = i - 1
    return i if 0 <= i < count else -1


def read_obj(path):
    """Reads an OBJ into a Mesh in file axes (see the module docstring)."""
    path = Path(path)
    kd = {}
    pos = []
    vcol = []
    tris, mats, corner_cols = [], [], []
    current = ""
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()
    for raw in lines:
        line = raw.strip()
        if not line or line[0] == "#":
            continue
        t = line.split()
        if t[0] == "mtllib" and len(t) > 1:
            read_mtl(path.parent / line[6:].strip(), kd)
        elif t[0] == "usemtl":
            current = t[1] if len(t) > 1 else ""
        elif t[0] == "v" and len(t) >= 4:
            pos.append((_num(t[1]), _num(t[2]), _num(t[3])))
            vcol.append((_num(t[4]), _num(t[5]), _num(t[6])) if len(t) >= 7 else None)
        elif t[0] == "f" and len(t) >= 4:
            face = []
            bad = False
            for c in t[1:]:
                i = _index(c.split("/")[0], len(pos))
                if i < 0:
                    bad = True
                    break
                face.append(i)
            if bad:
                continue
            base = kd.get(current.lower(), DEFAULT_KD)
            for i in range(1, len(face) - 1):
                a, b, c = np.array(pos[face[0]]), np.array(pos[face[i]]), np.array(pos[face[i + 1]])
                n = np.cross(b - a, c - a)
                if float(n @ n) < 1e-12:
                    continue
                tris.append((a, b, c))
                mats.append(current)
                corner_cols.append([vcol[k] if vcol[k] is not None else base for k in (face[0], face[i], face[i + 1])])
    tri_arr = np.array(tris, dtype=np.float64).reshape(-1, 3, 3)
    cc = np.array(corner_cols, dtype=np.float64).reshape(-1, 3, 3)
    return Mesh(tri_arr, mats, cc.mean(axis=1) if len(mats) else np.zeros((0, 3)), cc, str(path))


def _fmt(x):
    s = "%.4f" % x
    if s == "-0.0000":
        s = "0.0000"
    return s


def _fmt_c(x):
    x = min(1.0, max(0.0, float(x)))
    return "%.3f" % x


def header_lines(living_file):
    return [
        "# IsoRim animal skeleton (scripts/make_animal_skeletons.py, Multiplayer port), from %s." % living_file,
        "# The figure faces -z. Closed pieces, clockwise from outside.",
        "mtllib isorim.mtl",
        "mtllib skeleton.mtl",
    ]


def rounded_part(part):
    """The part's vertices as they will be written (4 decimals), for validation of the written file."""
    return np.round(np.asarray(part.verts, dtype=np.float64), 4) + 0.0


def obj_text(parts, living_file):
    """OBJ text for a list of prims.Part. Vertices are shared within a part; a vertex used by triangles of
    different explicit colours is written once per colour. Triangles are grouped by material inside each
    part, parts in order."""
    out = header_lines(living_file)
    base = 0
    for part in parts:
        verts = rounded_part(part)
        tris = np.asarray(part.tris, dtype=np.int64)
        cols = part.col  # (T, 3) with NaN for none
        # vertex key: (vertex index, colour or None)
        key_index = {}
        vlines = []
        face_idx = np.zeros_like(tris)
        for t in range(len(tris)):
            c = cols[t] if cols is not None else None
            ck = None
            if c is not None and not np.isnan(c[0]):
                ck = tuple(_fmt_c(v) for v in c)
            for k in range(3):
                vi = int(tris[t, k])
                key = (vi, ck)
                j = key_index.get(key)
                if j is None:
                    j = len(vlines)
                    key_index[key] = j
                    p = verts[vi]
                    if ck is None:
                        vlines.append("v %s %s %s" % (_fmt(p[0]), _fmt(p[1]), _fmt(p[2])))
                    else:
                        vlines.append("v %s %s %s %s %s %s" % (_fmt(p[0]), _fmt(p[1]), _fmt(p[2]), ck[0], ck[1], ck[2]))
                face_idx[t, k] = j
        out.extend(vlines)
        # faces grouped by material in first-use order
        order = []
        by_mat = {}
        for t in range(len(tris)):
            m = part.mat[t]
            if m not in by_mat:
                by_mat[m] = []
                order.append(m)
            by_mat[m].append(t)
        for m in order:
            out.append("usemtl %s" % m)
            for t in by_mat[m]:
                a, b, c = face_idx[t] + base + 1
                out.append("f %d %d %d" % (a, b, c))
        base += len(vlines)
    return "\n".join(out) + "\n"


def write_obj(path, parts, living_file):
    text = obj_text(parts, living_file)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return text
