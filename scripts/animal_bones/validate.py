"""Checks for generated skeleton parts (Multiplayer port, not shipped).

Runs on the parts as they will be written (vertices rounded to 4 decimals, welded by position the way
IsoRim's outline and inside-out checks see them):
  - no NaN or infinite coordinates
  - every shell closed (each edge used once in each direction) and wound clockwise from outside in file
    axes (negative signed volume)
  - no triangle that the loader would drop as degenerate (|cross| < 1e-6, which would open the shell)
  - triangle budget for the animal's size (a warning)
  - the bones inside the living model's bounds plus a margin (body against body, leg against leg)
  - the pelvis and the scapulae reaching down to hip height (legLength)
  - no body bones below the ground (lowest y >= -0.005, legless bodies too: a body lying on the ground in its
    standing pose must not sink), except vertices copied from the living body (a husk's or a shell's copy
    sinks exactly as far as the living model does, e.g. the SeaTurtle's shell at -0.0088)
  - the leg's top at legLength (up to 0.035 above) and its foot on the ground, except for legFromHip defs (the
    Megaspider: the hip at the origin, the leg reaching outward)
  - a warning when the whole file, every part welded together at 4 decimals, has open or inconsistently wound
    edges or inside-out shells that the per-part checks don't see (IsoRim's outline hull keys its offsets by
    position across the whole model, so parts sharing positions must still form consistent closed shells)
Returns (errors, warnings) as lists of strings.
"""

import numpy as np

from . import objio, prims as P

MARGIN = 0.06
GROUND = -0.005     # lowest y a body's bones may reach (copies of the living body's own vertices excepted)


def _written(part):
    q = P.Part(objio.rounded_part(part), part.tris, part.mat, part.col, part.name)
    return P.weld(q, 4)


def check_part(part, label):
    errs = []
    if len(part.tris) == 0:
        return errs
    if not np.all(np.isfinite(part.verts)):
        errs.append("%s: NaN or infinite vertex" % label)
        return errs
    w = _written(part)
    if len(w.tris) != len(part.tris):
        errs.append("%s: %d triangle(s) collapse when rounded to 4 decimals" % (label, len(part.tris) - len(w.tris)))
    a, b, c = w.verts[w.tris[:, 0]], w.verts[w.tris[:, 1]], w.verts[w.tris[:, 2]]
    cr = np.linalg.norm(np.cross(b - a, c - a), axis=1)
    nd = int((cr < 1e-6).sum())
    if nd:
        errs.append("%s: %d degenerate triangle(s) the loader would drop" % (label, nd))
    oe = P.open_edges(w)
    if oe:
        errs.append("%s: %d open or inconsistent edge(s)" % (label, oe))
    for i, s in enumerate(P.shells(w)):
        v = P.signed_volume(w, s)
        if v >= 0:
            errs.append("%s: shell %d wound counter-clockwise (volume %+.6f)" % (label, i, v))
    return errs


def _parse_written(text):
    """Vertex positions and 0-based triangles of OBJ text as objio.obj_text writes it."""
    verts, tris = [], []
    for line in text.splitlines():
        if line.startswith("v "):
            t = line.split()
            verts.append((float(t[1]), float(t[2]), float(t[3])))
        elif line.startswith("f "):
            t = line.split()
            tris.append((int(t[1]) - 1, int(t[2]) - 1, int(t[3]) - 1))
    return np.array(verts, dtype=np.float64).reshape(-1, 3), np.array(tris, dtype=np.int64).reshape(-1, 3)


