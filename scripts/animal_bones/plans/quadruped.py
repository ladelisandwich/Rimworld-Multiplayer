"""Quadruped plan: skeletons for the four-legged mammals, reptiles and frogs (Multiplayer port, not shipped).

Built from the living model (analyze.py) and the ModelDef, in FILE axes (the animal faces -z):

  skull     a loft through the living head's cross-sections, shrunk to 0.85 and shaped per family (cranium,
            snout, nose), a lower jaw under it, eye sockets as dark quads of the skull's own surface where the
            living eyes are and a dark nasal facet on the snout's front (none on narrow animals); horns,
            antlers, tusks and sabre teeth copied from the living model and moved in with the skull (tusks
            poke out of the mouth, wide antlers turn up so the dead pose props on them)
  neck      separate vertebrae from the back of the skull to the front of the spine, bent like the living neck
  spine     a loft just under the living back, from the neck to the sacrum, with lumbar vertebra bumps
  ribcage   a loft over the front of the trunk with rib ridges and bone_shade grooves (spine and sternum kept)
  pelvis    one slanted hip blade per side, from the sacrum back to the pin bone, its middle reaching down
            to the hip socket at hip height (over the leg's top)
  scapulae  plates on the ribcage at the front hips, from near the spine down to hip height
  tail      one loft along the living tail (or its root share), vertebrae as bone bands with shade joints
  extras    a bison's hump spines, a porcupine's quills (per species, see QUAD)
  leg       one loft per leg file: hip knob at y = legLength, thigh, knee, shin, ankle, then a hoof or a paw
            reaching forward to the living toe tip (the sloth's claws included)

Frogs and toads get their own body (Skeleton.frog_body: flat skull with orbits on top, no ribs or tail,
ilia and urostyle, folded hind legs through the living thigh pieces).

Families set the proportions (FAMILY); SPECIES_FAMILY maps each species; OVERRIDES tunes single species.
Triangle counts follow the spec's budgets by size (animal.size): fewer sides, ribs and vertebrae when small.
Narrow short-bodied medium animals (Guinea pig, Chinchilla, Raccoon, Lynx) get the small build: one-piece
neck, the jaw in the skull loft, one shoulder and one hip block, chunkier legs (fewer overlapping outlines).
"""

import math

import numpy as np

from .. import prims as P
from ..prims import BONE, SHADE

SPECIES_FAMILY = {
    # cattle, sheep, goats, antelopes, deer
    "Cow": "bovid", "Muffalo": "bovid", "Bison": "bovid", "Yak": "bovid", "Muskox": "bovid", "Sheep": "bovid",
    "Goat": "bovid", "Ibex": "bovid", "Gazelle": "bovid", "Boomalope": "bovid", "Toxalope": "bovid",
    "Deer": "bovid", "Elk": "bovid", "Caribou": "bovid", "Moose": "bovid",
    "Horse": "equid", "Donkey": "equid",
    "Alpaca": "camelid", "Dromedary": "camelid",
    "Pig": "suid", "WildBoar": "suid",
    "Husky": "canid", "LabradorRetriever": "canid", "YorkshireTerrier": "canid", "Fox_Fennec": "canid",
    "Fox_Red": "canid", "Fox_Arctic": "canid", "Wolf_Timber": "canid", "Wolf_Arctic": "canid", "Warg": "canid",
    "BogHound": "canid", "Wolf_Great": "canid",
    "Cat": "felid", "Cougar": "felid", "Panther": "felid", "Lynx": "felid", "Tiger": "felid",
    "Cat_Scimitar": "felid",
    "Bear_Grizzly": "ursid", "Bear_Polar": "ursid", "Panda": "ursid",
    "Raccoon": "mustelid", "Badger": "mustelid", "Mink": "mustelid", "Wolverine": "mustelid", "Otter": "mustelid",
    "GuineaPig": "rodent", "Rat": "rodent", "Boomrat": "rodent", "WasteRat": "rodent", "Squirrel": "rodent",
    "Hare": "rodent", "Snowhare": "rodent", "Chinchilla": "rodent", "Capybara": "rodent",
    "Alphabeaver": "rodent", "PrairieDog": "rodent", "Porcupine": "rodent", "Megavole": "rodent",
    "Monkey": "primate", "Gorilla": "primate",
    "Elephant": "elephantid", "Mastodon": "elephantid",
    "Rhinoceros": "rhino", "Hippo": "hippo",
    "Megasloth": "sloth",
    "Thrumbo": "thrumbo", "AlphaThrumbo": "thrumbo",
    "Iguana": "lizard", "MonitorLizard": "lizard", "Alligator": "lizard",
    "Bullfrog": "frog", "ColossusToad": "frog",
}

SPECIES = set(SPECIES_FAMILY)

# Skull profile: (t, width factor, height factor) along the head from the back (t = 0) to the snout tip
# (t = 1), multiplying the living cross-section (already shrunk to SHRINK). jaw: share of the skull's
# height that is lower jaw at the snout. orbit: socket radius as a share of the skull's half-width at the
# eye; fwd: how much the sockets face forward (0 = sideways, 1 = straight ahead).
BASE = dict(
    skull=[(0.0, 0.70, 0.75), (0.12, 0.95, 0.95), (0.35, 1.0, 1.0), (0.6, 0.85, 0.9), (0.85, 0.75, 0.8),
           (1.0, 0.6, 0.65)],
    jaw=0.32, orbit=0.36, fwd=0.15, ribs=6, rib_frac=0.62, neck=6, tail_max=12, leg="paw", spine_r=0.16,
    rib_depth=0.92, pelvis_len=1.0, scap=1.0, nose=0.32, ribs_on=True,
    tail_extra=0, leg_r=0.30,
)

