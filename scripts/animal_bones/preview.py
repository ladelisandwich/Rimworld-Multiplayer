#!/usr/bin/env python3
"""Preview renderer for IsoRim's animal skeletons (review tool, not shipped; Multiplayer port).

Renders each animal's living model next to its bones model (Models/<file>_bones.obj, written by
scripts/make_animal_skeletons.py) with a small numpy + Pillow software rasterizer, so the bones can be
checked without starting the game:

  python3 scripts/animal_bones/preview.py --only Cow,Chicken
  python3 scripts/animal_bones/preview.py --all
  python3 scripts/animal_bones/preview.py --plan birds,legless
  python3 scripts/animal_bones/preview.py --obj Models/skeleton_male_upper.obj,Models/skeleton_male_lower.obj,Models/person_skull.obj

Per animal it writes <thingDef>.png, one row of six tiles: living standing, bones standing and bones dead
(on its side, or belly up for deadBellyUp defs), each from a side camera and from a 3/4 camera; and contact
sheets sheet_<n>.png of up to 8 rows.

What it reproduces from IsoRim (IsoRim/Source):
  - ObjLoader.Load and ReadMaterials (Models.cs): mtllib (several), usemtl, v with 3 or 7 numbers (the
    per-vertex colour wins over Kd), f split into a fan from corner 0, negative indices, degenerate triangles
    dropped, material names matched ignoring case, an unknown material grey 0.8 (white for stuff*), z flipped
    to game axes (x east, y up, z north), and TurnRoundIfInsideOut. beam_* faces are left out (drawn apart).
  - Animals.Draw: the body at the origin turned by <rotation>; one leg per <legs> hip (legFromHip legs from
    the hip outward, turned by legYaw; others dropped by legLength under the hip and leaned by legSplay); wings
    folded by wingFold/wingDroop, the left one mirrored. Dead poses exactly as there: on its side (rolled 90 deg
    about +z round bodyCentre, lifted by the rest width: def.bodyHalfWidth for the living model, the bones
    body's own rest width for the bones), or belly up (rolled 180 deg, lifted by the body's maxY, legs curled).
    Gait 0, size 1, yaw 0 (facing north).
  - Shading: flat per triangle with IsoRim's model curve (IsoRimSolid.frag ModelShade: 0.5 facing away from
    the sun, 1 full on), drawn from both sides like the game; the outline is the game's inside-out hull, 0.03
    cells wide (BuildHull, approximated mitre), in the face colour times 0.25.
Not reproduced: the 3D view's vertical squash, the game's lighting overlay and colour grade, textures.

The bones tiles also carry the living model's silhouette (orange line, same pose and lift as the bones) so
you can see whether the bones sit inside the animal.
"""

import argparse
import math
import os
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from concurrent.futures import ProcessPoolExecutor
from functools import lru_cache
from pathlib import Path

# One BLAS thread per process: the work is spread over processes, one per animal (oversubscribed it ran 3x slower).
for _var in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ.setdefault(_var, "1")

import numpy as np  # noqa: E402
from PIL import Image, ImageDraw, ImageFilter, ImageFont

REPO = Path(__file__).resolve().parents[2]
ISORIM = REPO / "IsoRim"
DEFAULT_MODELS = ISORIM / "Models"
DEFAULT_DEFS = ISORIM / "Defs" / "Models.xml"
DEFAULT_OUT = Path(tempfile.gettempdir()) / "animal_bones_previews"

OUTLINE_WIDTH = 0.03     # ObjLoader.OutlineWidth, cells (--outline changes it for a run)
MITRE_LIMIT = 1.5        # ObjLoader.MitreLimit, outline widths
DEFAULT_COLOR = (0.8, 0.8, 0.8)
BELLY_UP_CURL = 30.0     # Animals.BellyUpCurl
ANIMALS_PER_SHEET = 8

# Body plans (design spec, "Body plans"); everything not listed is a quadruped.
PLAN_SPECIES = {
    "birds": "Chicken Duck Goose Turkey Emu Ostrich Cassowary Penguin",
    "legless": "Seal SeaLion Walrus Cobra",
    "husk": "Megascarab Spelopede Megaspider Locust Larva HiveQueen HermitCrab StoneCrab LavaSnail "
            "Tortoise SeaTurtle Armadillo",
}
PLAN_OF = {name: plan for plan, names in PLAN_SPECIES.items() for name in names.split()}


def plan_of(thing_def):
    if thing_def.startswith("Dryad_"):
        return "husk"
    return PLAN_OF.get(thing_def, "quadruped")


# ---------------------------------------------------------------- OBJ loading (ObjLoader.Load)

class Mesh:
    """A loaded model in game axes: tri (T,3,3) corners, col (T,3,3) corner colours, glow (T,), hull (T,3,3)
    outline offsets, as the loader's triangle soup."""

    def __init__(self, path, tri, col, glow, warnings, notes):
        self.path = Path(path)
        self.notes = notes          # harmless: an MTL read from the models folder instead of next to the OBJ
        self.tri = tri
        self.col = col
        self.glow = glow
        self.warnings = warnings
        self.faces = len(tri)
        self.maxY = max(0.0, float(tri[:, :, 1].max())) if len(tri) else 0.0   # Load: starts at 0
        self.hull = build_hull(tri)


def _num(s):
    try:
        return float(s)
    except ValueError:
        return 0.0


def _index(token, count):
    try:
        idx = int(token)
    except ValueError:
        return -1
    idx = count + idx if idx < 0 else idx - 1
    return idx if 0 <= idx < count else -1


def _add_material(name, materials, index):
    tinted = name.lower().startswith("stuff")
    glow = name.lower().startswith(("flame", "glow", "ember"))
    materials.append({"name": name, "color": (1.0, 1.0, 1.0) if tinted else DEFAULT_COLOR, "glow": glow})
    index[name.lower()] = len(materials) - 1
    return len(materials) - 1


