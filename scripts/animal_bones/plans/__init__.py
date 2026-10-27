"""Body plans for the animal skeleton generator (Multiplayer port, not shipped).

Every module in this package is a plan. scripts/make_animal_skeletons.py imports them all (sorted by module
name), so adding a plan means adding a module here; no other file changes. A plan module exposes:

    SPECIES = {"Cow", "Pig", ...}        # thingDef names (Defs/Models.xml <thingDef>) this plan owns
    def build(animal) -> dict            # parts per output file

build() returns a dict with any of the keys
    "body"  the bones body   -> written as Models/<body stem>_bones.obj
    "leg"   the bones leg    -> Models/<leg stem>_bones.obj   (only when the def has a <legModel>)
    "wing"  the bones wing   -> Models/<wing stem>_bones.obj  (only when the def has a <wingModel>)
Each value is a prims.Part or a list of prims.Part (written in order, one shared-vertex block per Part). A
missing key writes no file (the game then draws no legs / no wings for the bones, see the spec). Each Part
must be made of closed shells wound clockwise from outside in FILE axes (negative signed volume); every
primitive in prims.py already is, and prims.orient() fixes a shell copied from elsewhere. Materials: "bone",
"bone_shade", "an_dark", or "bone" plus a per-triangle colour (Part.col) for kept living colours.

A species claimed by two plans is an error; a species no plan claims is skipped (and listed).

The `animal` argument (class Animal below):
    animal.name          thingDef, e.g. "Cow"
    animal.d             the ModelDef fields (dict, see read_defs): legs (GAME axes), legLength, legSplay,
                         bodyCentre, bodyHalfWidth, bodyHalfLength, deadBellyUp, rotation, wings, ...
    animal.body          objio.Mesh of the living body, FILE axes (faces -z)
    animal.leg           objio.Mesh of the living leg (hip at y = legLength, centred on x = z = 0) or None
    animal.wing          objio.Mesh of the living wing or None
    animal.hips_file     hip positions in FILE axes: (x, y, -z_game)
    animal.size          "small" (bodyHalfWidth < 0.1), "medium" (< 0.3) or "large"
    animal.budget_body   (lo, hi) triangle budget for the body; animal.budget_leg likewise
    animal.an            analyze.Analysis (lazy): .eyes, .head, .trunk, .tail, .neck_comps(), .extras(), .comps
    animal.leg_info      analyze.LegInfo of the living leg (or None)
    animal.override(key, default=None)   the plan's per-species override table entry, if the plan set
                         animal.overrides (a dict) before asking the analysis

Paths: animal.model_rel ("Models/animal_cow.obj"), animal.model_path (absolute), animal.leg_rel, ...;
animal.out_names() gives {"body": "animal_cow_bones.obj", "leg": ..., "wing": ...}.

What the analysis gives (all FILE axes; see analyze.py):
    an.eyes              (left, right) eye centres, or None (overridable: overrides["eyes"] = (x, y, z))
    an.head              HeadInfo: .parts (comps), .lo/.hi, .z_front/.z_back, .slices (Slices: .at(z) ->
                         (ymin, ymax, half-width)), .eye_x/.eye_y/.eye_z (estimated when there are no eyes)
                         (overridable: overrides["head_box"] = ((lo), (hi)))
    an.trunk             TrunkInfo: .z_front/.z_rear/.length, .half_width, .top/.bottom, .at(z)
    an.tail              TailInfo (.path root first, .radius) or None (overrides["tail"] = path or False)
    an.extras()          horn / antler / tusk / tooth / casque comps (Comp: .idx triangle indices into
                         animal.body, .tris, .lo/.hi/.centre/.size, .mats)
    an.comps             every connected piece of the living body, with .kind "", "dark", "extra", "tail",
                         "head" or "trunk" once those are computed
    animal.leg_info      LegInfo: .levels (y, cx, cz, half x, half z), .hip_r, .shin_r, .hoof_top, .toe_z

Reusing the quadruped builder: quadruped.Skeleton(animal, p) with p = dict(quadruped.BASE, family="x",
...your keys) offers skull(), neck(), spine(), ribcage(), pelvis(), scapulae(), tail(), extras() and leg()
(body() sets rib0/rib1/pel_len first; call it or set those yourself). The validator checks Parts named
"pelvis" and "scapula" reach hip height, so name such parts that way (Part.name) when you make your own.
Primitives (prims.py): loft / loft_path (rings with ridges and shade bands), tube, lens (eye sockets), ellipsoid,
plate, box, bone (knobbed long bone), vertebra_chain, plus merge, transform, mirror_x, orient, weld,
signed_volume, open_edges, shells, sides_for (the spec's side count).
"""

import importlib
import pkgutil
import xml.etree.ElementTree as ET
from pathlib import Path

from .. import analyze, objio