FAMILY = {
    "bovid": dict(skull=[(0.0, 0.75, 0.80), (0.15, 1.0, 1.0), (0.38, 0.95, 0.95), (0.62, 0.72, 0.82),
                         (0.85, 0.62, 0.72), (1.0, 0.55, 0.6)], leg="hoof", jaw=0.36, orbit=0.40, ribs=7,
                  rib_frac=0.62),
    "equid": dict(skull=[(0.0, 0.70, 0.75), (0.15, 1.0, 1.0), (0.35, 0.95, 0.95), (0.6, 0.70, 0.85),
                         (0.85, 0.62, 0.75), (1.0, 0.58, 0.62)], leg="hoof", jaw=0.40, orbit=0.40, ribs=8,
                  rib_frac=0.64, neck=7),
    "camelid": dict(skull=[(0.0, 0.70, 0.75), (0.15, 1.0, 1.0), (0.4, 0.92, 0.92), (0.7, 0.70, 0.8),
                           (1.0, 0.55, 0.6)], leg="hoof", jaw=0.36, orbit=0.42, ribs=7, rib_frac=0.62, neck=8),
    # a deep snout down to the mouth line, so the tusks have a jaw to come out of
    "suid": dict(skull=[(0.0, 0.80, 0.85), (0.15, 1.0, 1.0), (0.4, 0.92, 0.9), (0.7, 0.72, 0.78),
                        (1.0, 0.62, 0.7)], leg="hoof", jaw=0.34, orbit=0.32, ribs=7, rib_frac=0.66, neck=4, tip=0.93),
    "canid": dict(skull=[(0.0, 0.65, 0.75), (0.15, 1.0, 1.0), (0.4, 0.95, 0.95), (0.62, 0.62, 0.70),
                         (0.85, 0.50, 0.58), (1.0, 0.42, 0.50)], jaw=0.36, orbit=0.40, fwd=0.35, ribs=6,
                  rib_frac=0.62, tail_max=14, tail_extra=1),
    "felid": dict(skull=[(0.0, 0.70, 0.78), (0.15, 1.0, 1.0), (0.45, 1.0, 0.95), (0.75, 0.80, 0.75),
                         (1.0, 0.6, 0.55)], jaw=0.36, orbit=0.40, fwd=0.5, ribs=6, rib_frac=0.60,
                  tail_max=16, tail_extra=2),
    "ursid": dict(skull=[(0.0, 0.70, 0.78), (0.15, 1.0, 1.0), (0.4, 0.95, 0.95), (0.7, 0.72, 0.75),
                         (1.0, 0.58, 0.58)], jaw=0.38, orbit=0.36, fwd=0.35, ribs=7, rib_frac=0.62, neck=5),
    "mustelid": dict(skull=[(0.0, 0.72, 0.78), (0.15, 1.0, 1.0), (0.45, 0.95, 0.9), (0.75, 0.70, 0.7),
                            (1.0, 0.55, 0.55)], jaw=0.35, orbit=0.42, fwd=0.35, ribs=6, rib_frac=0.58,
                     tail_max=14, tail_extra=2),
    "rodent": dict(skull=[(0.0, 0.72, 0.80), (0.15, 1.0, 1.0), (0.45, 0.92, 0.92), (0.75, 0.70, 0.75),
                          (1.0, 0.55, 0.6)], jaw=0.34, orbit=0.42, fwd=0.1, ribs=4, rib_frac=0.58, neck=4,
                   tail_max=14),
    "primate": dict(skull=[(0.0, 0.80, 0.85), (0.2, 1.0, 1.0), (0.55, 0.95, 0.95), (0.85, 0.80, 0.75),
                           (1.0, 0.7, 0.6)], jaw=0.38, orbit=0.42, fwd=0.85, ribs=6, rib_frac=0.62, neck=5,
                    tail_max=16, tail_extra=4),
    "elephantid": dict(skull=[(0.0, 0.80, 0.85), (0.2, 1.0, 1.0), (0.55, 0.95, 0.95), (0.8, 0.78, 0.8),
                              (1.0, 0.62, 0.65)], leg="column", jaw=0.35, orbit=0.30, ribs=7, rib_frac=0.66,
                       neck=4, tail_max=10),
    "rhino": dict(skull=[(0.0, 0.70, 0.80), (0.15, 1.0, 1.0), (0.45, 0.90, 0.9), (0.75, 0.70, 0.72),
                         (1.0, 0.55, 0.58)], leg="column", jaw=0.38, orbit=0.30, ribs=8, rib_frac=0.66, neck=5),
    "hippo": dict(skull=[(0.0, 0.70, 0.80), (0.15, 1.0, 1.0), (0.45, 0.90, 0.88), (0.75, 0.85, 0.80),
                         (1.0, 0.75, 0.70)], leg="column", jaw=0.45, orbit=0.30, ribs=8, rib_frac=0.66, neck=4),
    "sloth": dict(skull=[(0.0, 0.75, 0.80), (0.2, 1.0, 1.0), (0.55, 0.92, 0.92), (0.85, 0.72, 0.75),
                         (1.0, 0.62, 0.62)], jaw=0.4, orbit=0.32, fwd=0.3, ribs=8, rib_frac=0.62, neck=5),
    # the skull about 0.85 of the living head's height (a shallow skull read as a duck's under the horn)
    "thrumbo": dict(skull=[(0.0, 0.75, 0.90), (0.15, 1.0, 1.12), (0.4, 0.95, 1.12), (0.65, 0.78, 1.0),
                           (0.88, 0.66, 0.86), (1.0, 0.58, 0.72)], leg="hoof", jaw=0.36, orbit=0.36, ribs=7,
                    rib_frac=0.62),
    "lizard": dict(skull=[(0.0, 0.75, 0.80), (0.15, 1.0, 1.0), (0.45, 0.85, 0.85), (0.75, 0.62, 0.62),
                          (1.0, 0.48, 0.5)], jaw=0.42, orbit=0.40, fwd=0.1, ribs=7, rib_frac=0.75, neck=4,
                   tail_max=22, tail_extra=3, leg="splay", rib_depth=0.85, spine_r=0.18),
    # frogs have their own body (Skeleton.frog_body): no ribs, no tail, no neck
    "frog": dict(fwd=0.0, ribs_on=False, tail_max=0, leg="splay", spine_r=0.14, leg_r=0.55),
}

# Per-species tuning. Keys: anything in BASE/FAMILY, plus analysis overrides read by analyze.py
# (eyes: one eye (x, y, z) in file axes; head_box: ((lo), (hi)) file-axes box of the head pieces;
# tail: an explicit file-axes path or False).
_BLUNT = [(0.0, 0.72, 0.80), (0.15, 1.0, 1.0), (0.45, 0.95, 0.95), (0.75, 0.85, 0.88), (1.0, 0.8, 0.8)]
_WOOL = dict(trunk_w=0.72)      # fleece and long-haired coats: the ribcage well inside
# the Thrumbos' living trunk is a fur ball: a ribcage well inside it (its belly raised, its back kept, so
# the head still sits on the shoulders), legs to carry it and a short, massive neck
_THRUMBO = dict(leg_r=0.45, neck_r=1.6, neck=4, neck_shoulder=True, trunk_w=0.64, trunk_h=0.75,
                trunk_anchor=1.0, rib_start=0.15)
OVERRIDES = {
    # a bear's skull: the orbits on the tall cranium behind the muzzle's slope, a blunt muzzle and a short
    # heavy jaw tucked under it (the round living head made a bulb with a beak and an open bill)
    "Panda": dict(eyes=(0.11, 0.95, -0.67), orbit_r=0.045, tip=0.88, jaw_end=0.85, jaw_lift=0.15),
    # blunt-headed rodents: a boxy muzzle, not a beak
    "Capybara": dict(skull=_BLUNT, tip=0.9), "GuineaPig": dict(skull=_BLUNT, tip=0.9),
    "Horse": dict(tail_share=0.35),
    "Sheep": _WOOL, "Alpaca": _WOOL, "Muffalo": _WOOL, "Yak": _WOOL,
    "Thrumbo": _THRUMBO, "AlphaThrumbo": _THRUMBO,
    "Bison": dict(hump=0.8),
    "Dromedary": dict(flat_back=True),
    "Porcupine": dict(quills=3),
    "Badger": dict(eyes=(0.06, 0.21, -0.42)),
    "Megasloth": dict(eyes=(0.13, 0.90, -1.18)),
    "Gorilla": dict(head_box=((-0.2, 0.7, -0.6), (0.2, 1.15, -0.25))),
}

SHRINK = 0.85

# Quadruped-only defaults, on top of BASE. The husk and legless plans build their skulls and tusks from BASE
# alone, so the shared Skeleton methods read these with the older behaviour as the fallback.
#   quad         this plan's build (the small build's merged pieces, the hip blade, the sacrum line)
#   socket       "flush": each eye socket is one dark quad of the skull's own surface, between two rings put
#                round the eye (no outline of its own: one clean dark hole); the nasal opening likewise the
#                top facet of the snout's front cap. "lens": the older an_dark lenses standing on the skull
#   nose_min_w   no nasal opening below this bodyHalfWidth (it reads as a living black nose on small heads)
#   tusk_anchor  tusks and sabre teeth (extras whose base lies under the skull's centre line) move in only
#                with the shrink and sink into the skull or the jaw, whichever is nearer, instead of being
#                pushed up into the skull
#   antler_reach antlers and horns reaching further sideways than the ribcage's rest width plus this turn up
#                round their base, so on its side the skeleton props on them instead of one sinking into the
#                ground (Moose)
#   bleach       kept horn colours that are not light and grey enough are taken halfway to bone (dried antler)
#   trunk_w      ribcage, scapula and pelvis widths as a share of the living trunk's (fleece and fur coats less)
#   trunk_h      the trunk's height used for the bones, as a share of the living one, round the point
#                trunk_anchor of the way up (0.5: the middle)
#   rib_start    where the ribcage starts, as a share of the trunk's length from its front
#   pel_cap      the pelvis's length at most this share of the trunk (wide bodies grew a hind-leg-like hip)
#   tail_share   the share of the living tail (from the root) that has bones (a horse's tail is mostly hair)
#   tail_r_min   thinnest tail root radius (medium and large animals; small ones use 0.8 of it)
#   neck_r       neck vertebra radius as a share of the spine's
#   neck_shoulder  the neck ends at the top front of the ribcage at the front hips' depth (shoulder height)
#                instead of at the ribcage's front end (round fur-ball trunks: the Thrumbos)
#   hump         neural spine fin over the withers as a share of the living hump over the spine (Bison)
#   flat_back    the spine runs straight from the withers to the sacrum (a camel's hump has no bone)
#   tip          where the skull's last ring sits along the head (share from the back); the rest is the cap
#                (the head's last slices are slivers, so 0.97 gives a pointed snout)
#   jaw_end      the lower jaw's front end along the skull (1 = the snout tip)
#   jaw_lift     how far the jaw reaches up into the skull, as a share of its depth (closes the mouth line)
#   quills       keep every n-th living quill as a bleached spike (Porcupine), 0 = none
#   leg_r_small  leg radius share of the living hip for the small build (chunkier, so white beats outline)
QUAD = dict(
    quad=True, socket="flush", nose_min_w=0.15, tusk_anchor=True, antler_reach=0.12, bleach=True,
    trunk_w=SHRINK, trunk_h=1.0, pel_cap=0.22, tail_share=1.0, tail_r_min=0.022, neck_r=1.05,
    neck_shoulder=False, hump=0.0, flat_back=False, tip=0.97, quills=0, leg_r_small=0.40, trunk_anchor=0.5,
    rib_start=0.06, jaw_end=1.0, jaw_lift=0.0,
)


def params(name):
    p = dict(BASE)
    p.update(QUAD)
    p.update(FAMILY[SPECIES_FAMILY[name]])
    p.update(OVERRIDES.get(name, {}))
    p["family"] = SPECIES_FAMILY[name]
    return p