def check_file(parts, key):
    """Warnings for one file as objio writes it, every part welded together at 4 decimals (IsoRim's outline
    hull keys its offsets by position across the whole model). validate() runs it only when every part passed
    check_part, so what it finds appears only where parts meet: edges that two parts share in the same
    direction or leave open, and shells that come out inside out once joined."""
    warns = []
    names = ["%s[%d]" % (q.name or "part", i) for i, q in enumerate(parts)]
    verts, tris = _parse_written(objio.obj_text(parts, ""))
    owner = np.repeat(np.arange(len(parts)), [len(q.tris) for q in parts])
    if len(tris) != len(owner):
        return ["%s: whole file: %d faces written for %d triangles" % (key, len(tris), len(owner))]
    w = P.weld(P.Part(verts, tris, [str(int(i)) for i in owner]), 4)
    own = [int(m) for m in w.mat]

    def which(tri_idx):
        ps = sorted({own[int(t)] for t in tri_idx})
        s = ", ".join(names[p] for p in ps[:6])
        return s + (", ..." if len(ps) > 6 else "")

    if len(w.tris) != len(tris):
        warns.append("%s: whole file: %d triangle(s) collapse when the parts are welded at 4 decimals" % (
            key, len(tris) - len(w.tris)))
    edges = {}
    for t, (a, b, c) in enumerate(w.tris):
        for u, v in ((a, b), (b, c), (c, a)):
            edges.setdefault((int(u), int(v)), []).append(t)
    bad, at = 0, []
    for (u, v), ts in edges.items():
        back = edges.get((v, u), [])
        if len(ts) != 1 or len(back) != 1:
            bad += 1
            at += ts + back
    if bad:
        warns.append("%s: whole file: %d open or inconsistent edge(s) where parts meet (%s)" % (key, bad, which(at)))
    for s in P.shells(w):
        vol = P.signed_volume(w, s)
        if vol >= 0:
            warns.append("%s: whole file: a shell across %s is wound counter-clockwise (volume %+.6f)" % (
                key, which(s), vol))
    return warns


def as_list(v):
    if v is None:
        return []
    if isinstance(v, P.Part):
        return [v]
    return list(v)


def validate(animal, out):
    """Checks a plan's output dict for one animal. Returns (errors, warnings)."""
    errs, warns = [], []
    d = animal.d
    LL = float(d["legLength"])
    for key, parts in out.items():
        parts = as_list(parts)
        part_errs = []
        for i, part in enumerate(parts):
            part_errs += check_part(part, "%s/%s[%d]" % (key, part.name or "part", i))
        errs += part_errs
        n = sum(len(q) for q in parts)
        if key == "body" and parts:
            lo, hi = animal.budget_body
            if n > hi:
                warns.append("body: %d triangles, over the %s budget %d-%d" % (n, animal.size, lo, hi))
            elif n < 0.6 * lo:
                warns.append("body: %d triangles, well under the %s budget %d-%d" % (n, animal.size, lo, hi))
        if key == "leg" and parts:
            lo, hi = animal.budget_leg
            if n > hi:
                warns.append("leg: %d triangles, over the %s budget %d-%d" % (n, animal.size, lo, hi))
        if not parts:
            continue
        allv = np.concatenate([q.verts for q in parts])
        living = {"body": animal.body, "leg": animal.leg, "wing": animal.wing}.get(key)
        if living is not None and len(living):
            llo, lhi = living.bounds()
            if key == "body":
                # the Elephant's trunk hangs below ground; the bones may not
                llo = llo.copy()
            m = MARGIN
            bad_lo = allv.min(axis=0) < llo - m
            bad_hi = allv.max(axis=0) > lhi + m
            if np.any(bad_lo) or np.any(bad_hi):
                errs.append("%s: bones outside the living bounds + %.2f (bones %s..%s, living %s..%s)" % (
                    key, m, np.round(allv.min(axis=0), 3), np.round(allv.max(axis=0), 3), np.round(llo, 3),
                    np.round(lhi, 3)))
        if key == "body" and d["legs"]:
            for q in parts:
                if q.name in ("pelvis", "scapula"):
                    ymin = float(q.verts[:, 1].min())
                    if ymin > LL + 0.03:
                        errs.append("body: %s ends at y %.3f, above hip height %.3f" % (q.name, ymin, LL))
        if key == "body":
            low = np.round(allv[allv[:, 1] < GROUND], 4) + 0.0
            if len(low):
                # a copy of the living body's own vertices (a husk, a kept shell) sinks no further than it
                liv = {tuple(p) for p in (np.round(animal.body.points(), 4) + 0.0).tolist()}
                sunk = [p[1] for p in low.tolist() if tuple(p) not in liv]
                if sunk:
                    errs.append("body: bones below ground (y %.4f)" % min(sunk))
        if key == "leg" and not d.get("legFromHip"):
            # a legFromHip leg (the Megaspider's) has its hip at the origin and reaches outward
            top = float(allv[:, 1].max())
            if top < LL - 0.005 or top > LL + 0.035:
                errs.append("leg: top at y %.3f, legLength %.3f (allowed %.3f..%.3f)" % (top, LL, LL - 0.005,
                                                                                          LL + 0.035))
            bot = float(allv[:, 1].min())
            if bot < -0.005 or bot > 0.02:
                errs.append("leg: foot at y %.3f, not on the ground" % bot)
        if not part_errs:
            warns += check_file(parts, key)
    return errs, warns