def _read_materials(path, materials, index, warnings, notes, fallback_dir):
    if not path.is_file():
        alt = Path(fallback_dir) / path.name if fallback_dir else None
        if alt is not None and alt.is_file():
            # (the game would find none and draw grey; bones written elsewhere for review have no MTLs beside them)
            notes.append("mtllib %s read from %s" % (path.name, fallback_dir))
            path = alt
        else:
            warnings.append("material file not found: %s" % path)
            return
    current = None
    for raw in path.read_text(errors="replace").splitlines():
        line = raw.strip()
        if not line or line[0] == "#":
            continue
        t = line.split()
        if t[0] == "newmtl" and len(t) > 1:
            i = index.get(t[1].lower())
            if i is None:
                i = _add_material(t[1], materials, index)
            current = materials[i]
        elif t[0] == "Kd" and len(t) >= 4 and current is not None:
            current["color"] = (_num(t[1]), _num(t[2]), _num(t[3]))


@lru_cache(maxsize=None)
def load_obj(path, fallback_dir=None):
    """Mesh for an OBJ file, or None when it is missing or has no faces."""
    path = Path(path)
    if not path.is_file():
        return None
    warnings, notes = [], []
    pos, vcol = [], []
    materials = [{"name": "", "color": DEFAULT_COLOR, "glow": False}]
    index = {}
    current = 0
    tris, cols, glow, skipped_beam = [], [], [], 0
    for raw in path.read_text(errors="replace").splitlines():
        line = raw.strip()
        if not line or line[0] == "#":
            continue
        t = line.split()
        key = t[0]
        if key == "v" and len(t) >= 4:
            pos.append((_num(t[1]), _num(t[2]), -_num(t[3])))   # file -z = game +z (north)
            vcol.append((_num(t[4]), _num(t[5]), _num(t[6])) if len(t) >= 7 else None)
        elif key == "f" and len(t) >= 4:
            face = []
            for tok in t[1:]:
                idx = _index(tok.split("/")[0], len(pos))
                if idx < 0:
                    face = None
                    break
                face.append(idx)
            if face is None:
                continue
            mat = materials[current]
            if mat["name"].lower().startswith("beam_"):
                skipped_beam += 1
                continue
            for i in range(1, len(face) - 1):
                corners = (face[0], face[i], face[i + 1])
                a, b, c = (np.array(pos[k]) for k in corners)
                n = np.cross(b - a, c - a)
                if float(n @ n) < 1e-12:
                    continue
                tris.append([pos[k] for k in corners])
                cols.append([vcol[k] if vcol[k] is not None else mat["color"] for k in corners])
                glow.append(mat["glow"])
        elif key == "mtllib" and len(t) > 1:
            _read_materials(path.parent / line[6:].strip(), materials, index, warnings, notes, fallback_dir)
        elif key == "usemtl":
            name = t[1] if len(t) > 1 else ""
            current = index.get(name.lower())
            if current is None:
                current = _add_material(name, materials, index)
    if not tris:
        return None
    if skipped_beam:
        warnings.append("%d beam_* faces left out" % skipped_beam)
    tri = np.array(tris, dtype=np.float64)
    col = np.array(cols, dtype=np.float32)
    tri, col = turn_round_if_inside_out(tri, col)
    return Mesh(path, tri, col, np.array(glow, dtype=bool), warnings, notes)


def _keys(p):
    return np.round(p * 10000.0).astype(np.int64)   # ObjLoader.HullKey


def is_closed(tri):
    """Every edge (corners matched by position) also run the other way by some triangle (ObjLoader.IsClosed)."""
    k = _keys(tri.reshape(-1, 3))
    _, vid = np.unique(k, axis=0, return_inverse=True)
    vid = vid.reshape(-1, 3).astype(np.int64)
    a = vid.reshape(-1)
    b = vid[:, [1, 2, 0]].reshape(-1)
    n = int(vid.max()) + 1
    fwd = np.unique(a * n + b)
    rev = b * n + a
    return bool(np.isin(rev, fwd).all())


def turn_round_if_inside_out(tri, col):
    p0, p1, p2 = tri[:, 0], tri[:, 1], tri[:, 2]
    volume = float(np.einsum("ij,ij->i", p0, np.cross(p1, p2)).sum() / 6.0)
    size = tri.reshape(-1, 3).max(0) - tri.reshape(-1, 3).min(0)
    if volume >= 0.0 or (volume >= -0.1 * size[0] * size[1] * size[2] and not is_closed(tri)):
        return tri, col
    return tri[:, [0, 2, 1]].copy(), col[:, [0, 2, 1]].copy()


def face_normals(tri):
    n = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    ln = np.linalg.norm(n, axis=1, keepdims=True)
    return n / np.maximum(ln, 1e-20)


def build_hull(tri):
    """Per-corner outline offsets like ObjLoader.BuildHull: each corner position moves out along a mitre of the
    face normals around it (here the normalised sum, stretched so every face moves out by the full width, at most
    MitreLimit widths); corners on the ground only move sideways."""
    if len(tri) == 0:
        return np.zeros_like(tri)
    pts = tri.reshape(-1, 3)
    n = np.repeat(face_normals(tri), 3, axis=0)
    ground = pts[:, 1] < 0.001
    if ground.any():
        g = n[ground].copy()
        g[:, 1] = 0.0
        sq = (g * g).sum(1)
        g /= np.sqrt(np.maximum(sq, 1e-20))[:, None]
        g[sq < 0.01] = 0.0
        n[ground] = g
    valid = (n * n).sum(1) > 0.5
    _, inv = np.unique(_keys(pts), axis=0, return_inverse=True)
    inv = inv.reshape(-1)
    count = int(inv.max()) + 1
    s = np.zeros((count, 3))
    np.add.at(s, inv[valid], n[valid])
    ln = np.linalg.norm(s, axis=1)
    d = np.where(ln[:, None] > 1e-3, s / np.maximum(ln, 1e-20)[:, None], 0.0)
    dots = (d[inv] * n).sum(1)
    mind = np.full(count, np.inf)
    np.minimum.at(mind, inv[valid], dots[valid])
    stretch = np.where(mind > 1.0 / MITRE_LIMIT, 1.0 / np.maximum(mind, 1e-9), MITRE_LIMIT)
    off = d * (stretch * OUTLINE_WIDTH)[:, None]
    return off[inv].reshape(tri.shape)


