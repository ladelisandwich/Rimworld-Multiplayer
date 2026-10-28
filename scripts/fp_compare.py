#!/usr/bin/env python3
"""Compare the first-person self-test's state lines of two players (fp-play-spec 15.3).

  scripts/fp_compare.py host/Player.log client/Player.log

Each instance's FpTestKit writes, every 60 map ticks, one line per controlled pawn and per pawn owning a tally:
  FPSTATE m=<map id> t=<tick> p=<thingIDNumber> pos=<x>,<z> job=<def>:<loadID> wl=<per mille or -1> <FpState.Describe>
Lines are keyed by (map, tick, pawn). Only keys both logs have are compared (the client joins late and leaves early);
at least 30 are required, so a run where one side logged nothing can't pass.

It also compares the first-person commands as each machine ran them (FpTestKit.OnCommand):
  FPTEST CMD t=<tick> <command, its arguments, the issuer and its result>
(a bill command's line carries the bill's load id and the job's, a check result whether it was accepted). Every command
from the later first tick to the earlier last tick of the two logs must be in both, the same: the client runs every
command from its join point on, and either side may have run more before or after.

Exit code: 0 the same, 1 a difference or too few common lines, 2 a log is missing.
"""
import re
import sys

LINE = re.compile(r"FPSTATE m=(?P<m>\S+) t=(?P<t>\d+) p=(?P<p>\d+) (?P<rest>.*?)\s*$")
CMD = re.compile(r"FPTEST CMD t=(?P<t>\d+) (?P<text>.*?)\s*$")
MIN_COMMON = 30


def parse(path):
    states = {}
    dupes = 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            match = LINE.search(line)
            if not match:
                continue
            key = (int(match.group("t")), match.group("m"), int(match.group("p")))
            if key in states and states[key] != match.group("rest"):
                dupes += 1
            states[key] = match.group("rest")
    return states, dupes


def parse_commands(path):
    """The (tick, text) of every command line; a reload that runs a tick's commands again logs the same lines twice."""
    cmds = set()
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            match = CMD.search(line)
            if match:
                cmds.add((int(match.group("t")), match.group("text")))
    return cmds


def compare_commands(host_path, client_path):
    """0 when the commands both ran in the common tick range are the same, 1 otherwise."""
    host = parse_commands(host_path)
    client = parse_commands(client_path)
    if not host or not client:
        print(f"FPTEST CMD lines: host {len(host)}, client {len(client)}: nothing to compare")
        return 0
    lo = max(min(t for t, _ in host), min(t for t, _ in client))
    hi = min(max(t for t, _ in host), max(t for t, _ in client))
    h = {c for c in host if lo <= c[0] <= hi}
    c = {c for c in client if lo <= c[0] <= hi}
    print(f"FPTEST CMD lines: host {len(host)}, client {len(client)}, ticks {lo}-{hi}: {len(h & c)} in both")
    differ = sorted((h - c) | (c - h))
    if differ:
        t = differ[0][0]
        print(f"FIRST COMMAND DIFFERENCE at tick {t}:")
        for text in sorted(x for tick, x in h if tick == t):
            print(f"  host:   {text}")
        for text in sorted(x for tick, x in c if tick == t):
            print(f"  client: {text}")
        return 1
    return 0


def main(argv):
    if len(argv) != 3:
        print(__doc__.strip())
        return 2
    try:
        host, host_dupes = parse(argv[1])
        client, client_dupes = parse(argv[2])
    except OSError as e:
        print(f"can't read a log: {e}")
        return 2
    for name, dupes in (("host", host_dupes), ("client", client_dupes)):
        if dupes:
            # The same (map, tick, pawn) logged twice with different state: a reload replayed ticks (join point). The last
            # line counts.
            print(f"note: {dupes} repeated keys with different state in the {name} log (reloaded ticks); the last one counts")
    common = sorted(set(host) & set(client))
    only_host = len(set(host) - set(client))
    only_client = len(set(client) - set(host))
    print(f"FPSTATE lines: host {len(host)}, client {len(client)}, common {len(common)} "
          f"(host only {only_host}, client only {only_client})")
    for key in common:
        if host[key] != client[key]:
            t, m, p = key
            print(f"FIRST DIFFERENCE at map {m}, tick {t}, pawn {p}:")
            print(f"  host:   {host[key]}")
            print(f"  client: {client[key]}")
            return 1
    if len(common) < MIN_COMMON:
        print(f"too few common state lines ({len(common)} < {MIN_COMMON}): nothing much was compared")
        return 1
    if compare_commands(argv[1], argv[2]):
        return 1
    print("same state and commands on both sides")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
