#!/usr/bin/env python3
"""Writes IsoRim's 3D animal skeletons (Multiplayer port; a tool, not shipped).

For every active animal ModelDef in IsoRim/Defs/Models.xml (<animal>true</animal>) whose species a body plan
in scripts/animal_bones/plans/ claims, it reads the living OBJs and writes, next to them:
    Models/<body>_bones.obj   the skeleton (or dried husk) body
    Models/<leg>_bones.obj    the bone leg, hip at the top (y = legLength), foot at y = 0
    Models/<wing>_bones.obj   a bones wing, when the def has a wing model
IsoRim loads these when they exist and draws them for a dried (Dessicated) corpse.

Usage:
    python3 scripts/make_animal_skeletons.py [--only Name,Name] [--out DIR] [--check]
      --only   thingDef names (as in Models.xml, case ignored), comma separated
      --out    output folder (default IsoRim/Models)
      --check  build and validate everything, write nothing (exit status 1 on an error)

Deterministic: no randomness, the same input gives byte-identical files. File conventions (axes, winding,
header, materials) are documented in scripts/animal_bones/objio.py and prims.py.
"""

import argparse
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from animal_bones import objio, plans, validate  # noqa: E402

REPO = HERE.parent
ISORIM = REPO / "IsoRim"


def main(argv=None):
    ap = argparse.ArgumentParser(description="Write IsoRim's 3D animal skeleton OBJs.")
    ap.add_argument("--only", default="", help="thingDef names, comma separated (case ignored)")
    ap.add_argument("--out", default=str(ISORIM / "Models"), help="output folder (default IsoRim/Models)")
    ap.add_argument("--check", action="store_true", help="validate without writing")
    ap.add_argument("--defs", default=str(ISORIM / "Defs" / "Models.xml"), help=argparse.SUPPRESS)
    ap.add_argument("--quiet", action="store_true", help="only print problems and the summary")
    ap.add_argument("--write-errors", action="store_true", help=argparse.SUPPRESS)  # debugging: write anyway
    args = ap.parse_args(argv)

    mods = plans.discover()
    try:
        owner = plans.species_map(mods)
    except ValueError as e:
        print("error: %s" % e, file=sys.stderr)
        return 2

    defs = plans.read_defs(args.defs)
    only = [s.strip().lower() for s in args.only.split(",") if s.strip()]
    if only:
        known = {d["thingDef"].lower() for d in defs}
        missing = [s for s in only if s not in known]
        if missing:
            print("error: no active animal ModelDef for: %s" % ", ".join(missing), file=sys.stderr)
            return 2
        defs = [d for d in defs if d["thingDef"].lower() in only]

    out_dir = Path(args.out)
    if not args.check:
        out_dir.mkdir(parents=True, exist_ok=True)

    skipped = []
    n_err = 0
    n_written = 0
    t0 = time.time()
    for d in defs:
        name = d["thingDef"]
        plan = owner.get(name)
        if plan is None:
            skipped.append(name)
            continue
        animal = plans.Animal(d, ISORIM)
        try:
            out = mods[plan].build(animal)
        except Exception as e:  # report and go on with the other animals
            import traceback
            traceback.print_exc()
            print("%-18s %-10s ERROR building: %s" % (name, plan, e))
            n_err += 1
            continue
        errs, warns = validate.validate(animal, out)
        names = animal.out_names()
        living = animal.living_names()
        counts = []
        for key in ("body", "leg", "wing"):
            if key in out and out[key] is not None and key not in names:
                errs.append("%s: plan built a %s but the def has no %s model" % (name, key, key))
                continue
            parts = validate.as_list(out.get(key))
            if parts:
                counts.append("%s %d" % (key, sum(len(q) for q in parts)))
        status = "ok" if not errs else "ERROR"
        if not args.quiet or errs or warns:
            print("%-18s %-10s %-28s %s" % (name, plan, ", ".join(counts), status))
            for w in warns:
                print("    warning: %s" % w)
            for e in errs:
                print("    error: %s" % e)
        if errs:
            n_err += 1
            if not args.write_errors:
                continue
        if args.check:
            continue
        for key in ("body", "leg", "wing"):
            parts = validate.as_list(out.get(key))
            if not parts or key not in names:
                continue
            path = out_dir / names[key]
            objio.write_obj(path, parts, living[key])
            n_written += 1

    if skipped:
        print("skipped (no plan claims them): %s" % ", ".join(skipped))
    done = "checked, nothing written" if args.check else "%d files written to %s" % (n_written, out_dir)
    print("%d animals, %d with errors, %s (%.1fs)" % (len(defs) - len(skipped), n_err, done, time.time() - t0))
    return 1 if n_err else 0


if __name__ == "__main__":
    sys.exit(main())
