#!/usr/bin/env python3
"""Compare the first-person self-test's state lines of two players (fp-play-spec 15.3).

  scripts/fp_compare.py host/Player.log client/Player.log

Each instance's FpTestKit writes, every 60 map ticks, one line per controlled pawn and per pawn owning a tally:
  FPSTATE m=<map id> t=<tick> p=<thingIDNumber> pos=<x>,<z> job=<def>:<loadID> wl=<per mille or -1> <FpState.Describe>
Lines are keyed by (map, tick, pawn). Only keys both logs have are compared (the client joins late and leaves early);
at least 30 are required, so a run where one side logged nothing can't pass.

Exit code: 0 the same, 1 a difference or too few common lines, 2 a log is missing.
"""
import re
import sys

LINE = re.compile(r"FPSTATE m=(?P<m>\S+) t=(?P<t>\d+) p=(?P<p>\d+) (?P<rest>.*?)\s*$")
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
    print("same state on both sides")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