# ---------------------------------------------------------------- posing (Animals.Draw)

def T(x, y, z):
    m = np.eye(4)
    m[:3, 3] = (x, y, z)
    return m


def _rot(axis, deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    m = np.eye(4)
    if axis == "x":
        m[1:3, 1:3] = [[c, -s], [s, c]]
    elif axis == "y":
        m[0, 0], m[0, 2], m[2, 0], m[2, 2] = c, s, -s, c
    else:
        m[0:2, 0:2] = [[c, -s], [s, c]]
    return m


def Rx(deg):
    return _rot("x", deg)


def Ry(deg):
    return _rot("y", deg)   # Unity's AngleAxis(deg, up): +z turns toward +x


def Rz(deg):
    return _rot("z", deg)   # AngleAxis(deg, forward): +x turns toward +y


MIRROR = np.diag([-1.0, 1.0, 1.0, 1.0])


class Part:
    def __init__(self, tri, col, glow, hull):
        self.tri, self.col, self.glow, self.hull = tri, col, glow, hull


def posed(mesh, m):
    """The mesh moved by the 4x4 matrix m. A mirroring matrix swaps corners 1 and 2 so the faces stay
    wound outward (ObjLoader.Mirrored)."""
    r = m[:3, :3]
    tri = mesh.tri @ r.T + m[:3, 3]
    hull = mesh.hull @ r.T
    col = mesh.col
    if np.linalg.det(r) < 0:
        tri, hull, col = tri[:, [0, 2, 1]], hull[:, [0, 2, 1]], col[:, [0, 2, 1]]
    return Part(tri, col, mesh.glow, hull)


def assemble(d, body, leg, wing, pose, rest):
    """Parts of an animal as Animals.Draw places them (yaw 0, size 1, gait 0). pose: stand, side, bellyup."""
    parts = []
    if body is None:
        return parts
    if pose == "bellyup":
        body_pose = T(0, body.maxY, 0) @ Rz(180)
        leg_pose = body_pose
    elif pose == "side":
        body_pose = T(0, rest, 0) @ Rz(90) @ T(0, -d["bodyCentre"], 0)
        leg_pose = body_pose
    else:
        body_pose = np.eye(4)
        leg_pose = np.eye(4)
    belly_up = pose == "bellyup"
    body_turn = Ry(d["rotation"]) if d["rotation"] != 0 else np.eye(4)
    parts.append(posed(body, body_pose @ body_turn))

    if leg is not None and d["legs"]:
        drop = T(0, -d["legLength"], 0)
        for i, hip in enumerate(d["legs"]):
            if d["legFromHip"]:
                spread = d["legYaw"][i] if i < len(d["legYaw"]) else 0.0
                lift = 0.0
                if belly_up:
                    spread *= 0.5
                    lift = -BELLY_UP_CURL
                side = Ry(-spread) if hip[0] >= 0 else Ry(180.0 + spread)
                local = leg_pose @ T(*hip) @ side @ Rz(lift)
            else:
                splay = d["legSplay"] if hip[0] >= 0 else -d["legSplay"]
                swing = 0.0
                if belly_up:
                    splay = -BELLY_UP_CURL * 0.4 if hip[0] >= 0 else BELLY_UP_CURL * 0.4
                    swing = math.copysign(BELLY_UP_CURL * 0.3, hip[2]) if abs(hip[2]) > 0.02 else 0.0
                local = leg_pose @ T(*hip) @ Rz(splay) @ Rx(swing) @ drop
            parts.append(posed(leg, local))

    if wing is not None and d["wings"]:
        # On the ground (alive or dead): open 0, so folded back by wingFold and lowered by wingDroop.
        turn = Ry(d["wingFold"]) @ Rz(-d["wingDroop"])
        for hinge in d["wings"]:
            local = body_pose @ T(*hinge) @ (MIRROR @ turn if hinge[0] < 0 else turn)
            parts.append(posed(wing, local))
    return parts


def rest_width(d, body):
    """How far the bones body reaches sideways, for the on-side pose: as ModelLibrary.BonesRest (Models.cs), the
    largest |x| of its corners between the rear and front hips, the body turned by <rotation>; with no legs or every
    hip at one depth (within 0.01), the middle 60% of its length; bodyHalfWidth when nothing is in that stretch."""
    pts = body.tri.reshape(-1, 3) @ Ry(d["rotation"])[:3, :3].T
    z = pts[:, 2]
    hz = [h[2] for h in d["legs"]]
    lo, hi = (min(hz), max(hz)) if hz else (math.inf, -math.inf)
    if hi - lo < 0.01:
        z0, z1 = float(z.min()), float(z.max())
        lo, hi = z0 + 0.2 * (z1 - z0), z1 - 0.2 * (z1 - z0)
    sel = (z >= lo) & (z <= hi)
    reach = float(np.abs(pts[sel, 0]).max()) if sel.any() else 0.0
    return reach if reach > 0.0 else d["bodyHalfWidth"]


# ---------------------------------------------------------------- Models.xml

def _vec(s):
    s = s.strip().strip("()")
    return tuple(float(v) for v in s.split(","))


def read_defs(defs_path):
    """The active (not commented out) animal ModelDefs, in file order."""
    root = ET.parse(defs_path).getroot()
    out = []
    for e in root:
        if e.findtext("animal", "").strip().lower() != "true":
            continue

        def f(tag, default):
            v = e.findtext(tag)
            return float(v) if v is not None and v.strip() else default

        def lst(tag, conv):
            node = e.find(tag)
            return [conv(li.text) for li in node.findall("li")] if node is not None else []

        out.append({
            "defName": e.findtext("defName", ""),
            "thingDef": e.findtext("thingDef", "").strip(),
            "model": (e.findtext("model") or "").strip(),
            "legModel": (e.findtext("legModel") or "").strip() or None,
            "wingModel": (e.findtext("wingModel") or "").strip() or None,
            "rotation": f("rotation", 0.0),
            "scale": f("scale", 1.0),
            "legs": lst("legs", _vec),
            "legLength": f("legLength", 0.0),
            "legSplay": f("legSplay", 0.0),
            "legFromHip": e.findtext("legFromHip", "").strip().lower() == "true",
            "legYaw": lst("legYaw", float),
            "deadBellyUp": e.findtext("deadBellyUp", "").strip().lower() == "true",
            "wings": lst("wings", _vec),
            "wingFold": f("wingFold", 90.0),
            "wingDroop": f("wingDroop", 0.0),
            "bodyCentre": f("bodyCentre", 0.0),
            "bodyHalfWidth": f("bodyHalfWidth", 0.2),
            "bodyHalfLength": f("bodyHalfLength", 0.4),
        })
    return out


def bones_name(model_path):
    p = Path(model_path)
    return p.stem + "_bones.obj"


# ---------------------------------------------------------------- cameras and rasterizer

class Camera:
    """Orthographic camera in game axes (left-handed: x east, y up, z north). f: the way it looks."""

    def __init__(self, name, f):
        self.name = name
        f = np.asarray(f, dtype=np.float64)
        f /= np.linalg.norm(f)
        up = np.array([0.0, 1.0, 0.0])
        r = np.cross(up, f)          # left-handed: the viewer's right hand
        r /= np.linalg.norm(r)
        u = np.cross(f, r)
        self.r, self.u, self.f = r, u, f
        self.basis = np.stack([r, u, f])
        # A sun fixed to the camera: high, from the front-left of the view, so the faces toward the viewer show.
        toward = -f.copy()
        toward[1] = 0.0
        toward = toward / max(np.linalg.norm(toward), 1e-9) if np.linalg.norm(toward) > 1e-6 else np.array([0, 0, -1.0])
        h = math.cos(math.radians(40)) * toward - math.sin(math.radians(40)) * r
        light = 0.82 * up + 0.57 * h
        self.light = light / np.linalg.norm(light)


def make_cameras():
    side = Camera("side", (1.0, 0.0, 0.0))            # from the west (game -x), looking at the left flank
    pitch, yaw = math.radians(55.0), math.radians(30.0)
    # from the east (right flank), turned 30 deg toward the front, looking 55 deg down
    hx, hz = math.cos(yaw), math.sin(yaw)
    pos = np.array([hx * math.cos(pitch), math.sin(pitch), hz * math.cos(pitch)])
    three = Camera("3/4", -pos)
    return [side, three]


FRAG_CHUNK = 1_500_000


def raster(X, Y, W, H):
    """Fragments of triangles given in pixel coordinates (X, Y: (T,3), y down). Yields (tri index, pixel x,
    pixel y, l0, l1, l2) per chunk for the pixel centres inside each triangle."""
    if len(X) == 0:
        return
    xmin = np.clip(np.ceil(X.min(1) - 0.5), 0, W - 1).astype(np.int64)
    xmax = np.clip(np.floor(X.max(1) - 0.5), -1, W - 1).astype(np.int64)
    ymin = np.clip(np.ceil(Y.min(1) - 0.5), 0, H - 1).astype(np.int64)
    ymax = np.clip(np.floor(Y.max(1) - 0.5), -1, H - 1).astype(np.int64)
    off = (X.max(1) < 0) | (Y.max(1) < 0) | (X.min(1) > W) | (Y.min(1) > H)
    w = np.where(off, 0, np.maximum(xmax - xmin + 1, 0))
    h = np.where(off, 0, np.maximum(ymax - ymin + 1, 0))
    ax, bx, cx = X[:, 0], X[:, 1], X[:, 2]
    ay, by, cy = Y[:, 0], Y[:, 1], Y[:, 2]
    area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax)
    n = np.where(np.abs(area) > 1e-9, w * h, 0)
    area = np.where(np.abs(area) > 1e-9, area, 1.0)
    A0, B0, C0 = (by - cy) / area, (cx - bx) / area, (bx * cy - by * cx) / area
    A1, B1, C1 = (cy - ay) / area, (ax - cx) / area, (cx * ay - cy * ax) / area
    tri_ids = np.nonzero(n)[0]
    if len(tri_ids) == 0:
        return
    cum = np.cumsum(n[tri_ids])
    start = 0
    while start < len(tri_ids):
        base = cum[start - 1] if start > 0 else 0
        end = int(np.searchsorted(cum, base + FRAG_CHUNK, side="right"))
        end = max(end, start + 1)
        ids = tri_ids[start:end]
        cnt = n[ids]
        t = np.repeat(ids, cnt)
        first = np.cumsum(cnt) - cnt
        local = np.arange(int(cnt.sum()), dtype=np.int64) - np.repeat(first, cnt)
        wt = w[t]
        py, px = np.divmod(local, wt)
        px += xmin[t]
        py += ymin[t]
        fx = px + 0.5
        fy = py + 0.5
        l0 = A0[t] * fx + B0[t] * fy + C0[t]
        l1 = A1[t] * fx + B1[t] * fy + C1[t]
        l2 = 1.0 - l0 - l1
        eps = -1e-9
        inside = (l0 >= eps) & (l1 >= eps) & (l2 >= eps)
        yield t[inside], px[inside], py[inside], l0[inside], l1[inside], l2[inside]
        start = end


