#!/usr/bin/env python3
"""Run a first-person play self-test scenario (Source/IsoRim/Fp/Test, fp-play-spec 15) in the Windows build of the game
under Proton, the way scripts/check_isorim_shaders.py --headless runs it, and report the result.

  scripts/fp_selftest.py m0-noop                 solo: one windowed instance, -quicktest -isorim3d
  scripts/fp_selftest.py mp1-walk --mp           host (headless; --host-windowed for a window) and a windowed client

Install the mod first (scripts/install_local.sh): the instances load the installed Mods/Multiplayer-1.7. Windowed
instances open real game windows (1280x720 from the test Prefs): run this only while nobody is playing. The harness
feeds its input through the engine's own queue, so nothing reaches the desktop, but a game already running is refused
unless --force.

Each role has its own Proton prefix under ~/.rw-mp-test/fptest-<role>/prefix (created by one headless boot the first
time), with a ModsConfig.xml (Harmony, Core, the installed DLCs, rwmt.multiplayer), Prefs.xml (copied from the
proton-isorim prefix when there is one, then windowed, dev mode on, run in background on) and a Multiplayer username.
The game gets ISORIM_FPTEST=<scenario>:<role>, the host also FPTEST_HOST=1 (Multiplayer's test host then hosts on
0.0.0.0:30502 once the scene is built and logs "FPTEST HOSTED"; the client is started after that line).

An instance with no Player.log 180 s after its launch hung in the Proton start (before any game code logs): it is ended
and launched once more. Every instance starts only once the last run's wineserver of its prefix has exited.

Afterwards: the FPTEST lines of each Player.log, the errors that name IsoRim or Multiplayer, and with --mp
scripts/fp_compare.py on the two logs and any new MpDesyncs files.

Exit code: the scenario's fail count, plus 1 per mod error line, desync file or compare failure; 99 on a timeout;
98 when the setup failed (no game, a game already running).

The run's timeout (--timeout) is 900 s, longer for the scenarios in LONG_TIMEOUTS (mp6-both plays 5 minutes and
mp6-soak 25 after hosting and the join; the harness's own whole-scenario limit is 30 minutes of real time).
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import time

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DEFAULT_GAME = os.environ.get(
    "RIMWORLD_DIR", "/run/media/ahearn/Big and Slow guy/SteamLibrary/steamapps/common/RimWorld")
DEFAULT_PROTON = "/run/media/system/Fast_AF_Guy/SteamLibrary/steamapps/common/Proton 11.0/proton"
LOCAL_LOW = "pfx/drive_c/users/steamuser/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios"
DEFAULT_TIMEOUT = 900
# Seconds for scenarios that run longer than DEFAULT_TIMEOUT allows (launch, hosting and the join included).
LONG_TIMEOUTS = {"mp6-both": 1500, "mp6-soak": 2400, "mp4-work": 1200, "m5-odds": 1500}
TEST_ROOT = os.path.expanduser("~/.rw-mp-test")
PREFS_SOURCE = os.path.join(TEST_ROOT, "proton-isorim", "prefix", LOCAL_LOW, "Config", "Prefs.xml")
CONNECT = "127.0.0.1:30502"
STARTUP_LIMIT = 180   # seconds from the launch to the first Player.log line before an instance counts as hung
DLCS = ["Royalty", "Ideology", "Biotech", "Anomaly", "Odyssey"]   # Data/<folder> -> ludeon.rimworld.<lower>
USERNAMES = {"solo": "FpSolo", "host": "FpHost", "client": "FpClient"}

MODS_CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>{version}</version>
  <activeMods>
    <li>brrainz.harmony</li>
    <li>ludeon.rimworld</li>
{dlcs}    <li>rwmt.multiplayer</li>
  </activeMods>
  <knownExpansions>
{known}  </knownExpansions>
</ModsConfigData>
"""

MP_SETTINGS = """<?xml version="1.0" encoding="utf-8"?>
<SettingsBlock>
  <ModSettings Class="Multiplayer.Client.MpSettings">
    <username>{name}</username>
  </ModSettings>
</SettingsBlock>
"""