def lerp_table(table, t):
    ts = [r[0] for r in table]
    return tuple(float(np.interp(t, ts, [r[k] for r in table])) for k in range(1, len(table[0])))


def is_dark_name(name):
    return name.lower() == "an_dark"


def surface_point(part, target):
    """The point of `part`'s surface nearest `target` on the x >= 0 side (sampled densely per triangle) and the
    outward normal there (parts are wound clockwise from outside, so the outward normal is -cross)."""
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
    d[pts[:, :, 0] < 0] = np.inf
    ti, wi = np.unravel_index(int(np.argmin(d)), d.shape)
    nrm = -np.cross(b[ti] - a[ti], c[ti] - a[ti])
    return pts[ti, wi], P.unit(nrm)


# ---------------------------------------------------------------- the build

def build(animal):
    p = params(animal.name)
    animal.overrides = p
    sk = Skeleton(animal, p)
    out = {"body": sk.body()}
    if animal.leg is not None:
        out["leg"] = sk.leg()
    return out


class Skeleton:
    def __init__(self, animal, p):
        self.a = animal
        self.p = p
        self.an = animal.an
        self.tr = self.an.trunk
        self.hd = self.an.head
        d = animal.d
        self.LL = float(d["legLength"])
        self.W = float(self.tr.half_width)
        self.size = animal.size
        hips = animal.hips_file
        self.zfh = min(h[2] for h in hips)
        self.zrh = max(h[2] for h in hips)
        self.Hf = float(np.mean([abs(h[0]) for h in hips if abs(h[2] - self.zfh) < 1e-6]))
        self.Hr = float(np.mean([abs(h[0]) for h in hips if abs(h[2] - self.zrh) < 1e-6]))
        h = self.tr.top - self.tr.bottom
        self.rs = max(P.MIN_R + 0.002, min(self.p["spine_r"] * self.W, 0.11 * h))
        # build by how big the animal looks, not by width alone: narrow but short-bodied animals (the Guinea
        # pig, Chinchilla, Raccoon, Lynx) get the small build's fewer, chunkier pieces, whose outlines overlap
        # less (the triangle budget stays the def's)
        if self.size == "medium" and float(d["bodyHalfWidth"]) < 0.13 and float(d["bodyHalfLength"]) < 0.4:
            self.size = "small"

    # ---------------- trunk helpers
    def prof(self, z):
        ymin, ymax, hw = self.tr.at(min(max(z, self.tr.z_front + 0.01), self.tr.z_rear - 0.01))
        k = self.p.get("trunk_h", 1.0)
        if k != 1.0:
            c = ymin + self.p.get("trunk_anchor", 0.5) * (ymax - ymin)
            ymin, ymax = c - k * (c - ymin), c + k * (ymax - c)
        return ymin, ymax, hw

    def sacrum_z(self):
        return min(self.zrh + 0.45 * self.pel_len, self.tr.z_rear - 0.02)

    def spine_y(self, z):
        ymin, ymax, hw = self.prof(z)
        y = ymax - max(1.2 * self.rs, 0.10 * (ymax - ymin))
        if not hasattr(self, "zrh"):
            return y
        if self.p.get("flat_back") and self.zfh < z < self.zrh:
            # a straight back from the withers to the hips (Dromedary: the hump is fat)
            y0, y1 = self.spine_y_raw(self.zfh), self.spine_y_raw(self.zrh)
            y = min(y, y0 + (y1 - y0) * (z - self.zfh) / (self.zrh - self.zfh))
        if self.p.get("quad") and z > self.zrh:
            # the sacrum runs on behind the hips instead of bending down over a round rump
            y = max(y, self.spine_y_raw(self.zrh) - 0.3 * (z - self.zrh))
        return y

    def spine_y_raw(self, z):
        ymin, ymax, hw = self.prof(z)
        return ymax - max(1.2 * self.rs, 0.10 * (ymax - ymin))

    def trunk_w(self):
        return self.p.get("trunk_w", SHRINK)

    # ---------------- body
    def body(self):
        if self.p["family"] == "frog":
            return self.frog_body()
        parts = []
        L = self.tr.length
        zf = self.tr.z_front
        p = self.p
        # ribcage range
        self.rib0 = zf + p["rib_start"] * L
        pel_len = p["pelvis_len"] * max(0.9 * self.W, 0.35 * (self.spine_y(self.zrh) - self.LL) + 0.5 * self.W)
        pel_len = min(pel_len, p["pel_cap"] * L)
        self.pel_len = pel_len
        pel_front = self.zrh - 0.55 * pel_len
        self.rib1 = min(self.rib0 + p["rib_frac"] * L, pel_front - 0.04 * L)
        if p["ribs_on"]:
            parts.append(self.ribcage())
            rv = parts[-1].verts
            win = (rv[:, 2] >= self.zfh) & (rv[:, 2] <= self.zrh)
            self.rest_est = float(np.abs(rv[win, 0]).max()) if win.any() else self.W
        parts.append(self.spine())
        if p["hump"] > 0:
            parts.append(self.hump())
        parts += self.pelvis()
        parts += self.scapulae()
        sk = self.skull()
        parts += sk
        parts.append(self.neck())
        t = self.tail()
        if t is not None:
            parts.append(t)
        parts += self.extras()
        if p["quills"]:
            parts += self.quills()
        return [q for q in parts if q is not None and len(q)]

    def frog_body(self):
        """Frogs and toads: no ribs and no tail. A wide flat skull with big orbits on top, a jaw bar round its
        front edge, a short spine with one pair of transverse processes, long ilia from the sacrum back to
        the hip sockets with the urostyle between them, scapulae at the front legs, and bones through the
        living model's thigh and foot pieces (a frog's hind legs are mostly in its body mesh)."""
        hd = self.hd
        tr = self.tr
        LL = self.LL
        legr = self.leg_r()
        parts = []
        z0 = hd.z_front
        Lh = hd.z_back - hd.z_front
        z1 = z0 + 0.62 * Lh
        sl = hd.slices
        rings = []
        prof = [(0.0, 0.40), (0.25, 0.80), (0.55, 0.95), (0.8, 0.92), (1.0, 0.75)]
        for t, f in prof:
            z = z0 + 0.03 * Lh + t * (z1 - z0 - 0.03 * Lh)
            ymin, ymax, hw = sl.at(z)
            h = ymax - ymin
            cy = ymax - 0.40 * h
            rings.append(P.Ring((0.0, cy, z), SHRINK * hw * f, 0.16 * h, 0.12 * h, power=2.6))
        rings = rings[::-1]   # back to front, like the other skulls
        self.skull_rings = rings
        n = len(rings)
        fr = (np.tile([0.0, 0.0, -1.0], (n, 1)), np.tile([1.0, 0.0, 0.0], (n, 1)), np.tile([0.0, 1.0, 0.0], (n, 1)))
        sides = 10
        skull = P.loft(rings, sides, frames=fr, caps=(0.04 * Lh, 0.02 * Lh), name="skull")
        parts.append(skull)
        # orbits: two round lenses on the skull's upper sides where the living eyes bulge, standing a little
        # proud (aimed straight up, they merged into one flat black cap across the skull)
        _, hw_e, _ = self.skull_at(hd.eye_z)
        eye = np.array([max(abs(hd.eye_x), 0.6 * hw_e), hd.eye_y, hd.eye_z])
        q, nq = surface_point(skull, eye)
        if q[0] < 0.55 * hw_e:
            q, nq = surface_point(skull, np.array([0.75 * hw_e, q[1], q[2]]))
        r_o = max(0.015, min(0.3 * hw_e, 0.9 * (q[0] - 0.02)))
        axis = P.unit(nq + np.array([0.0, 0.35, 0.0]))
        hl = max(0.015, 0.5 * r_o)
        for sgn in (-1.0, 1.0):
            c = (q - axis * (0.2 * hl)) * [sgn, 1, 1]
            parts.append(P.lens(c, axis * [sgn, 1, 1], r_o, hl, sides=8, name="socket"))
        # jaw: a bar round the skull's front edge, under it
        jp = []
        for t in (-1.0, -0.6, 0.0, 0.6, 1.0):
            ang = t * math.pi / 2
            z = z0 + 0.08 * Lh + (z1 - z0) * 0.75 * (1 - math.cos(ang))
            ymin, ymax, hw = sl.at(z)
            jp.append((SHRINK * hw * 0.85 * math.sin(ang), rings[0].c[1] - rings[0].bot - 0.01, z))
        jr = max(P.MIN_R, 0.012 + 0.03 * Lh)
        parts.append(P.loft_path(jp, jr, jr * 0.8, sides=6, caps=(0.5 * jr, 0.5 * jr), name="jaw"))
        # spine, sacrum, urostyle, ilia: the sacrum halfway up the trunk, so the ilia run back to the hip
        # sockets at a low slant (from the top of the tall living back they crossed the body as a diagonal)
        zs = z1 + 0.15 * (self.zrh - z1)
        ys = min(self.spine_y(zs), tr.bottom + 0.48 * (tr.top - tr.bottom))
        rs = max(P.MIN_R + 0.002, self.rs)
        y_sk = rings[0].c[1]
        parts.append(P.vertebra_chain([(0.0, y_sk, z1 - 0.02), (0.0, ys, zs)], 3, rs, rs, gap=0.006, sides=6,
                                      name="spine"))
        tw = SHRINK * tr.at(zs)[2] * 0.45
        parts.append(P.tube((-tw, ys, zs), (tw, ys, zs), max(P.MIN_R, 0.7 * rs), sides=6, name="spine"))
        z_back = min(self.zrh + 0.35 * (tr.z_rear - self.zrh), tr.z_rear - 0.02)
        parts.append(P.tube((0.0, ys, zs), (0.0, ys - 0.25 * (ys - LL), z_back), max(P.MIN_R, 0.8 * rs), max(P.MIN_R, 0.5 * rs),
                            sides=6, caps=(0.0, 0.5 * rs), name="spine"))
        for sgn in (-1.0, 1.0):
            a = np.array([sgn * 0.6 * tw, ys, zs])
            b = np.array([sgn * (self.Hr + 0.2 * legr), LL + 0.6 * legr, self.zrh])
            parts.append(P.bone(a, b, max(P.MIN_R, 0.75 * legr), 0.9 * legr, 1.3 * legr, sides=6, name="pelvis"))
        parts += self.scapulae()
        # folded hind legs through the living thigh and foot pieces: femur from the hip socket out toward the
        # thigh's outermost point (knee), shin from there toward its rearmost point (ankle), foot toward the
        # front of the foot piece, all folded in tight along the flank (no further out than the pelvis plus
        # 0.03, so on its side the skeleton neither sinks a leg into the ground nor stands one up), and
        # chunky: a few thick parts read better at this size than more thin ones
        h = tr.top - tr.bottom
        reach = self.Hr + 1.5 * legr + 0.03
        rfloor = max(0.025, 0.07 * self.W)

        def tuck(pt, hip):
            pt = hip + 0.6 * (pt - hip)
            pt[0] = math.copysign(min(abs(pt[0]), reach), pt[0])
            return pt
        for sgn in (-1.0, 1.0):
            side = [c for c in self.an.comps if not c.kind and not c.has(is_dark_name)
                    and c.centre[0] * sgn > 0.35 * self.W and c.centre[2] > self.zfh
                    and c.centre[1] < tr.bottom + 0.6 * h]
            if not side:
                continue
            thigh = max(side, key=lambda c: (c.box_volume, float(c.centre[2])))
            pts = np.unique(np.round(thigh.tris.reshape(-1, 3), 4), axis=0)
            cc = thigh.centre
            hip = np.array([sgn * (self.Hr + 0.2 * legr), LL + 0.5 * legr, self.zrh])
            knee = pts[int(np.argmax(pts[:, 0] * sgn - 0.3 * pts[:, 1]))]
            ankle = pts[int(np.argmax(pts[:, 2] - 0.3 * pts[:, 1]))]
            knee = tuck(cc + (knee - cc) * 0.8, hip)
            ankle = tuck(cc + (ankle - cc) * 0.8, hip)
            ankle[1] = max(ankle[1], 0.5 * (hip[1] + knee[1]) - 0.5 * rfloor)   # tucked under the thigh
            r = max(rfloor, min(0.8 * legr, 0.12 * float(min(thigh.size[1], thigh.size[0]))))
            parts.append(P.bone(hip, knee, r, 1.2 * r, 1.4 * r, sides=6, name="limb"))
            parts.append(P.bone(knee, ankle, r, 1.2 * r, 1.2 * r, sides=6, name="limb"))
            feet = [c for c in side if c is not thigh and c.centre[1] < thigh.centre[1]]
            if feet:
                foot = min(feet, key=lambda c: float(c.centre[1]))
                fp = np.unique(np.round(foot.tris.reshape(-1, 3), 4), axis=0)
                toe = fp[int(np.argmin(fp[:, 2]))]
                toe = tuck(foot.centre + (toe - foot.centre) * 0.85, hip)
                toe[1] = max(toe[1], r)
                parts.append(P.bone(ankle, toe, 0.8 * r, 1.1 * r, r, sides=6, name="limb"))
        return [q for q in parts if q is not None and len(q)]

    def ribcage(self):
        p = self.p
        n = int(p["ribs"])
        if self.size == "small":
            n = min(n, 3 if self.a.size == "small" else 4)   # the small build of a medium def: its budget
            sides = 8
        elif self.size == "medium":
            n = min(n, 6)
            sides = 10
        else:
            sides = 12 if self.W < 0.6 else 14
        z0, z1 = self.rib0, self.rib1
        pitch = (z1 - z0) / (n + 0.6)
        rings = []

        def ring(z, s, bot_raise=0.0):
            ymin, ymax, hw = self.prof(z)
            top = self.spine_y(z) + 0.4 * self.rs
            bot = ymin + (0.10 + bot_raise) * (ymax - ymin)
            bot = min(bot, top - 0.04)
            c = (top + bot) / 2.0
            hh = (top - bot) / 2.0 * p["rib_depth"]
            c = top - hh
            return P.Ring((0.0, c, z), self.trunk_w() * hw * s, hh * s, hh * s, power=2.0)

        small = self.size == "small"
        if not small:
            rings.append(ring(z0, 0.55, 0.25))
        z = z0 + 0.45 * pitch
        for i in range(n):
            f = i / max(1, n - 1)
            raise_ = 0.25 * max(0.0, f - 0.5) * 2  # the last ribs end higher (floating ribs)
            s_main = 0.80 + 0.2 * min(1.0, (i + 1) / 2.0) - 0.08 * max(0.0, f - 0.6) / 0.4
            rings.append(ring(z, s_main * 1.04, raise_))
            rings.append(ring(z + 0.42 * pitch, s_main * 0.95, raise_))
            z += pitch
        rings.append(ring(z1, 0.62, 0.35))
        # bands: front ring -> first ridge (bone), then (ridge -> groove: shade, groove -> next ridge: bone)
        band = [] if small else [BONE]
        for i in range(n):
            band.append(SHADE)
            band.append(BONE)
        band = band[: len(rings) - 1]
        fr = (np.tile([0.0, 0.0, 1.0], (len(rings), 1)), np.tile([1.0, 0.0, 0.0], (len(rings), 1)),
              np.tile([0.0, 1.0, 0.0], (len(rings), 1)))
        ext0 = (0.45 if small else 0.3) * pitch
        ext1 = 0.3 * pitch
        return P.loft(rings, sides, frames=fr, caps=(ext0, ext1), band_mat=band, keep={0, sides // 2},
                      name="ribcage")

    def spine(self):
        """Thoracic and lumbar spine: neck base to the sacrum, under the living back; lumbar vertebra bumps."""
        z0 = self.rib0 - 0.02
        z_end = self.sacrum_z()
        rs = self.rs
        pts, rad = [], []
        # thoracic: a plain rod (mostly inside the ribcage top)
        nth = 2
        for i in range(nth):
            z = z0 + (self.rib1 - z0) * i / (nth - 1)
            pts.append((0.0, self.spine_y(z), z))
            rad.append(rs * (0.9 if i == 0 else 1.0))
        # lumbar: bumps (vertebra bodies and joints)
        zl0, zl1 = self.rib1, z_end
        nl = max(2, int(round((zl1 - zl0) / (3.2 * rs))))
        nl = min(nl, {"small": 1, "medium": 2, "large": 3}[self.size])
        for i in range(nl):
            for f, r in ((0.35, 1.18), (0.85, 0.82)):
                z = zl0 + (zl1 - zl0) * (i + f) / nl
                if z > self.zrh and self.p.get("quad"):
                    r = min(r, 1.0)   # the sacrum stays spine-thick over the rump
                pts.append((0.0, self.spine_y(z), z))
                rad.append(rs * r)
        pts.append((0.0, self.spine_y(zl1), zl1 + 0.3 * rs))
        rad.append(rs * 0.7)
        pts = np.array(pts)
        sides = 6 if rs < 0.06 else 8
        return P.loft_path(pts, rad, rad, sides=sides, caps=(0.6 * rs, 0.8 * rs), name="spine")

    def pelvis(self):
        """Two hip bones (ilium blade from the sacrum down to the hip socket, ischium back from it) on the
        sacrum end of the spine; the sockets sit at hip height over the legs' tops."""
        z = self.zrh
        pl = self.pel_len
        LL = self.LL
        H = self.Hr
        legr = self.leg_r()
        za = z - 0.55 * pl
        ya = self.spine_y(za)
        h = max(ya - LL, 0.05)
        rib_hw = self.trunk_w() * self.prof(za)[2]
        A = np.array([max(self.rs * 1.2, 0.45 * rib_hw), ya + 0.2 * self.rs, za])
        B = np.array([H + 0.3 * legr, LL + 0.55 * legr, z])
        zc = min(z + 0.45 * pl, self.tr.z_rear - 0.03)
        th = max(0.026, 0.55 * legr)
        sides = 6
        parts = []
        if self.p.get("quad"):
            # one slanted hip blade per side, from the sacrum (A) back to the pin bone (C) high under the
            # tail, its middle reaching down over the hip socket (B), so ilium and ischium read as one plate
            # (two rods made a V that looked like a second, folded hind leg on wide bodies)
            C = np.array([max(0.7 * H, self.rs * 1.5), LL + 0.6 * (self.spine_y(zc) - LL), zc])
            if self.size == "small":
                # one block across the hips (fewer outlines on a small animal)
                yb = LL - 0.4 * legr
                wb = H + 0.7 * legr
                spec = [(za, ya + 0.2 * self.rs, 0.5 * (ya + LL), 0.5 * wb),
                        (z, self.spine_y(z) + 0.3 * self.rs, yb, wb),
                        (zc, self.spine_y(zc) + 0.2 * self.rs, C[1] - th, 0.7 * wb)]
                return [self.girdle_block(spec, "pelvis")]
            for sgn in (1.0, -1.0):
                a, b, c = (np.array(q) * [sgn, 1, 1] for q in (A, B, C))
                m = 0.5 * (a + c)
                lo = b - np.array([0.0, 0.9 * legr, 0.0])
                hi = b + 0.6 * (m + np.array([0.0, 0.5 * th, 0.0]) - b)   # the top edge sags at the socket
                mc = 0.5 * (hi + lo)
                pts = np.array([a, mc, c])
                n = P.unit(np.cross(c - a, (0.0, 1.0, 0.0))) * sgn
                T, U, V = P.path_frames(pts, side=n, up=(0.0, 1.0, 0.0))
                half = 0.5 * float(np.linalg.norm(hi - lo))
                rings = [P.Ring(pts[0], th / 2, max(0.20 * h, 1.2 * th)),
                         P.Ring(pts[1], max(th / 2, 1.1 * legr), half, power=2.4),
                         P.Ring(pts[2], th / 2, max(0.10 * h, th))]
                parts.append(P.loft(rings, sides, frames=(T, U, V), caps=(0.4 * th, 0.6 * th), name="pelvis"))
            return parts
        C = np.array([max(0.7 * H, self.rs * 1.5), LL + 0.30 * (self.spine_y(zc) - LL), zc])
        for sgn in (1.0, -1.0):
            pts = np.array([A, B, C]) * [sgn, 1, 1]
            n = P.unit(np.cross(pts[1] - pts[0], pts[2] - pts[1]))
            T, U, V = P.path_frames(pts, side=n, up=(0.0, 1.0, 0.0))
            rings = [P.Ring(pts[0], th / 2, max(0.20 * h, 1.2 * th)),
                     P.Ring(pts[1], 1.25 * legr, 1.25 * legr),
                     P.Ring(pts[2], th / 2, max(0.10 * h, th))]
            if self.size == "small":   # ilium and socket only (budget)
                rings = rings[:2]
                T, U, V = T[:2], U[:2], V[:2]
            parts.append(P.loft(rings, sides, frames=(T, U, V), caps=(0.4 * th, 0.6 * th), name="pelvis"))
        return parts

    def hump(self):
        """A bison's hump is bone: tall neural spines over the withers. A thin fin along the spine from the
        ribcage's front over 40% of it, as tall as `hump` of the living back's rise over the spine (the hump
        pieces included), the spines as bone bands with bone_shade grooves between them."""
        from ..analyze import slice_bounds
        tris = np.concatenate([c.tris for c in self.an.comps
                               if c.kind in ("", "trunk") and not c.has(is_dark_name)
                               and abs(float(c.centre[0])) < 0.5 * self.W])
        z0 = self.rib0
        z1 = self.rib0 + 0.4 * (self.rib1 - self.rib0)
        k = 4
        th = max(P.MIN_R, 0.45 * self.rs)
        rings, band = [], []
        for i in range(2 * k):
            z = z0 + (z1 - z0) * (i // 2 + (0.15 if i % 2 == 0 else 0.85)) / k
            b = slice_bounds(tris, z)
            base = self.spine_y(z) + 0.5 * self.rs
            # tallest just behind the front, shortening toward the back (a sloping hump, not a box)
            f = (0.8, 1.0, 1.0, 0.92, 0.82, 0.7, 0.55, 0.4)[i]
            top = base + max(0.02, ((b[1] if b else base) - base) * self.p["hump"] * f)
            rings.append(P.Ring((0.0, 0.5 * (base + top), z), th, 0.5 * (top - base), power=2.4))
            band.append(BONE if i % 2 == 0 else SHADE)
        fr = (np.tile([0.0, 0.0, 1.0], (len(rings), 1)), np.tile([1.0, 0.0, 0.0], (len(rings), 1)),
              np.tile([0.0, 1.0, 0.0], (len(rings), 1)))
        return P.loft(rings, 6, frames=fr, caps=(0.0, 0.0), band_mat=band[:-1], name="spine")

    def quills(self):
        """A porcupine's quills are keratin and outlast the flesh: every `quills`-th symmetric set of the
        living quill pieces (a single quill on the back's middle or a left-right pair), horn-coloured and
        moved in toward the trunk's middle by the fur's depth, so their roots sit on the spine and ribs."""
        comps = [c for c in self.an.comps if c.has(lambda m: "quill" in m.lower())]
        if not comps:
            return []
        sets = []
        for c in sorted(comps, key=lambda c: (round(float(c.centre[2]), 3), float(c.centre[0]))):
            key = (round(abs(float(c.centre[0])), 2), round(float(c.centre[2]), 2), round(float(c.centre[1]), 2))
            for st in sets:
                if abs(st[0][0] - key[0]) < 0.02 and abs(st[0][1] - key[1]) < 0.02 and abs(st[0][2] - key[2]) < 0.02:
                    st[1].append(c)
                    break
            else:
                sets.append((key, [c]))
        sets.sort(key=lambda st: (st[0][1], st[0][0]))
        out = []
        m = self.a.body
        horn = np.array((0.85, 0.81, 0.69))
        for st in sets[::int(self.p["quills"])]:
            for c in st[1]:
                v = m.tris[c.idx].reshape(-1, 3)
                part = P.orient(P.weld(P.Part(v, np.arange(len(v)).reshape(-1, 3), BONE,
                                              np.tile(horn, (len(c.idx), 1)), name="quill")))
                # the root: the vertex nearest the trunk's centre line
                ymin, ymax, hw = self.prof(float(c.centre[2]))
                cy = 0.5 * (ymin + ymax)
                off = part.verts[:, :2] - np.array([0.0, cy])
                i = int(np.argmin(np.linalg.norm(off, axis=1)))
                k = (self.spine_y(float(c.centre[2])) + 0.5 * self.rs - cy) / max(ymax - cy, 1e-6)
                d = np.array([off[i, 0] * (k - 1.0), off[i, 1] * (k - 1.0), 0.0])
                out.append(P.translate(part, d))
        return out

    def girdle_block(self, spec, name):
        """A solid block across the body (a small animal's pelvis or shoulder girdle): a loft along z through
        (z, top y, bottom y, lower half-width) per ring, egg-shaped (narrow at the top over the spine, wide
        at the bottom over the legs' tops)."""
        rings = []
        taper = 0.45
        for z, top, bot, wb in spec:
            bot = min(bot, top - 0.02)
            c = 0.5 * (top + bot)
            hh = 0.5 * (top - bot)
            rings.append(P.Ring((0.0, c, z), max(wb / (1.0 + 0.5 * taper), P.MIN_R), hh, hh, power=2.4, taper=taper))
        n = len(rings)
        fr = (np.tile([0.0, 0.0, 1.0], (n, 1)), np.tile([1.0, 0.0, 0.0], (n, 1)), np.tile([0.0, 1.0, 0.0], (n, 1)))
        ext = 0.25 * (spec[-1][0] - spec[0][0])
        return P.loft(rings, 6, frames=fr, caps=(0.3 * ext, 0.3 * ext), name=name)

    def scapulae(self):
        z = self.zfh
        ys = self.spine_y(z)
        LL = self.LL
        H = self.Hf
        legr = self.leg_r()
        ymin, ymax, hw = self.prof(z)
        rib_hw = self.trunk_w() * hw
        h = max(ys - LL, 0.05)
        th = max(0.026, 0.5 * legr)
        top = np.array([rib_hw * 0.55 + th / 2, ys - 0.08 * h, z + 0.25 * h * self.p["scap"]])
        bot = np.array([H + 0.25 * legr, LL + 0.5 * legr, z])
        wtop = max(0.40 * h, 0.05)
        sides = 4 if self.size == "small" else 6
        if self.size == "small" and self.p.get("quad"):
            # one shoulder block across the chest, from under the spine down over the front legs' tops
            yb = LL - 0.4 * legr
            wb = H + 0.6 * legr
            d = max(1.5 * legr, 0.12 * h)
            spec = [(z - d, ys - 0.15 * h, 0.5 * (ys + LL), 0.8 * wb),
                    (z + 0.5 * d, ys - 0.05 * h, yb, wb),
                    (z + 0.25 * h * self.p["scap"] + d, ys - 0.1 * h, 0.5 * (ys + LL), 0.8 * wb)]
            return [self.girdle_block(spec, "scapula")]
        parts = []
        for sgn in (1.0, -1.0):
            pts = np.array([top, bot]) * [sgn, 1, 1]
            n = P.unit(np.cross(pts[1] - pts[0], (0.0, 0.0, 1.0)))
            T, U, V = P.path_frames(pts, side=n, up=(0.0, 0.0, -1.0))
            rings = [P.Ring(pts[0], th / 2, wtop / 2), P.Ring(pts[1], max(th / 2, 1.0 * legr), 1.15 * legr)]
            parts.append(P.loft(rings, sides, frames=(T, U, V), caps=(0.0, 0.6 * legr), name="scapula"))
        return parts

    # ---------------- skull
    def skull(self):
        hd = self.hd
        p = self.p
        zb, zf = hd.z_back, hd.z_front
        Lh = zb - zf
        # rings from the back of the head to the snout tip
        n = {"small": 5, "medium": 7, "large": 8}[self.size]
        ts = list(np.linspace(0.0, 1.0, n))
        sl = hd.slices
        self.skull_rings = []
        tip = p.get("tip", 0.97)
        span = (tip - 0.03) * Lh
        merged = self.size == "small" and p.get("quad")   # small build: the jaw is part of the skull loft
        flush = p.get("socket") == "flush"

        def ring_at(t):
            z = zb - 0.03 * Lh - t * span
            ymin, ymax, hw = sl.at(z)
            fw, fh = lerp_table(p["skull"], t)
            hh = 0.5 * (ymax - ymin) * SHRINK
            c = 0.5 * (ymax + ymin)
            # the jaw takes the lower part of the head; the skull keeps the top
            jaw = p["jaw"] * min(1.0, 0.4 + t)
            top = c + hh * fh
            bot = c - hh * fh
            skull_bot = bot + (top - bot) * jaw * 0.85
            if merged:
                skull_bot = bot + 0.25 * (skull_bot - bot)
            ring = P.Ring((0.0, (top + skull_bot) / 2, z), SHRINK * hw * fw, (top - skull_bot) / 2,
                          (top - skull_bot) / 2, power=2.4)
            return ring, (z, bot, skull_bot, SHRINK * hw * fw)

        # flush sockets: a pair of rings round the eye, so one quad of the skull's own surface can be the
        # orbit (dark facets of the skull itself have no outline of their own: one clean dark hole, where a
        # separate lens on a small, curved skull showed its outline as a dark arc round a pale ring)
        eye_band = None
        if flush:
            eye = np.array(p.get("eyes", (abs(hd.eye_x), hd.eye_y, hd.eye_z)), dtype=np.float64)
            eye[0] = abs(eye[0])
            te = (zb - 0.03 * Lh - eye[2]) / span
            te = min(max(te, 0.08), 0.92)
            re, _ = ring_at(te)
            r = p["orbit"] * min(re.hw, 1.3 * re.top)
            r = max(P.MIN_R * 1.5, min(r, 0.7 * re.top, 0.9 * re.hw))
            r = p.get("orbit_r", r)
            dt = r / span
            ta, tb = max(te - dt, 0.04), min(te + dt, 0.96)
            ts = [t for t in ts if t in (0.0, 1.0) or t < ta - 0.4 * dt or t > tb + 0.4 * dt]
            ts = sorted(ts + [ta, tb])
            eye_band = (ts.index(ta), eye)
        rings, jaw_rings = [], []
        for t in ts:
            ring, jr_ = ring_at(t)
            rings.append(ring)
            jaw_rings.append(jr_)
        n = len(rings)
        self.skull_rings = rings
        wide = self.size == "medium" or (p.get("quad") and self.a.size != "small")   # small build, medium def
        sides = 12 if self.size == "large" else (10 if wide else 8)
        fr = (np.tile([0.0, 0.0, -1.0], (n, 1)), np.tile([1.0, 0.0, 0.0], (n, 1)), np.tile([0.0, 1.0, 0.0], (n, 1)))
        cap_back = 0.12 * Lh
        cap_front = (1.0 - tip) * Lh
        if tip < 0.97:   # a blunt muzzle: a low cap, not a cone to the head's front
            cap_front = min(cap_front, 0.6 * max(rings[-1].hw, rings[-1].top))
        skull = P.loft(rings, sides, frames=fr, caps=(cap_back, cap_front), name="skull")
        if eye_band is not None:
            # the quad of the band between the eye rings whose middle is nearest the living eye, and its mirror
            ia, eye = eye_band
            V = skull.verts
            best, kb = None, 0
            for k in range(sides):
                c = V[[ia * sides + k, ia * sides + (k + 1) % sides, (ia + 1) * sides + k,
                       (ia + 1) * sides + (k + 1) % sides]].mean(axis=0)
                d = float(np.linalg.norm(c - eye))
                if best is None or d < best:
                    best, kb = d, k
            for k in (kb, (sides - kb) % sides):   # sector k is centred at pi/2 + 2 pi k / sides
                skull.mat[2 * (ia * sides + k)] = P.DARK
                skull.mat[2 * (ia * sides + k) + 1] = P.DARK
        self.skull_part = skull
        self.jaw_part = None
        parts = [skull]
        # lower jaw: from below the back third to the chin (the rings of the original spacing)
        jr = []
        if merged:
            jaw_rings = []
        orig = list(np.linspace(0.0, 1.0, {"small": 5, "medium": 7, "large": 8}[self.size]))
        for i, (z, bot, sb, hw) in enumerate(jaw_rings):
            t = ts[i]
            if t not in orig:
                continue
            j = orig.index(t)
            if t < 0.18 or (self.size == "small" and 0.3 < t < 0.7) or (self.size != "small" and j % 2 == 1 and 0.2 < t < 0.95):
                continue
            if t > p.get("jaw_end", 1.0) + 1e-6:
                continue
            depth = max(sb - bot, 0.024)
            jhw = hw * (0.78 - 0.25 * t)
            lift = p.get("jaw_lift", 0.0) * depth
            jr.append(P.Ring((0.0, bot + depth * 0.5 - 0.004 + lift, z), max(jhw, 0.014), depth * 0.5, depth * 0.5,
                             power=2.2))
        if len(jr) >= 2:
            m = len(jr)
            frj = (np.tile([0.0, 0.0, -1.0], (m, 1)), np.tile([1.0, 0.0, 0.0], (m, 1)),
                   np.tile([0.0, 1.0, 0.0], (m, 1)))
            jsides = 8 if self.size == "large" else 6
            self.jaw_part = P.loft(jr, jsides, frames=frj, caps=(0.6 * jr[0].top, 0.4 * jr[-1].top),
                                   band_mat=BONE, name="jaw")
            parts.append(self.jaw_part)
        # sockets
        if not flush:
            parts += self.sockets(skull, rings)
        # nasal opening at the snout tip (too small to read on small animals)
        if self.size == "small":
            return parts
        tip = rings[-1]
        if flush:
            # the top facet of the snout's front cap, dark: a nasal opening flush with the bone (a lens
            # standing out past the tip, with its outline, read as a living animal's black nose); none on
            # narrow animals
            if float(self.a.d["bodyHalfWidth"]) >= p.get("nose_min_w", 0.0):
                skull.mat[2 * (n - 1) * sides + sides] = P.DARK
            return parts
        nr = max(0.013, p["nose"] * min(tip.hw, tip.top))
        parts.append(P.lens(tip.c + np.array([0.0, 0.25 * tip.top, -0.03 * Lh * 0.3]), (0.0, 0.0, -1.0),
                            nr, max(0.012, 0.5 * nr), sides=8 if nr > 0.025 else 6, name="nose"))
        return parts

    def skull_at(self, z):
        """(centre y, half-width, half-height top) of the skull loft at z (interpolated)."""
        rs = self.skull_rings
        zs = [r.c[2] for r in rs]
        order = np.argsort(zs)
        zs = np.array(zs)[order]
        cy = np.interp(z, zs, [rs[i].c[1] for i in order])
        hw = np.interp(z, zs, [rs[i].hw for i in order])
        ht = np.interp(z, zs, [rs[i].top for i in order])
        return float(cy), float(hw), float(ht)

    def sockets(self, skull, rings, eye=None, size=None):
        """Two an_dark lenses on the skull's surface where it comes nearest the living eyes, facing out along
        the surface (turned toward the front by the family's `fwd`), standing a little proud."""
        hd = self.hd
        p = self.p
        out = []
        eye = np.array([abs(hd.eye_x), hd.eye_y, hd.eye_z]) if eye is None else np.asarray(eye, dtype=np.float64)
        q, n = surface_point(skull, eye)
        cy, hw, ht = self.skull_at(q[2])
        # the outline adds 0.03 of dark round a socket, so small skulls get smaller sockets
        r = (p["orbit"] * min(hw, 1.3 * ht) - 0.012) if size is None else size
        r = max(P.MIN_R, min(r, 0.7 * ht, 0.9 * hw))
        nrm = P.unit(n * (1.0 - p["fwd"]) + np.array([0.0, 0.0, -1.0]) * p["fwd"] + np.array([0.0, 0.1, 0.0]))
        for sgn in (-1.0, 1.0):
            s_ = q * [sgn, 1, 1]
            nn = nrm * [sgn, 1, 1]
            hl = max(0.012, 0.45 * r)
            c = s_ - nn * (0.35 * hl) + nn * 0.006
            out.append(P.lens(c, nn, r, hl, sides=8 if (r > 0.03 and self.size == "large") else 6, name="socket"))
        return out

    # ---------------- neck
    def neck(self):
        hd = self.hd
        rb = self.skull_rings[0]
        # occiput: the back of the skull, a little under its middle
        start = np.array([0.0, rb.c[1] - 0.15 * rb.top, rb.c[2] - 0.05 * rb.top])
        z_end = self.rib0
        end = np.array([0.0, self.spine_y(z_end) + 0.2 * self.rs, z_end])
        if self.p.get("neck_shoulder"):
            # the head sits on the shoulders (a round fur-ball trunk curves down in front, so the ribcage's
            # front end is low): a short neck down to the top of the trunk under the back of the skull
            zs = min(max(rb.c[2] + 0.5 * (self.zfh - rb.c[2]), self.rib0), self.zfh)
            end = np.array([0.0, self.spine_y(zs) + 0.9 * self.rs, zs])
        nc = self.an.neck_comps()
        if nc:
            pts = np.concatenate([c.tris.reshape(-1, 3) for c in nc])
            mid = pts.mean(axis=0)
            mid[0] = 0.0
            # keep the bend modest: halfway between the straight line and the living neck's middle
            straight = (start + end) / 2
            ctrl = straight + 0.6 * (mid - straight)
            ctrl[1] = max(ctrl[1], min(start[1], end[1]))
        else:
            ctrl = (start + end) / 2
        path = [start * (1 - t) ** 2 + 2 * ctrl * (1 - t) * t + end * t ** 2 for t in np.linspace(0, 1, 9)]
        L = P.path_length(path)
        n = int(self.p["neck"])
        rn = self.rs * self.p.get("neck_r", 1.05)
        if self.size == "small" and self.p.get("quad"):
            # one piece: separate tiny vertebrae are mostly outline at this size
            return P.loft_path(P.resample(path, 3), rn * 1.1, rn * 1.1, sides=6, caps=(0.3 * rn, 0.3 * rn),
                               name="neck")
        n = min(n, {"small": 2, "medium": 4, "large": 6}[self.size])
        n = max(2, min(n, int(L / (1.3 * rn + 0.008)) + (1 if self.p.get("neck_shoulder") else 0)))
        gap = min(0.012, 0.15 * L / n)
        return P.vertebra_chain(path, n, rn, rn * 1.05, gap=gap, sides=6, name="neck")

    # ---------------- tail
    def tail(self):
        tl = self.an.tail
        if tl is None or self.p["tail_max"] <= 0:
            return None
        path = np.array(tl.path, dtype=np.float64)
        # start inside the sacrum end of the spine
        z_end = self.sacrum_z()
        root = np.array([0.0, self.spine_y(z_end), z_end])
        if np.linalg.norm(path[0] - root) > 0.02:
            path = np.vstack([root[None, :], path])
        L = P.path_length(path)
        if L < 0.04:
            return None
        share = self.p["tail_share"]
        if share < 1.0:
            # only the root part of the tail has bones (a horse's dock; the rest is hair)
            keep = share * L
            seg = np.linalg.norm(np.diff(path, axis=0), axis=1)
            s_ = np.concatenate([[0.0], np.cumsum(seg)])
            j = int(np.searchsorted(s_, keep, side="right"))
            f = (keep - s_[j - 1]) / max(seg[j - 1], 1e-9)
            path = np.vstack([path[:j], path[j - 1] + (path[j] - path[j - 1]) * f])
            L = keep
        # thick enough that the bone, not its outline, shows (the outline is 0.03 a side), following a thick
        # living tail further; a gentle taper
        rmin = self.p["tail_r_min"] * (0.8 if self.size == "small" else 1.0)
        r0 = max(rmin, min(0.75 * self.rs, 0.8 * tl.radius + 0.006), min(0.45 * tl.radius, 1.2 * self.rs))
        r1 = max(0.016 if self.size != "small" else P.MIN_R + 0.002, 0.65 * r0)
        path[:, 1] = np.maximum(path[:, 1], r0 + 0.004)   # a tail lying on the ground stays above it
        cap = {"small": 3, "medium": 6, "large": 5}[self.size] + (self.p["tail_extra"] // (3 if self.size == "small" else 1))
        if self.size == "small" and L > 0.8 * self.tr.length:
            cap = max(cap, 4)   # a long small tail (the Squirrel's): 8 rings bend with the path
        n = int(min(self.p["tail_max"], cap, max(2, round(L / (3.0 * r0 + 0.012)))))
        return self.tail_loft(path, n, r0, r1)

    def tail_loft(self, path, n, r0, r1):
        """The tail as one loft along the path: n vertebrae of bone with short bone_shade joints between them
        (separate pieces left gaps, and the outline round each piece made the tail a dark dashed line)."""
        path = [np.asarray(q, dtype=np.float64) for q in path]
        # a living tail's traced path can double back (a tuft; the Yak's turns 150 degrees), and one loft
        # folds over itself there: drop the sharpest corner while any turns more than 75 degrees, then round
        # the rest off (two Chaikin passes, the ends kept)
        while len(path) > 2:
            turns = []
            for i in range(1, len(path) - 1):
                a, b = path[i] - path[i - 1], path[i + 1] - path[i]
                c = float(a @ b) / max(float(np.linalg.norm(a) * np.linalg.norm(b)), 1e-12)
                turns.append(math.degrees(math.acos(max(-1.0, min(1.0, c)))))
            j = int(np.argmax(turns))
            if turns[j] <= 75.0:
                break
            del path[j + 1]
        for _ in range(2):
            sm = [path[0]]
            for a, b in zip(path[:-1], path[1:]):
                sm += [0.75 * a + 0.25 * b, 0.25 * a + 0.75 * b]
            sm.append(path[-1])
            path = sm[:1] + sm[2:-2] + sm[-1:] if len(sm) > 4 else sm
        path = np.array(path)
        seg = np.linalg.norm(np.diff(path, axis=0), axis=1)
        s_ = np.concatenate([[0.0], np.cumsum(seg)])
        L = s_[-1]

        def at(f):
            t = f * L
            j = min(max(int(np.searchsorted(s_, t, side="right")) - 1, 0), len(seg) - 1)
            g = 0.0 if seg[j] < 1e-12 else (t - s_[j]) / seg[j]
            return path[j] + (path[j + 1] - path[j]) * g

        rings, band = [], []
        for i in range(n):
            for f in (i + 0.08, i + 0.92):
                r = r0 + (r1 - r0) * f / n
                rings.append(P.Ring(at(f / n), r, r))
            band += [BONE, SHADE]
        return P.loft(rings, 6, caps=(0.08 * L / n + 0.3 * r0, 0.08 * L / n + 0.5 * r1), band_mat=band[:-1],
                      name="tail")

    # ---------------- horns, antlers, tusks
    def extras(self):
        """The living horn / antler / tusk / tooth pieces as they are (closed shells already), keeping their
        colour when it reads as horn or ivory (light and unsaturated, like an_horn) and bone otherwise.
        Touching pieces (an antler's beam, palm and tines) move together: first in with the skull's 0.85
        shrink at the vertex nearest the head's axis (the base), then, if that base is still off the skull's
        surface, along the surface normal until it sinks 0.01 into the skull. Tusks and sabre teeth (tusk_anchor)
        and wide antlers (antler_reach) are placed as described at QUAD."""
        sl = self.hd.slices
        comps = self.an.extras()
        # clusters of touching pieces
        parent = list(range(len(comps)))

        def find(a):
            while parent[a] != a:
                parent[a] = parent[parent[a]]
                a = parent[a]
            return a

        for i in range(len(comps)):
            for j in range(i + 1, len(comps)):
                a, b = comps[i], comps[j]
                if np.all(a.lo <= b.hi + 0.02) and np.all(b.lo <= a.hi + 0.02):
                    ri, rj = find(i), find(j)
                    if ri != rj:
                        parent[rj] = ri
        groups = {}
        for i in range(len(comps)):
            groups.setdefault(find(i), []).append(i)
        out = []
        m = self.a.body
        skull = getattr(self, "skull_part", None)
        for key in sorted(groups):
            parts = []
            for i in groups[key]:
                idx = comps[i].idx
                col = m.color[idx]
                lum = col.mean(axis=1)
                sat = col.max(axis=1) - col.min(axis=1)
                keep = (lum > 0.6) & (sat < 0.3)
                cc = np.where(keep[:, None], col, np.array(P.BONE_RGB)[None, :])
                if self.p.get("bleach"):
                    # dried antler is bone: a darker or warmer kept colour (the Moose's tan, the Muskox's
                    # horn) goes halfway to bone; light grey horn and ivory (an_horn, tusks) stay as they are
                    pale = (lum > 0.7) & (sat < 0.18)
                    cc = np.where((keep & ~pale)[:, None], 0.5 * (col + np.array(P.BONE_RGB)[None, :]), cc)
                part = P.Part(m.tris[idx].reshape(-1, 3), np.arange(len(idx) * 3).reshape(-1, 3), BONE, cc)
                parts.append(P.orient(P.weld(part)))
            v = np.concatenate([q.verts for q in parts])
            d = np.zeros(3)
            if len(sl):
                cy = 0.5 * (np.interp(v[:, 2], sl.z, sl.ymin) + np.interp(v[:, 2], sl.z, sl.ymax))
                off = np.stack([v[:, 0], v[:, 1] - cy], axis=1)
                i = int(np.argmin(np.linalg.norm(off, axis=1)))
                base = v[i].copy()
                d = np.array([off[i, 0] * (SHRINK - 1.0), off[i, 1] * (SHRINK - 1.0), 0.0])
                if skull is not None:
                    b = base + d
                    sgn = -1.0 if b[0] < 0 else 1.0
                    low = self.p.get("tusk_anchor") and b[1] < self.skull_at(b[2])[0]
                    if low:
                        # a tusk or sabre tooth (base under the skull's centre line): only the point of it
                        # nearest the skull or the jaw sinks 0.01 into that bone, so it still pokes out of
                        # the mouth (pushing its base along the skull's normal buried a boar's short low
                        # tusk in the upper skull)
                        jaw = getattr(self, "jaw_part", None)
                        best = None
                        w = np.unique(np.round(v + d, 4), axis=0)
                        for bone_part in [skull] + ([jaw] if jaw is not None else []):
                            for pt in w:
                                sg = -1.0 if pt[0] < 0 else 1.0
                                q, n = surface_point(bone_part, pt * [sg, 1, 1])
                                gap = float((pt * [sg, 1, 1] - q) @ n)
                                if best is None or gap < best[0]:
                                    best = (gap, n * [sg, 1, 1])
                        gap, n = best
                    else:
                        q, n = surface_point(skull, b * [sgn, 1, 1])
                        q, n = q * [sgn, 1, 1], n * [sgn, 1, 1]
                        gap = float((b - q) @ n)
                    if gap > -0.01:
                        d = d - (gap + 0.01) * n
                    if not low and self.p.get("antler_reach") is not None and hasattr(self, "rest_est"):
                        parts = self.antler_turn(parts, base + d, d)
            out += [P.translate(q, d) for q in parts]
        return out

    def antler_turn(self, parts, pivot, d):
        """Antlers or horns reaching further sideways than the ribcage's rest width plus antler_reach turn up
        round their base (about the file z axis) in 3 degree steps until they don't: on its side the skeleton
        lies on its ribcage, so a wide antler sank deep into the ground and the other stood up as a sail;
        turned up, both lie nearly flat. They turn up only while their top stays within the living model's
        (the Moose's palms are its top already), then sweep back round the vertical for the rest."""
        v = np.concatenate([q.verts for q in parts]) + d
        lim = self.rest_est + self.p["antler_reach"]
        if float(np.abs(v[:, 0]).max()) <= lim:
            return parts
        ytop = float(self.a.body.bounds()[1][1]) + 0.04
        sgn = 1.0 if float(v[:, 0].mean()) > 0 else -1.0
        pv = np.asarray(pivot, dtype=np.float64)

        def turned(w, up, back):
            w = w - pv
            a, b = math.radians(up) * sgn, math.radians(back) * sgn
            x = w[:, 0] * math.cos(a) - w[:, 1] * math.sin(a)
            y = w[:, 0] * math.sin(a) + w[:, 1] * math.cos(a)
            z = w[:, 2]
            x, z = x * math.cos(b) - z * math.sin(b), x * math.sin(b) + z * math.cos(b)
            return np.stack([x, y, z], axis=1) + pv

        kup = 0
        while kup < 29 and float(turned(v, 3.0 * (kup + 1), 0.0)[:, 1].max()) <= ytop:
            kup += 1
        up, back = 0.0, 0.0
        for step in range(1, 30):
            up = 3.0 * min(step, kup)
            back = 3.0 * (step - min(step, kup))
            if float(np.abs(turned(v, up, back)[:, 0]).max()) <= lim:
                break
        return [P.Part(turned(q.verts + d, up, back) - d, q.tris, q.mat, q.col, q.name) for q in parts]

    # ---------------- leg
    def leg_r(self):
        li = self.a.leg_info
        hip = li.hip_r if li else 0.05
        k = self.p["leg_r"]
        if self.size == "small" and "leg_r_small" in self.p:
            k = max(k, self.p["leg_r_small"])
        return max(P.MIN_R, k * hip)

    def leg(self):
        """One bone leg, hip at the top (y = legLength, the pole reaches 0.025 above to overlap the girdle),
        centred on x = z = 0 like the living leg: hip knob, thigh, knee knob, shin, ankle, then
          hoof / column: a flat foot ring on the ground under the ankle;
          paw / splay: the foot bends forward to a flat toe ring (heel up, toes on the ground) and the end
          pole reaches the living toe tip (further for claws)."""
        li = self.a.leg_info
        LL = self.LL
        r = self.leg_r()
        style = self.p["leg"]
        knob = max(P.MIN_R * 1.3, 1.45 * r)
        knee = 0.52 * LL
        toe = li.toe_z if li else -0.05
        down = ((0.0, -1.0, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, -1.0))   # T, U, V of a horizontal ring
        fwd = ((0.0, 0.0, -1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0))     # a ring facing forward
        rings, frames = [], []

        def add(y, hw, ht=None, z=0.0, frame=down):
            rings.append(P.Ring((0.0, y, z), hw, hw if ht is None else ht, hw if ht is None else ht))
            frames.append(frame)

        add(LL - 0.6 * knob, knob)
        if self.size != "small":
            add(LL - 0.28 * LL, r)
        add(knee + 0.35 * knob, 0.9 * r)
        add(knee, 1.3 * r)
        if style in ("hoof", "column"):
            ankle = (li.hoof_top * 1.5 if (li and li.hoof_top) else 0.14 * LL)
            ankle = min(max(ankle, 0.08 * LL, 2.2 * r), 0.3 * LL)
            add(ankle, 1.05 * r)
            fw = (0.75 * li.foot_hw) if (style == "column" and li) else 1.15 * r
            add(0.0, max(fw, 1.15 * r), z=0.12 * toe)
            caps = (0.025 + 0.6 * knob, 0.0)
        else:
            ankle = min(max(0.16 * LL, 2.5 * r), 0.35 * LL)
            add(ankle, 1.0 * r)
            fw = max(1.3 * r, 0.55 * (li.foot_hw if li else r))
            th = max(P.MIN_R, 0.55 * r)
            reach = abs(toe) if self.p["leg"] != "splay" else 1.15 * abs(toe)
            add(th, fw, th, z=-0.5 * reach, frame=fwd)
            caps = (0.025 + 0.6 * knob, 0.5 * reach)
        sides = 6 if self.size != "large" else 8
        n = len(rings)
        T = np.array([f[0] for f in frames], dtype=np.float64)
        U = np.array([f[1] for f in frames], dtype=np.float64)
        V = np.array([f[2] for f in frames], dtype=np.float64)
        return [P.loft(rings, sides, frames=(T, U, V), caps=caps, name="leg")]