def model_shade(n, light):
    """IsoRimSolid.frag ModelShade: 1 full on to the sun (on top by day), 0.5 facing away."""
    d = np.maximum(0.0, n @ light)
    return 0.5 + 0.5 * np.clip(d / max(0.2, light[1]), 0.0, 1.0)


BG_SIDE = np.array([0.60, 0.64, 0.68])
SOIL_SIDE = np.array([0.40, 0.37, 0.33])
GROUND = np.array([0.46, 0.49, 0.43])
GHOST = np.array([1.0, 0.50, 0.12])


def gather(parts):
    if not parts:
        z = np.zeros((0, 3, 3))
        return z, z.astype(np.float32), np.zeros(0, bool), z
    return (np.concatenate([p.tri for p in parts]), np.concatenate([p.col for p in parts]),
            np.concatenate([p.glow for p in parts]), np.concatenate([p.hull for p in parts]))


class View:
    """A camera framed for one tile: scale in pixels per cell, centre in camera-plane units."""

    def __init__(self, cam, scale, cx, cy, size):
        self.cam, self.scale, self.cx, self.cy, self.size = cam, scale, cx, cy, size

    def project(self, pts, ss):
        p = pts @ self.cam.basis.T
        s = self.scale * ss
        half = self.size * ss / 2.0
        X = (p[..., 0] - self.cx) * s + half
        Y = half - (p[..., 1] - self.cy) * s
        return X, Y, p[..., 2]