def _vec(s):
    s = s.strip().strip("()")
    return tuple(float(v) for v in s.split(","))


def read_defs(defs_path):
    """The active (not commented out) animal ModelDefs of Models.xml, in file order."""
    root = ET.parse(defs_path).getroot()
    out = []
    for e in root:
        if (e.findtext("animal") or "").strip().lower() != "true":
            continue

        def f(tag, default):
            v = e.findtext(tag)
            return float(v) if v is not None and v.strip() else default

        def lst(tag, conv):
            node = e.find(tag)
            return [conv(li.text) for li in node.findall("li")] if node is not None else []

        def flag(tag):
            return (e.findtext(tag) or "").strip().lower() == "true"

        out.append({
            "defName": (e.findtext("defName") or "").strip(),
            "thingDef": (e.findtext("thingDef") or "").strip(),
            "model": (e.findtext("model") or "").strip(),
            "legModel": (e.findtext("legModel") or "").strip() or None,
            "wingModel": (e.findtext("wingModel") or "").strip() or None,
            "rotation": f("rotation", 0.0),
            "scale": f("scale", 1.0),
            "legs": lst("legs", _vec),
            "legLength": f("legLength", 0.0),
            "legSwing": f("legSwing", 26.0),
            "legSplay": f("legSplay", 0.0),
            "legFromHip": flag("legFromHip"),
            "legYaw": lst("legYaw", float),
            "legLift": f("legLift", 0.0),
            "deadBellyUp": flag("deadBellyUp"),
            "wings": lst("wings", _vec),
            "wingFold": f("wingFold", 90.0),
            "wingDroop": f("wingDroop", 0.0),
            "bodyCentre": f("bodyCentre", 0.0),
            "bodyHalfWidth": f("bodyHalfWidth", 0.2),
            "bodyHalfLength": f("bodyHalfLength", 0.4),
        })
    return out


class Animal:
    def __init__(self, d, isorim_dir):
        self.d = d
        self.name = d["thingDef"]
        self.root = Path(isorim_dir)
        self.model_rel = d["model"]
        self.leg_rel = d["legModel"]
        self.wing_rel = d["wingModel"]
        self.model_path = self.root / self.model_rel
        self.leg_path = self.root / self.leg_rel if self.leg_rel else None
        self.wing_path = self.root / self.wing_rel if self.wing_rel else None
        self.body = objio.read_obj(self.model_path)
        self.leg = objio.read_obj(self.leg_path) if self.leg_path and self.leg_path.exists() else None
        self.wing = objio.read_obj(self.wing_path) if self.wing_path and self.wing_path.exists() else None
        self.hips_file = [(float(h[0]), float(h[1]), -float(h[2])) for h in d["legs"]]
        W = d["bodyHalfWidth"]
        self.size = "small" if W < 0.1 else ("medium" if W < 0.3 else "large")
        self.budget_body = {"small": (350, 550), "medium": (700, 1000), "large": (700, 1400)}[self.size]
        self.budget_leg = {"small": (40, 70), "medium": (60, 90), "large": (60, 100)}[self.size]
        self.overrides = {}
        self._an = None
        self._leg_info = None

    def override(self, key, default=None):
        return self.overrides.get(key, default)

    @property
    def an(self):
        if self._an is None:
            self._an = analyze.Analysis(self)
        return self._an

    @property
    def leg_info(self):
        if self._leg_info is None and self.leg is not None:
            self._leg_info = analyze.leg_info(self)
        return self._leg_info

    def out_names(self):
        out = {"body": Path(self.model_rel).stem + "_bones.obj"}
        if self.leg_rel:
            out["leg"] = Path(self.leg_rel).stem + "_bones.obj"
        if self.wing_rel:
            out["wing"] = Path(self.wing_rel).stem + "_bones.obj"
        return out

    def living_names(self):
        out = {"body": Path(self.model_rel).name}
        if self.leg_rel:
            out["leg"] = Path(self.leg_rel).name
        if self.wing_rel:
            out["wing"] = Path(self.wing_rel).name
        return out


def discover():
    """{module name: module} for every plan module in this package, sorted by name."""
    mods = {}
    for info in sorted(pkgutil.iter_modules(__path__), key=lambda i: i.name):
        if info.name.startswith("_"):
            continue
        mod = importlib.import_module(__name__ + "." + info.name)
        if hasattr(mod, "SPECIES") and hasattr(mod, "build"):
            mods[info.name] = mod
    return mods


def species_map(mods):
    """{thingDef: module name}; raises ValueError when two plans claim one species."""
    owner = {}
    clashes = []
    for name in sorted(mods):
        for sp in sorted(mods[name].SPECIES):
            if sp in owner:
                clashes.append("%s (%s and %s)" % (sp, owner[sp], name))
            else:
                owner[sp] = name
    if clashes:
        raise ValueError("species claimed by two plans: " + ", ".join(clashes))
    return owner