def q(s):
    return "'" + s.replace("'", "'\\''") + "'"


IN_CONTAINER = os.path.exists("/run/.containerenv") and shutil.which("distrobox-host-exec")


def host_cmd(inner):
    """A shell command run on the host (Proton isn't in the container)."""
    return ["distrobox-host-exec", "bash", "-c", inner] if IN_CONTAINER else ["bash", "-c", inner]


class Instance:
    def __init__(self, role, args):
        self.role = role
        self.args = args
        self.root = os.path.join(TEST_ROOT, "fptest-" + role)
        self.prefix = os.path.join(self.root, "prefix")
        self.local_low = os.path.join(self.prefix, LOCAL_LOW)
        self.log = os.path.join(self.local_low, "Logs", "Player.log")
        self.desyncs = os.path.join(self.local_low, "MpDesyncs")
        self.proc = None
        self.desyncs_before = set()
        self.started = False
        self.relaunches = 0

    def game_args(self):
        if self.role == "solo":
            return "-quicktest -isorim3d"
        if self.role == "host":
            return "-quicktest -isorim3d" + ("" if self.args.host_windowed else " -Headless")
        return "-connect=" + CONNECT

    def run(self, game_args, env_extra, wait):
        env = (f"STEAM_COMPAT_CLIENT_INSTALL_PATH=\"$HOME/.local/share/Steam\" STEAM_COMPAT_DATA_PATH={q(self.prefix)} "
               f"DOTNET_TieredCompilation=0 {env_extra}")
        inner = (f"cd {q(self.args.game)} && exec env {env} timeout {self.args.timeout + 120} "
                 f"python3 {q(self.args.proton)} run ./RimWorld.exe {game_args}")
        cmd = host_cmd(inner)
        print(f"[{self.role}] $ " + " ".join(q(c) if " " in c else c for c in cmd))
        if self.args.dry_run:
            return 0
        os.makedirs(self.root, exist_ok=True)
        out = open(os.path.join(self.root, "stdout.log"), "w")
        proc = subprocess.Popen(cmd, stdout=out, stderr=subprocess.STDOUT)
        if wait:
            return proc.wait()
        self.proc = proc
        return 0

    def ensure_prefix(self):
        if os.path.isdir(os.path.join(self.prefix, "pfx")) or self.args.dry_run:
            return
        print(f"[{self.role}] new Proton prefix: one plain headless boot to create it")
        self.run("-Headless -QuitAfterBoot", "", wait=True)

    def write_config(self):
        cfg = os.path.join(self.local_low, "Config")
        if self.args.dry_run:
            print(f"[{self.role}] would write ModsConfig.xml, Prefs.xml and the Multiplayer username into {cfg}")
            return
        os.makedirs(cfg, exist_ok=True)
        version = ""
        vt = os.path.join(self.args.game, "Version.txt")
        if os.path.isfile(vt):
            version = open(vt).read().strip()
        installed = [d for d in DLCS if os.path.isdir(os.path.join(self.args.game, "Data", d))]
        ids = "".join(f"    <li>ludeon.rimworld.{d.lower()}</li>\n" for d in installed)
        with open(os.path.join(cfg, "ModsConfig.xml"), "w") as f:
            f.write(MODS_CONFIG.format(version=version, dlcs=ids, known=ids))

        prefs_path = os.path.join(cfg, "Prefs.xml")
        if os.path.isfile(prefs_path):
            prefs = open(prefs_path, encoding="utf-8-sig").read()
        elif os.path.isfile(PREFS_SOURCE):
            prefs = open(PREFS_SOURCE, encoding="utf-8-sig").read()
        else:
            prefs = '<?xml version="1.0" encoding="utf-8"?>\n<PrefsData>\n</PrefsData>\n'
        for key, value in (("fullscreen", "False"), ("devMode", "True"), ("runInBackground", "True"),
                           ("pauseOnError", "False"), ("pauseOnLoad", "False")):
            prefs = set_xml_value(prefs, key, value)
        with open(prefs_path, "w", encoding="utf-8") as f:
            f.write(prefs)

        mp_path = os.path.join(cfg, "Mod_Multiplayer-1.7_Multiplayer.xml")
        name = USERNAMES[self.role]
        if os.path.isfile(mp_path):
            text = open(mp_path, encoding="utf-8-sig").read()
            if "<username>" in text:
                text = re.sub(r"<username>[^<]*</username>", f"<username>{name}</username>", text)
            else:
                text = text.replace("<ModSettings Class=\"Multiplayer.Client.MpSettings\">",
                                    f"<ModSettings Class=\"Multiplayer.Client.MpSettings\">\n    <username>{name}</username>", 1)
        else:
            text = MP_SETTINGS.format(name=name)
        with open(mp_path, "w", encoding="utf-8") as f:
            f.write(text)
        print(f"[{self.role}] config: {len(installed)} DLCs ({', '.join(installed)}), dev mode, windowed, user {name}")

    def start(self, scenario):
        if not self.args.dry_run:
            if os.path.isfile(self.log):
                os.remove(self.log)   # only this run's lines
            self.desyncs_before = set(os.listdir(self.desyncs)) if os.path.isdir(self.desyncs) else set()
        extra = f"ISORIM_FPTEST={q(scenario + ':' + self.role)}" + (" FPTEST_HOST=1" if self.role == "host" else "")
        self.wait_for_wineserver()
        self.run(self.game_args(), extra, wait=False)
        self.started = True
        self.started_at = time.time()

    def stuck_at_start(self):
        """Still no Player.log STARTUP_LIMIT seconds after the launch: the game never got as far as its first log line
        (seen under Proton: RimWorld.exe asleep with 4 threads, the CLR barely started). Ends that instance."""
        if self.args.dry_run or not self.running() or os.path.isfile(self.log):
            return False
        if time.time() - self.started_at < STARTUP_LIMIT:
            return False
        print(f"[{self.role}] no Player.log {STARTUP_LIMIT} s after the launch: the game hung at startup; ending it")
        self.kill()
        return True

    def text(self):
        try:
            with open(self.log, encoding="utf-8", errors="replace") as f:
                return f.read()
        except OSError:
            return ""

    def running(self):
        return self.proc is not None and self.proc.poll() is None

    def new_desyncs(self):
        if not os.path.isdir(self.desyncs):
            return []
        return sorted(set(os.listdir(self.desyncs)) - self.desyncs_before)

    def check_startup(self, scenario):
        """A launch that hung before its first log line is ended and launched once more: nothing of the game's or a
        mod's ran yet (mods load after the log starts), so this is the Proton start, not the scenario. Seen 4 times in
        about 40 --mp launches, always the headless host, never twice in a row."""
        if not self.stuck_at_start() or self.relaunches >= 1:
            return
        self.relaunches += 1
        print(f"[{self.role}] relaunching once")
        self.start(scenario)

    def wait_for_wineserver(self):
        """Waits (up to 60 s) for the wineserver of this prefix left by the last run to exit before a new launch: twice
        a headless host launched straight after the previous run hung before its first log line (RimWorld.exe asleep in
        ntsync with 4 threads), and a launch racing a wineserver that is shutting down is the likeliest cause."""
        if self.args.dry_run:
            return
        wineserver = os.path.join(os.path.dirname(self.args.proton), "files", "bin", "wineserver")
        subprocess.run(host_cmd(f"WINEPREFIX={q(os.path.join(self.prefix, 'pfx'))} timeout 60 {q(wineserver)} -w"),
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def kill(self):
        """Ends whatever still runs in this prefix (wineserver -k), then the launcher."""
        wineserver = os.path.join(os.path.dirname(self.args.proton), "files", "bin", "wineserver")
        subprocess.run(host_cmd(f"WINEPREFIX={q(os.path.join(self.prefix, 'pfx'))} {q(wineserver)} -k"),
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if self.running():
            self.proc.terminate()


def set_xml_value(xml, key, value):
    pattern = re.compile(rf"<{key}>[^<]*</{key}>")
    if pattern.search(xml):
        return pattern.sub(f"<{key}>{value}</{key}>", xml, count=1)
    return xml.replace("</PrefsData>", f"  <{key}>{value}</{key}>\n</PrefsData>", 1)


ERROR_LINE = re.compile(r"^\S+ \S+\s+ERROR \[")
MOD_WORDS = ("IsoRim", "Multiplayer", "FPTEST", "[MP]")


def mod_errors(text):
    """ERROR lines whose message or stack names IsoRim or Multiplayer: (count, the first lines of each)."""
    lines = text.splitlines()
    found = []
    i = 0
    while i < len(lines):
        if ERROR_LINE.match(lines[i]):
            block = [lines[i]]
            j = i + 1
            while j < len(lines) and (lines[j].startswith(" ") or lines[j].startswith("\t")):
                block.append(lines[j])
                j += 1
            if any(w in line for line in block for w in MOD_WORDS):
                found.append(block)
            i = j
        else:
            i += 1
    return found


def game_running():
    """Any RimWorld already running on the host (the user playing, or a test left behind)."""
    r = subprocess.run(host_cmd("pgrep -af 'RimWorld(\\.exe|\\.dll)?( |$)' | grep -v pgrep || true"),
                       capture_output=True, text=True)
    return [line for line in r.stdout.splitlines() if "RimWorld" in line and "fp_selftest" not in line and is_game(line)]


def is_game(line):
    """A pgrep -af line of the game itself: the Windows build (RimWorld.exe, also in Steam's and Proton's launcher lines)
    or the native apphost (argv[0] named RimWorld) or RimWorld.dll. Not another program that merely has the game folder
    on its command line (a file manager open on .../common/RimWorld matched the bare pattern)."""
    parts = line.split(None, 1)
    rest = parts[1] if len(parts) > 1 else ""
    if re.search(r"RimWorld\.(exe|dll)\b", rest, re.IGNORECASE):
        return True
    if re.search(r"(^|/)RimWorld/RimWorld( |$)", rest):   # the apphost in the game folder (the path may hold spaces)
        return True
    argv0 = rest.split(" ", 1)[0]
    return os.path.basename(argv0.replace("\\", "/")) in ("RimWorld", "RimWorld.exe")


def registered_scenarios():
    """Scenario ids the harness knows: the s["<id>"] = ... lines of each FpTestM<n>.Register."""
    ids = set()
    folder = os.path.join(REPO, "Source", "IsoRim", "Fp", "Test")
    for name in sorted(os.listdir(folder)) if os.path.isdir(folder) else []:
        if re.fullmatch(r"FpTestM\d+\.cs", name):
            with open(os.path.join(folder, name), encoding="utf-8") as f:
                ids.update(re.findall(r'\bs\["([^"]+)"\]\s*=', f.read()))
    return ids


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scenario", help="scenario id, e.g. m0-noop, m1-walk, mp1-walk")
    ap.add_argument("--mp", action="store_true", help="host (headless) and client instead of one solo instance")
    ap.add_argument("--timeout", type=int, default=None,
                    help=f"seconds for the whole run (default {DEFAULT_TIMEOUT}, longer for " + ", ".join(sorted(LONG_TIMEOUTS)) + ")")
    ap.add_argument("--proton", default=DEFAULT_PROTON, help="Proton's 'proton' script, on the host")
    ap.add_argument("--game", default=DEFAULT_GAME, help="RimWorld 1.7 folder (default: RIMWORLD_DIR or the Steam one)")
    ap.add_argument("--host-windowed", action="store_true", help="--mp: the host gets a window too")
    ap.add_argument("--force", action="store_true", help="start even though a RimWorld is already running")
    ap.add_argument("--dry-run", action="store_true", help="print the commands only")
    args = ap.parse_args()

    if args.timeout is None:
        args.timeout = LONG_TIMEOUTS.get(args.scenario, DEFAULT_TIMEOUT)
    known = registered_scenarios()
    if known and args.scenario not in known:
        print(f"unknown scenario {args.scenario}: not registered in Source/IsoRim/Fp/Test (known: {', '.join(sorted(known))})")
        return 98
    if args.scenario.startswith("mp") and not args.mp:
        print(f"{args.scenario} is a multiplayer scenario: add --mp")
        return 98

    if not os.path.isfile(os.path.join(args.game, "RimWorld.exe")):
        print(f"no Windows build at {args.game} (RimWorld.exe): Steam must be forcing Proton (CLAUDE.md)")
        return 98
    if not os.path.isdir(os.path.join(args.game, "Mods", "Multiplayer-1.7")):
        print("WARNING: the mod isn't installed in the game's Mods folder: run scripts/install_local.sh first")
    if not args.dry_run and not args.force:
        running = game_running()
        if running:
            print("A RimWorld is already running (the user playing?); not starting test windows. --force to override:")
            for line in running:
                print("  " + line)
            return 98

    roles = ["host", "client"] if args.mp else ["solo"]
    inst = {r: Instance(r, args) for r in roles}
    for i in inst.values():
        i.ensure_prefix()
        i.write_config()

    deadline = time.time() + args.timeout
    timed_out = False
    if args.mp:
        inst["host"].start(args.scenario)
        hosted = True
        if not args.dry_run:
            print("waiting for the host to log FPTEST HOSTED ...")
            while "FPTEST HOSTED" not in inst["host"].text():
                inst["host"].check_startup(args.scenario)
                if time.time() > deadline or not inst["host"].running() or "FPTEST DONE" in inst["host"].text():
                    print("the host never hosted: the client isn't started")
                    timed_out = time.time() > deadline
                    hosted = False
                    break
                time.sleep(2)
        if hosted:
            inst["client"].start(args.scenario)
    else:
        inst["solo"].start(args.scenario)
    if args.dry_run:
        return 0

    pending = set(r for r in roles if inst[r].started)
    while pending and not timed_out:
        for r in sorted(pending):
            i = inst[r]
            i.check_startup(args.scenario)
            if "FPTEST DONE" in i.text() or not i.running():
                pending.discard(r)
        if time.time() > deadline:
            timed_out = True
            break
        time.sleep(2)

    # Root.Shutdown then Application.Quit 600 frames later: give it a moment, then end whatever is left.
    grace = time.time() + 60
    while any(i.running() for i in inst.values()) and time.time() < grace:
        time.sleep(2)
    for i in inst.values():
        if i.running():
            i.kill()

    total = 0
    for r in roles:
        i = inst[r]
        if not i.started:
            print(f"\n==== {r}: not started")
            total += 1
            continue
        text = i.text()
        print(f"\n==== {r}: {i.log}")
        fails = None
        for line in text.splitlines():
            m = re.search(r"(FPTEST (?:PASS|FAIL|INFO|START|DONE|INSTALLED|HOSTED).*)$", line)
            if m:
                print("  " + m.group(1))
            d = re.search(r"FPTEST DONE fails=(\d+)", line)
            if d:
                fails = int(d.group(1))
        if fails is None:
            # A game that ended without its DONE line crashed or quit before the scenario finished: a failure (a timeout
            # is reported as such below).
            print(f"  no FPTEST DONE line ({'timed out' if timed_out else 'the game ended early'})")
            total += 0 if timed_out else 1
        else:
            total += fails
        errors = mod_errors(text)
        for block in errors:
            print("  ERROR " + block[0].split("ERROR", 1)[1].strip()[:300])
            for line in block[1:4]:
                print("        " + line.strip())
        total += len(errors)
        missing = [line for line in text.splitlines() if "First-person hook missing" in line or "Hooks missing" in line]
        for line in missing:
            print("  " + line.strip())
        total += len(missing)
        if args.mp:
            desyncs = i.new_desyncs()
            for name in desyncs:
                print("  new desync report: " + os.path.join(i.desyncs, name))
            total += len(desyncs)

    if args.mp and all(inst[r].started and os.path.isfile(inst[r].log) for r in roles):
        print("\n==== fp_compare")
        code = subprocess.run([sys.executable, os.path.join(REPO, "scripts", "fp_compare.py"),
                               inst["host"].log, inst["client"].log]).returncode
        total += 1 if code else 0

    if timed_out:
        print(f"\nTIMEOUT after {args.timeout} s")
        return 99
    print(f"\n{'OK' if total == 0 else 'FAILED'}: {total}")
    return min(total, 97)


if __name__ == "__main__":
    sys.exit(main())