def render_tile(view, parts, ghost_parts, ss, shadow=True):
    """One tile (size x size RGB uint8) of the parts seen through the view."""
    cam = view.cam
    W = H = view.size * ss
    s = view.scale * ss
    half = W / 2.0
    # background: the ground, as a plane in the depth buffer (3/4), or sky over soil (side)
    jj, ii = np.meshgrid(np.arange(W) + 0.5, np.arange(H) + 0.5)
    sx = (jj - half) / s + view.cx
    sy = (half - ii) / s + view.cy
    img = np.empty((H, W, 3), dtype=np.float32)
    if abs(cam.f[1]) > 1e-6:
        t = -(sx * cam.r[1] + sy * cam.u[1]) / cam.f[1]
        zbuf = t.reshape(-1)
        gx = sx * cam.r[0] + sy * cam.u[0] + t * cam.f[0]
        gz = sx * cam.r[2] + sy * cam.u[2] + t * cam.f[2]
        img[:] = GROUND
        line_w = 1.2 / s
        for step, k in ((0.25, 0.94), (1.0, 0.84)):
            on = (np.abs(gx / step - np.round(gx / step)) * step < line_w) | \
                 (np.abs(gz / step - np.round(gz / step)) * step < line_w)
            img[on] *= k
        if shadow and parts:
            tri, _, _, _ = gather(parts)
            flat = tri.copy()
            flat[..., 1] = 0.0
            sh = np.zeros((H, W), dtype=np.float32)
            X, Y, _ = view.project(flat, ss)
            for tt, px, py, *_ in raster(X, Y, W, H):
                sh[py, px] = 1.0
            r = max(1.0, 0.035 * s)
            blur = np.asarray(Image.fromarray((sh * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(r)),
                              dtype=np.float32) / 255.0
            img *= (1.0 - 0.38 * blur)[..., None]
    else:
        zbuf = np.full(H * W, np.inf)
        img[:] = BG_SIDE
        img[sy < 0] = SOIL_SIDE
    img = img.reshape(-1, 3)

    tri, col, glow, hull = gather(parts)
    frag_pix, frag_dep, frag_col = [], [], []
    if len(tri):
        n = face_normals(tri)
        facing = n @ cam.f
        lit_n = np.where(facing[:, None] > 0, -n, n)          # drawn from both sides
        shade = np.where(glow, 1.0, model_shade(lit_n, cam.light)).astype(np.float32)
        # the faces
        X, Y, D = view.project(tri, ss)
        for t, px, py, l0, l1, l2 in raster(X, Y, W, H):
            dep = D[t, 0] * l0 + D[t, 1] * l1 + D[t, 2] * l2
            c = (col[t, 0] * l0[:, None] + col[t, 1] * l1[:, None] + col[t, 2] * l2[:, None]) * shade[t][:, None]
            frag_pix.append(py * W + px)
            frag_dep.append(dep)
            frag_col.append(c.astype(np.float32))
        # the outline: the hull's faces that look away from the camera, in the face colour times 0.25
        back = np.nonzero(facing > 0)[0] if OUTLINE_WIDTH > 0 else np.zeros(0, dtype=np.int64)
        if len(back):
            htri = tri[back] + hull[back]
            hcol = col[back] * 0.25
            X, Y, D = view.project(htri, ss)
            for t, px, py, l0, l1, l2 in raster(X, Y, W, H):
                dep = D[t, 0] * l0 + D[t, 1] * l1 + D[t, 2] * l2
                c = hcol[t, 0] * l0[:, None] + hcol[t, 1] * l1[:, None] + hcol[t, 2] * l2[:, None]
                frag_pix.append(py * W + px)
                frag_dep.append(dep)
                frag_col.append(c.astype(np.float32))
    if frag_pix:
        pix = np.concatenate(frag_pix)
        dep = np.concatenate(frag_dep)
        fc = np.concatenate(frag_col)
        keep = dep < zbuf[pix]
        pix, dep, fc = pix[keep], dep[keep], fc[keep]
        order = np.lexsort((dep, pix))
        ps = pix[order]
        first = np.ones(len(ps), dtype=bool)
        first[1:] = ps[1:] != ps[:-1]
        win = order[first]
        img[pix[win]] = fc[win]
    img = img.reshape(H, W, 3)

    if ghost_parts:
        gtri = gather(ghost_parts)[0]
        mask = np.zeros((H, W), dtype=bool)
        X, Y, _ = view.project(gtri, ss)
        for t, px, py, *_ in raster(X, Y, W, H):
            mask[py, px] = True
        inner = mask.copy()
        inner[1:, :] &= mask[:-1, :]
        inner[:-1, :] &= mask[1:, :]
        inner[:, 1:] &= mask[:, :-1]
        inner[:, :-1] &= mask[:, 1:]
        edge = mask & ~inner
        if ss >= 2:   # about one output pixel wide
            e2 = edge.copy()
            e2[1:, :] |= edge[:-1, :]
            e2[:, 1:] |= edge[:, :-1]
            edge = e2
        img[edge] = img[edge] * 0.2 + GHOST * 0.8

    out = Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB")
    if ss > 1:
        out = out.reduce(ss)
    return out


def bounds(parts, cam):
    if not parts:
        return None
    pts = gather(parts)[0].reshape(-1, 3) @ cam.basis.T
    return pts[:, 0].min(), pts[:, 0].max(), pts[:, 1].min(), pts[:, 1].max()


def frame(cams, scenes, size, top_px):
    """One scale for every tile (the largest that fits all of them) and a centre per camera. scenes: lists of
    parts. top_px: room kept for the caption."""
    margin = 0.06 * size
    avail_w = size - 2 * margin
    avail_h = size - 2 * margin - top_px
    boxes = []
    scale = math.inf
    for cam in cams:
        bs = [b for b in (bounds(p, cam) for p in scenes) if b is not None]
        if not bs:
            boxes.append((0, 0))
            continue
        x0 = min(b[0] for b in bs); x1 = max(b[1] for b in bs)
        y0 = min(b[2] for b in bs); y1 = max(b[3] for b in bs)
        if cam.name == "side":
            y0 = min(y0, 0.0)
        scale = min(scale, avail_w / max(x1 - x0, 1e-3), avail_h / max(y1 - y0, 1e-3))
        boxes.append(((x0 + x1) / 2, (y0 + y1) / 2))
    if not math.isfinite(scale):
        scale = size / 2.0
    views = []
    for cam, (cx, cy) in zip(cams, boxes):
        # shift down by half the caption so the figure sits under it
        views.append(View(cam, scale, cx, cy + (top_px / 2) / scale, size))
    return views


# ---------------------------------------------------------------- tiles and labels

@lru_cache(maxsize=None)
def font(px, bold=False):
    names = ["DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"]
    for d in ("/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/dejavu-sans-fonts", "/usr/share/fonts/TTF"):
        for nme in names:
            p = Path(d) / nme
            if p.is_file():
                return ImageFont.truetype(str(p), px)
    try:
        return ImageFont.load_default(size=px)
    except TypeError:
        return ImageFont.load_default()


def caption(img, text, sub=None, color=(255, 255, 255)):
    d = ImageDraw.Draw(img)
    px = max(10, img.width // 22)
    d.rectangle([0, 0, img.width, px + 6], fill=(30, 30, 34))
    d.text((5, 2), text, font=font(px, True), fill=color)
    if sub:
        w = d.textlength(sub, font=font(px))
        d.text((img.width - w - 5, 2), sub, font=font(px), fill=(190, 190, 200))
    return img


def missing_tile(size, text, sub):
    img = Image.new("RGB", (size, size), (70, 62, 62))
    d = ImageDraw.Draw(img)
    for k in range(-size, size, 16):
        d.line([(k, size), (k + size, 0)], fill=(85, 74, 74), width=3)
    px = max(10, size // 14)
    msg = "missing"
    w = d.textlength(msg, font=font(px, True))
    d.text(((size - w) / 2, size / 2 - px), msg, font=font(px, True), fill=(255, 140, 120))
    sp = max(9, size // 26)
    w = d.textlength(sub, font=font(sp))
    d.text((max(2, (size - w) / 2), size / 2 + 6), sub, font=font(sp), fill=(230, 210, 200))
    return caption(img, text, None, (255, 160, 140))


def label_strip(width, height, lines):
    """lines: list of [(text, rgb, bold)] runs per line. Wider than width if the text needs it."""
    px = max(11, height // len(lines) - 6)
    measure = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    need = max(8 + sum(measure.textlength(t, font=font(px + (2 if b else 0), b)) + 12 for t, _, b in runs)
               for runs in lines)
    img = Image.new("RGB", (max(width, int(need) + 4), height), (22, 22, 26))
    d = ImageDraw.Draw(img)
    y = 3
    for runs in lines:
        x = 8
        for text, rgb, bold in runs:
            f = font(px + (2 if bold else 0), bold)
            d.text((x, y), text, font=f, fill=rgb)
            x += d.textlength(text, font=f) + 12
        y += px + 5
    return img


# ---------------------------------------------------------------- one animal

def render_animal(d, models_dir, bones_dir, size, ss, shadow):
    """The row image of one animal, and a one-line summary."""
    t0 = time.time()
    models_dir, bones_dir = Path(models_dir), Path(bones_dir)
    fb = str(models_dir)

    def living(rel):
        return load_obj(str(models_dir / Path(rel).name), fb) if rel else None

    def bones(rel):
        return load_obj(str(bones_dir / bones_name(rel)), fb) if rel else None

    body, leg, wing = living(d["model"]), living(d["legModel"]), living(d["wingModel"])
    bbody, bleg, bwing = bones(d["model"]), bones(d["legModel"]), bones(d["wingModel"])
    warn = []
    if body is None:
        warn.append("living body missing: " + d["model"])
    if bbody is None:
        warn.append("bones body missing: " + bones_name(d["model"]))
    if d["legModel"] and d["legs"] and bleg is None:
        warn.append("bones leg missing (no legs drawn): " + bones_name(d["legModel"]))
    if d["wingModel"] and bwing is None:
        warn.append("bones wing missing (no wings drawn): " + bones_name(d["wingModel"]))
    for m in (bbody, bleg, bwing):
        if m is not None:
            warn += ["%s: %s" % (m.path.name, w) for w in m.warnings]

    dead_pose = "bellyup" if d["deadBellyUp"] else "side"
    rest = rest_width(d, bbody) if bbody is not None else d["bodyHalfWidth"]
    live_stand = assemble(d, body, leg, wing, "stand", d["bodyHalfWidth"])
    bone_stand = assemble(d, bbody, bleg, bwing, "stand", rest)
    bone_dead = assemble(d, bbody, bleg, bwing, dead_pose, rest)
    # the living silhouette in the bones' dead pose: same lift as the bones (belly up: lifted by the bones' maxY)
    ghost_dead = []
    if body is not None and bbody is not None:
        if dead_pose == "bellyup":
            ghost_dead = assemble_ghost_bellyup(d, body, leg, wing, bbody)
        else:
            ghost_dead = assemble(d, body, leg, wing, "side", rest)

    cams = make_cameras()
    top_px = max(10, size // 22) + 8
    views = frame(cams, [live_stand, bone_stand, bone_dead, ghost_dead], size, top_px)
    dead_label = "bones · belly up" if dead_pose == "bellyup" else "bones · on side"
    tiles = []
    for v in views:
        cam_name = v.cam.name
        if body is None:
            tiles.append(missing_tile(size, "living · standing", Path(d["model"]).name))
        else:
            tiles.append(caption(render_tile(v, live_stand, None, ss, shadow), "living · standing", cam_name))
        if bbody is None:
            tiles.append(missing_tile(size, "bones · standing", bones_name(d["model"])))
            tiles.append(missing_tile(size, dead_label, bones_name(d["model"])))
        else:
            tiles.append(caption(render_tile(v, bone_stand, live_stand, ss, shadow), "bones · standing", cam_name))
            tiles.append(caption(render_tile(v, bone_dead, ghost_dead, ss, shadow), dead_label, cam_name))

    gap = 4
    width = len(tiles) * size + (len(tiles) - 1) * gap + 2 * gap
    tc = lambda m: str(m.faces) if m is not None else "-"
    info = [("living body %s / leg %s" % (tc(body), tc(leg)), (220, 220, 225), False),
            ("bones body %s / leg %s" % (tc(bbody), tc(bleg)), (235, 225, 190), False)]
    if d["wingModel"]:
        info.append(("wing %s / bones %s" % (tc(wing), tc(bwing)), (220, 220, 225), False))
    extra = [("rest %.3f (bodyHalfWidth %.3f)" % (rest, d["bodyHalfWidth"]), (170, 200, 230), False)]
    if bleg is not None and d["legs"]:
        extra.append(("bones leg top %.3f (legLength %.3f)" % (float(bleg.tri[:, :, 1].max()), d["legLength"]),
                      (170, 200, 230), False))
    if any(m is not None and m.notes for m in (bbody, bleg, bwing)):
        extra.append(("MTLs from the models folder", (150, 150, 160), False))
    if bbody is not None:
        extra.append(("bones body maxY %.3f (living %.3f)" % (bbody.maxY, body.maxY if body else 0.0),
                      (170, 200, 230), False))
    lines = [[(d["thingDef"], (255, 255, 255), True), ("[%s]" % plan_of(d["thingDef"]), (160, 200, 160), False)]
             + info, extra]
    for w in warn[:3]:
        lines.append([("! " + w, (255, 120, 100), False)])
    if len(warn) > 3:
        lines.append([("! ... %d more" % (len(warn) - 3), (255, 120, 100), False)])
    line_h = max(11, size // 18) + 7
    strip = label_strip(width, line_h * len(lines) + 6, lines)
    row = Image.new("RGB", (strip.width, strip.height + size + gap), (22, 22, 26))
    row.paste(strip, (0, 0))
    for i, t in enumerate(tiles):
        # a wider gap between the two cameras
        x = gap + i * (size + gap)
        row.paste(t, (x, strip.height))
    summary = "%-18s living %4s/%3s  bones %5s/%4s  rest %.3f  %4.1fs%s" % (
        d["thingDef"], tc(body), tc(leg), tc(bbody), tc(bleg), rest, time.time() - t0,
        ("  ! " + "; ".join(warn)) if warn else "")
    return row, summary


def assemble_ghost_bellyup(d, body, leg, wing, bbody):
    """The living model belly up but lifted by the bones body's maxY, so it lines up with the bones tile."""
    parts = assemble(d, body, leg, wing, "bellyup", 0.0)
    lift = bbody.maxY - body.maxY
    for p in parts:
        p.tri = p.tri + np.array([0.0, lift, 0.0])
    return parts


def _render_job(args):
    global OUTLINE_WIDTH
    d, models_dir, bones_dir, size, ss, shadow, out, outline = args
    OUTLINE_WIDTH = outline   # before any model of this process is loaded (each run uses one width)
    row, summary = render_animal(d, models_dir, bones_dir, size, ss, shadow)
    path = Path(out) / ("%s.png" % d["thingDef"])
    row.save(path)
    return str(path), summary


def write_sheets(paths, out, per_sheet=ANIMALS_PER_SHEET):
    sheets = []
    for n, k in enumerate(range(0, len(paths), per_sheet), start=1):
        rows = [Image.open(p).convert("RGB") for p in paths[k:k + per_sheet]]
        width = max(r.width for r in rows)
        height = sum(r.height for r in rows) + 6 * (len(rows) - 1)
        sheet = Image.new("RGB", (width, height), (10, 10, 12))
        y = 0
        for r in rows:
            sheet.paste(r, (0, y))
            y += r.height + 6
        p = Path(out) / ("sheet_%d.png" % n)
        sheet.save(p)
        sheets.append(str(p))
    return sheets


# ---------------------------------------------------------------- any OBJ files

def render_objs(paths, models_dir, out, size, ss, shadow):
    meshes = []
    for p in paths:
        q = Path(p)
        if not q.is_file() and (Path(models_dir) / q.name).is_file():
            q = Path(models_dir) / q.name
        m = load_obj(str(q.resolve()), str(models_dir))
        if m is None:
            sys.exit("not found or no faces: %s" % p)
        meshes.append(m)
    parts = [posed(m, np.eye(4)) for m in meshes]
    cams = make_cameras()
    top_px = max(10, size // 22) + 8
    views = frame(cams, [parts], size, top_px)
    tiles = [caption(render_tile(v, parts, None, ss, shadow), "standing", v.cam.name) for v in views]
    gap = 4
    width = len(tiles) * (size + gap) + gap
    names = ", ".join("%s (%d)" % (m.path.name, m.faces) for m in meshes)
    lines = [[("OBJ", (255, 255, 255), True), (names, (220, 220, 225), False)],
             [("total %d triangles" % sum(m.faces for m in meshes), (170, 200, 230), False)]]
    for m in meshes:
        for w in m.warnings:
            lines.append([("! %s: %s" % (m.path.name, w), (255, 120, 100), False)])
    line_h = max(11, size // 18) + 7
    strip = label_strip(width, line_h * len(lines) + 6, lines)
    row = Image.new("RGB", (strip.width, strip.height + size + gap), (22, 22, 26))
    row.paste(strip, (0, 0))
    for i, t in enumerate(tiles):
        row.paste(t, (gap + i * (size + gap), strip.height))
    stem = "+".join(m.path.stem for m in meshes)[:120]
    path = Path(out) / ("obj_%s.png" % stem)
    row.save(path)
    return str(path)


# ---------------------------------------------------------------- command line

def main(argv=None):
    global OUTLINE_WIDTH
    ap = argparse.ArgumentParser(description="Render IsoRim animals living vs bones to PNGs for review.")
    ap.add_argument("--only", help="thingDef names, comma separated (case ignored)")
    ap.add_argument("--all", action="store_true", help="every active animal ModelDef")
    ap.add_argument("--plan", help="body plans, comma separated: quadruped, birds, legless, husk")
    ap.add_argument("--obj", help="render these OBJ files together instead (comma separated)")
    ap.add_argument("--models-dir", default=str(DEFAULT_MODELS), help="living models (default IsoRim/Models)")
    ap.add_argument("--bones-dir", help="where the *_bones.obj files are (default: --models-dir)")
    ap.add_argument("--defs", default=str(DEFAULT_DEFS), help="Models.xml (default IsoRim/Defs/Models.xml)")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="output folder (default: animal_bones_previews in the system temp folder)")
    ap.add_argument("--tile", type=int, default=256, help="tile size in pixels (default 256)")
    ap.add_argument("--ss", type=int, default=2, help="supersampling factor (default 2)")
    ap.add_argument("--no-shadow", action="store_true", help="no ground shadow in the 3/4 view")
    ap.add_argument("--outline", type=float, default=OUTLINE_WIDTH,
                    help="outline hull width in cells (default 0.03, the game's; 0 for none)")
    ap.add_argument("--jobs", type=int, default=0, help="worker processes (default: half the CPUs)")
    a = ap.parse_args(argv)
    OUTLINE_WIDTH = a.outline

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    models_dir = Path(a.models_dir).resolve()
    bones_dir = Path(a.bones_dir).resolve() if a.bones_dir else models_dir
    t0 = time.time()

    if a.obj:
        p = render_objs([s.strip() for s in a.obj.split(",") if s.strip()], models_dir, out, a.tile, a.ss,
                        not a.no_shadow)
        print("wrote", p, "(%.1fs)" % (time.time() - t0))
        return 0

    if not (a.only or a.all or a.plan):
        ap.error("give --only, --all, --plan or --obj")
    defs = read_defs(a.defs)
    chosen = defs
    if a.only:
        want = {s.strip().lower() for s in a.only.split(",") if s.strip()}
        chosen = [d for d in chosen if d["thingDef"].lower() in want]
        unknown = want - {d["thingDef"].lower() for d in chosen}
        if unknown:
            print("not an active animal ModelDef: " + ", ".join(sorted(unknown)), file=sys.stderr)
    if a.plan:
        plans = {s.strip().lower() for s in a.plan.split(",") if s.strip()}
        chosen = [d for d in chosen if plan_of(d["thingDef"]) in plans]
    if not chosen:
        print("nothing to render", file=sys.stderr)
        return 1

    jobs = [(d, str(models_dir), str(bones_dir), a.tile, a.ss, not a.no_shadow, str(out), a.outline) for d in chosen]
    workers = a.jobs or max(1, min(len(jobs), (os.cpu_count() or 2) // 2))
    if workers > 1 and len(jobs) > 1:
        with ProcessPoolExecutor(max_workers=workers) as ex:
            results = list(ex.map(_render_job, jobs))
    else:
        results = [_render_job(j) for j in jobs]
    for _, summary in results:
        print(summary)
    sheets = write_sheets([p for p, _ in results], out)
    print("wrote %d animal PNGs and %d sheets to %s (%.1fs)" % (len(results), len(sheets), out, time.time() - t0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
