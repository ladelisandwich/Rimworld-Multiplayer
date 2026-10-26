#!/usr/bin/env python3
"""Check IsoRim's GLSL shaders the way RimWorld 1.7 (Waylith) builds them, before launching the game.

Offline (default, about 3 s, in the distrobox):
  Emulates, from the decompiled engine (Waylith.Rendering, 1.7.4990):
  - ShaderSourceLoader: file lookup (mod Shaders folders, then <game>/Data/*/Shaders, then
    <game>/Rendering/Shaders; the exact path, else the first file of that name anywhere below the root) and
    quoted #include expansion;
  - ShaderVariantEnumerator + ShaderModuleCache.BuildVariantSource: the variants the game compiles for each
    ShaderDef (USE_TEXTURE from useTexture, INSTANCED when the vertex file mentions INSTANCED or
    gl_InstanceIndex, OUTLINE_MRT for fragments, shaderKeywords), a define kept only when the source mentions
    its name, injected right after the #version line, and the "Mesh.<name>[_kw<hash>].<stage>" source name;
  - ShaderMetadataParser + MaterialLayout: the comment-annotation rules (material uniform block, textures,
    vertex inputs, stage interface) whose violations the game logs as "ERROR [Vulkan] Shader '...'" and
    draws magenta.
  Each variant is compiled with glslangValidator (SPIR-V 1.3 for Vulkan 1.1, like the engine's spirv_1_3
  profile) and checked with spirv-val, and the material block offsets the engine computes are compared with
  glslang's std140 reflection. glslang is not the game's compiler (Slang 2026.19): it is stricter in most
  places (implicit conversions, use before declaration), so a pass is a strong sign, not a proof.

--headless installed|repo|all (about 1 minute, starts the game):
  Runs the Windows build headless under Proton on the host with -isorimshadercheck (Source/IsoRim/ShaderCheck.cs),
  which compiles the same variants with the game's own Slang and writes a report; then reads the report and
  Player.log and runs spirv-val on the SPIR-V. "installed": the installed mod's shaders (build and install
  first); "repo": GLSL from this repo's IsoRim/Shaders (defs and C# stay the installed ones); "all": every
  ShaderDef of the game (baseline: vanilla must come out clean).

Exit code: 0 all good, 1 errors found, 2 setup problem.
"""
import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DEFAULT_GAME = os.environ.get(
    "RIMWORLD_DIR", "/run/media/ahearn/Big and Slow guy/SteamLibrary/steamapps/common/RimWorld")
DEFAULT_PROTON = "/run/media/system/Fast_AF_Guy/SteamLibrary/steamapps/common/Proton 11.0/proton"
DEFAULT_PREFIX = os.path.expanduser("~/.rw-mp-test/proton-isorim/prefix")
LOCAL_LOW = "pfx/drive_c/users/steamuser/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios"

# ---- engine regexes (ShaderMetadataParser, 1.7.4990), Python syntax ----
RE_UNIFORM_BLOCK = re.compile(
    r"layout\s*\((?P<layout>[^)]*)\)\s*uniform\s+(?P<bname>\w+)\s*\{(?P<body>.*?)\}\s*\w*\s*;", re.S)
RE_UNIFORM_MEMBER = re.compile(
    r"(?:layout\s*\([^)]*\)\s*)?(?P<type>mat3|mat4|vec2|vec3|vec4|int|float)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*"
    r"(?P<array>\[[^\]]+\])?\s*;\s*(?://\s*(?P<comment>[^\r\n]+))?")
RE_MATERIAL_TEXTURE = re.compile(
    r"layout\s*\((?P<layout>[^)]*)\)\s*uniform\s+(?P<type>sampler[A-Za-z0-9_]*|texture[A-Za-z0-9_]+)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?P<array>\[[^\]]+\])?\s*;")
RE_MATERIAL_TEXTURE_COMMENT = re.compile(
    r"layout\s*\((?P<layout>[^)]*)\)\s*uniform\s+(?:sampler[A-Za-z0-9_]*|texture[A-Za-z0-9_]+)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]+\])?\s*;[^\S\r\n]*//(?P<comment>[^\r\n]*)")
RE_STORAGE_BUFFER = re.compile(
    r"layout\s*\((?P<layout>[^)]*)\)\s*(?:(?:readonly|writeonly|restrict|coherent|volatile)\s+)*buffer\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{")
RE_VERTEX_INPUT = re.compile(
    r"layout\s*\(\s*location\s*=\s*(?P<location>\d+)\s*\)\s*in\s+(?P<type>[A-Za-z0-9_]+)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?://\s*@(?P<kind>vertex|instance)(?:\s+(?P<semantic>[A-Za-z_][A-Za-z0-9_]*))?"
    r"(?:\s+(?P<presence>required|optional))?(?P<tail>[^\r\n]*))?")
RE_VARYING = re.compile(
    r"(?:layout\s*\((?P<layout>[^)]*)\)\s*)(?:(?:flat|smooth|noperspective|centroid|sample|invariant|highp|mediump|lowp)\s+)*"
    r"(?P<dir>in|out)\s+(?P<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?P<array>\[[^\]]*\])?\s*;")
RE_INTERFACE_BLOCK = re.compile(r"\b(?:in|out)\s+[A-Za-z_][A-Za-z0-9_]*\s*\{")
RE_VARYING_TYPE = re.compile(
    r"^(?:(?P<scalar>float|int|uint|bool|double)|(?P<prefix>[iubd]?)vec(?P<size>[234])|(?P<mprefix>d?)mat(?P<columns>[234])(?:x(?P<rows>[234]))?)$")
RE_DEFAULT = re.compile(r"@default\b(?P<assign>\s*=)?(?P<value>[^\r\n]*)")
RE_CTOR = re.compile(r"^\s*(?P<type>[a-z]+[234])\s*\((?P<args>[^)]*)\)")
RE_LEADING_NUMBER = re.compile(r"^[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?[fFuU]?(?![A-Za-z0-9_.(])")
RE_FULL_NUMBER = re.compile(r"^[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?[fFuU]?$")
RE_STRIP_LINE = re.compile(r"//.*?$", re.M)
RE_STRIP_BLOCK = re.compile(r"/\*.*?\*/", re.S)
RE_SET = re.compile(r"(?:^|,)\s*set\s*=\s*(\d+)")
RE_BINDING = re.compile(r"(?:^|,)\s*binding\s*=\s*(\d+)")
RE_LOCATION = re.compile(r"(?:^|,)\s*location\s*=\s*(\d+)")
RE_IDENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
RE_VERTEX_HINT = re.compile(r"@vertex\b")

VERTEX_SEMANTICS = {"Position", "Normal", "Tangent", "Color", "TexCoord0", "TexCoord1", "TexCoord2", "TexCoord3",
                    "TexCoord4", "TexCoord5", "TexCoord6", "TexCoord7", "BlendWeight", "BlendIndices"}
INSTANCE_SEMANTICS = {"ModelMatrix"}
COMPONENTS = {"float": 1, "uint": 1, "int": 1, "ivec2": 2, "uvec2": 2, "vec2": 2, "ivec3": 3, "uvec3": 3, "vec3": 3,
              "ivec4": 4, "uvec4": 4, "vec4": 4, "mat3": 9, "mat4": 16}
ALIGN = {"mat3": 16, "mat4": 16, "vec2": 8, "vec3": 16, "vec4": 16, "int": 4, "float": 4}
SIZE = {"mat3": 48, "mat4": 64, "vec2": 8, "vec3": 16, "vec4": 16, "int": 4, "float": 4}   # MaterialLayout.GetStd140Size
MAX_UNIFORM_BYTES, MAX_SAMPLERS, MAX_TEXTURE_SLOTS, MAX_STORAGE_BUFFERS = 512, 16, 16, 8
# Names Slang reserves (keywords or core-module types) that GLSL does not: glslang accepts them as names, the
# game's compiler may not.
SLANG_RESERVED = {"let", "var", "func", "import", "module", "implementing", "extension", "interface", "namespace",
                  "property", "associatedtype", "typealias", "This", "this", "none", "is", "as", "where", "throws",
                  "static", "public", "private", "internal", "export", "override", "cbuffer", "tbuffer", "register",
                  "packoffset", "groupshared", "nointerpolation", "vector", "matrix", "half", "float2", "float3",
                  "float4", "int2", "int3", "int4", "uint2", "uint3", "uint4", "float2x2", "float3x3", "float4x4",
                  "Texture2D", "SamplerState", "RWTexture2D", "StructuredBuffer", "ConstantBuffer", "ParameterBlock",
                  "Optional", "Tuple", "Ptr", "Ref", "Conditional"}
# HLSL intrinsics in Slang's core module that GLSL lacks: a function of your own with one of these names can be
# ambiguous in Slang.
SLANG_INTRINSICS = {"saturate", "lerp", "frac", "rsqrt", "mad", "rcp", "fmod", "atan2", "ddx", "ddy", "mul", "clip",
                    "sincos", "log10", "countbits", "firstbithigh", "firstbitlow", "reversebits", "asfloat", "asint",
                    "asuint", "isfinite", "lit", "select"}


class Report:
    def __init__(self, quiet=False):
        self.errors = 0
        self.warnings = 0
        self.quiet = quiet

    def error(self, msg):
        self.errors += 1
        if not self.quiet:
            print("ERROR  " + msg)

    def warn(self, msg):
        self.warnings += 1
        if not self.quiet:
            print("WARN   " + msg)

    @staticmethod
    def info(msg):
        print("       " + msg)


# ---------------------------------------------------------------- file lookup and includes (ShaderSourceLoader)
class Loader:
    def __init__(self, roots, rep):
        self.roots = [os.path.abspath(r) for r in roots if os.path.isdir(r)]
        self.rep = rep
        self.leaf = {}

    def _leaf_index(self, root):
        if root not in self.leaf:
            idx = {}
            for d, _, files in sorted(os.walk(root)):
                for f in sorted(files):
                    idx.setdefault(f.lower(), []).append(os.path.join(d, f))
            self.leaf[root] = idx
        return self.leaf[root]

    def resolve(self, name):
        for root in self.roots:
            p = os.path.join(root, name)
            if os.path.isfile(p):
                return os.path.abspath(p)
            hits = self._leaf_index(root).get(os.path.basename(name).lower())
            if hits:
                if len(hits) > 1:
                    self.rep.warn(f"{name}: {len(hits)} files of that name under {root}; the game takes whichever "
                                  f"the file system lists first: {hits}")
                return os.path.abspath(hits[0])
        return None

    @staticmethod
    def lines(text):
        parts = re.split(r"\r\n|\r|\n", text)   # StringReader.ReadLine
        if parts and parts[-1] == "":
            parts.pop()
        return parts

    @staticmethod
    def quoted_include(line):
        t = line.strip()
        if not t.startswith("#include"):
            return None
        a = t.find('"')
        if a < 0:
            return None
        b = t.find('"', a + 1)
        if b <= a + 1:
            return None
        return t[a + 1:b]

    def expand(self, path, stack=()):
        """Expanded text and a line map [(file, line)], like ShaderSourceLoader.ReadExpandedShaderSource."""
        path = os.path.abspath(path)
        if path in stack:
            raise RuntimeError("Shader include cycle detected at '" + path + "'.")
        with open(path, encoding="utf-8-sig") as f:   # File.ReadAllText drops a BOM
            text = f.read()
        out, lmap = [], []
        for i, line in enumerate(self.lines(text), 1):
            inc = self.quoted_include(line)
            if inc is None:
                out.append(line)
                lmap.append((path, i))
                continue
            full = os.path.abspath(os.path.join(os.path.dirname(path), inc))
            if not os.path.isfile(full):
                full = self.resolve(inc) or full
            if not os.path.isfile(full):
                raise RuntimeError(f"{path}:{i}: #include \"{inc}\" not found")
            sub, submap = self.expand(full, stack + (path,))
            out.extend(self.lines(sub))
            lmap.extend(submap)
            out.append("")          # AppendLine(included text) adds one more line break
            lmap.append((full, 0))
        return "\n".join(out) + "\n", lmap


# ---------------------------------------------------------------- defs
class Def:
    def __init__(self, name, vert, frag, use_texture, keywords):
        self.name, self.vert, self.frag, self.use_texture, self.keywords = name, vert, frag, use_texture, keywords


def read_defs(def_dir, rep):
    defs = []
    for root, _, files in sorted(os.walk(def_dir)):
        for f in sorted(files):
            if not f.lower().endswith(".xml"):
                continue
            p = os.path.join(root, f)
            try:
                tree = ET.parse(p)
            except ET.ParseError as e:
                rep.error(f"{p}: XML: {e}")
                continue
            for sd in tree.getroot().iter("ShaderDef"):
                if sd.get("ParentName") or sd.get("Abstract"):
                    rep.warn(f"{p}: ShaderDef with ParentName/Abstract: inheritance is not emulated")
                name = (sd.findtext("defName") or "").strip()
                prog = sd.find("shaderProgram")
                if prog is None:
                    continue
                vert = (prog.findtext("vertexShaderFileName") or "SceneDefault.vert.glsl").strip()
                frag = (prog.findtext("fragmentShaderFileName") or "SceneDefault.frag.glsl").strip()
                use_texture = (prog.findtext("useTexture") or "true").strip().lower() == "true"
                kws = sorted({li.text.strip() for li in sd.findall("shaderKeywords/li") if li.text and li.text.strip()})
                if prog.find("passShaderOverrides") is not None:
                    rep.warn(f"{name}: passShaderOverrides not emulated")
                defs.append(Def(name, vert, frag, use_texture, kws))
    return defs


# ---------------------------------------------------------------- variants (ShaderModuleCache)
def all_defines(use_texture, outline_mrt, instanced, keywords, nl):
    s = ""
    if use_texture:
        s += "#define USE_TEXTURE 1" + nl
    if outline_mrt:
        s += "#define OUTLINE_MRT 1" + nl
    if instanced:
        s += "#define INSTANCED 1" + nl
    seen = {"OUTLINE_MRT", "INSTANCED"}
    for k in keywords:
        if k not in seen:
            seen.add(k)
            s += "#define " + k + " 1" + nl
    return s


def mentioned_defines(block, source):
    if "##" in source or "#include" in source:
        return block
    names = set(RE_IDENT.findall(source))
    out = ""
    for line in [l for l in block.split("\n") if l]:
        parts = line.split(" ", 2)
        if len(parts) >= 2 and parts[1].rstrip("\r") in names:
            out += line + "\n"
    return out


def inject(source, block):
    if not block:
        return source, 0
    if source.startswith("#version"):
        i = source.find("\n")
        if i >= 0:
            return source[:i + 1] + block + source[i + 1:], block.count("\n")
    return block + source, -1   # defines above #version


def source_name(file, stage, block):
    stem = os.path.splitext(os.path.splitext(os.path.basename(file))[0])[0]
    if block:
        stem += "_kw" + hashlib.sha256(block.encode("utf-8")).hexdigest()[:8]
    return "Mesh." + stem + "." + stage


# ---------------------------------------------------------------- metadata (ShaderMetadataParser, MaterialLayout)
def strip_comments(s):
    return RE_STRIP_LINE.sub("", RE_STRIP_BLOCK.sub("", s))


def layout_int(regex, layout):
    m = regex.search(layout)
    return int(m.group(1)) if m else None


def default_ok(gtype, comment, alt=None):
    m = RE_DEFAULT.search(comment or "")
    if not m:
        return True, None
    if not m.group("assign"):
        return False, "has '@default' without '='"
    value = m.group("value").strip()

    def parse(t):
        n = COMPONENTS.get(t, 0)
        if n == 0:
            return False
        if n == 1:
            return bool(RE_LEADING_NUMBER.match(value))
        c = RE_CTOR.match(value)
        if not c or c.group("type") != t:
            return False
        args = c.group("args").split(",")
        return len(args) in (1, n) and all(RE_FULL_NUMBER.match(a.strip()) for a in args)
    if parse(gtype) or (alt and parse(alt)):
        return True, None
    return False, f"has a malformed @default '{value}'"


def material_layout(src, name, rep):
    """The engine's material layout of a fragment source: block name, members [(name, type, offset)], size,
    textures {binding: (name, type, comment)}, buffers {binding: name}."""
    no_block_comments = RE_STRIP_BLOCK.sub("", src)
    code = RE_STRIP_LINE.sub("", no_block_comments)
    comments = {}
    for m in RE_MATERIAL_TEXTURE_COMMENT.finditer(no_block_comments):
        if layout_int(RE_SET, m.group("layout")) == 2:
            comments[m.group("name")] = m.group("comment")
    textures, nsamplers, slot = {}, 0, 0
    for m in RE_MATERIAL_TEXTURE.finditer(code):
        if m.group("type").startswith("sampler"):
            nsamplers += 1
        lay = m.group("layout")
        if layout_int(RE_SET, lay) != 2 or layout_int(RE_BINDING, lay) is None:
            continue
        b = layout_int(RE_BINDING, lay)
        if m.group("array"):
            rep.error(f"{name}: material sampler array '{m.group('name')}' is not supported (the engine drops the layout)")
        if b <= 0:
            rep.error(f"{name}: material texture '{m.group('name')}' binding {b} must be positive")
        if m.group("type") != "sampler":
            slot += 1
        com = comments.get(m.group("name"), "")
        d = RE_DEFAULT.search(com)
        if d:
            v = d.group("value").strip().split(" ", 1)[0] if d.group("value").strip() else ""
            if not d.group("assign"):
                rep.error(f"{name}: texture '{m.group('name')}' has '@default' without '=' (expected white|black|gray)")
            elif v not in ("white", "black", "gray", "grey"):
                rep.error(f"{name}: texture '{m.group('name')}' has an unknown @default '{v}'")
        if b in textures:
            rep.error(f"{name}: set 2 binding {b} used by both '{textures[b][0]}' and '{m.group('name')}'")
        textures[b] = (m.group("name"), m.group("type"), com)
    if nsamplers > MAX_SAMPLERS:
        rep.error(f"{name}: declares {nsamplers} samplers, above the engine's limit of {MAX_SAMPLERS} per stage")
    if slot > MAX_TEXTURE_SLOTS:
        rep.error(f"{name}: {slot} texture slots, max {MAX_TEXTURE_SLOTS}")
    buffers = {}
    for m in RE_STORAGE_BUFFER.finditer(code):
        lay = m.group("layout")
        if layout_int(RE_SET, lay) == 2 and layout_int(RE_BINDING, lay) is not None:
            b = layout_int(RE_BINDING, lay)
            if b in textures or b in buffers:
                rep.error(f"{name}: set 2 binding {b} is used twice (buffer '{m.group('name')}')")
            buffers[b] = m.group("name")
    if len(buffers) > MAX_STORAGE_BUFFERS:
        rep.error(f"{name}: {len(buffers)} storage buffers, max {MAX_STORAGE_BUFFERS}")
    bname, members, size = None, [], 0
    for blk in RE_UNIFORM_BLOCK.finditer(no_block_comments):   # the engine keeps line comments here (annotations)
        lay = blk.group("layout")
        if layout_int(RE_SET, lay) != 2 or layout_int(RE_BINDING, lay) != 0:
            continue
        bname, body = blk.group("bname"), blk.group("body")
        if re.search(r"^\s*#", body, re.M):
            rep.error(f"{name}: preprocessor line inside the material uniform block: the engine's layout ignores "
                      f"#if/#ifdef, so offsets would differ between variants")
        for decl in re.finditer(r"(?:layout\s*\([^)]*\)\s*)?(?P<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)"
                                r"\s*(?P<array>\[[^\]]*\])?\s*;", RE_STRIP_LINE.sub("", body)):
            t, n = decl.group("type"), decl.group("name")
            if t in ALIGN:
                continue
            if t == "uint":
                rep.warn(f"{name}: material uniform '{n}' is uint; the engine's parser reads it as int (same bytes): "
                         f"prefer int")
            elif re.fullmatch(r"[iub]vec[234]", t):
                rep.error(f"{name}: material uniform '{n}' is {t}; the engine's parser takes it for {t[1:]} and writes "
                          f"float bits into it: declare {t[1:]} and convert in the shader")
            else:
                rep.error(f"{name}: material uniform '{n}' has type {t}, which the engine's parser skips: every member "
                          f"after it gets the wrong offset (use float, int, vec2-4, mat3, mat4)")
        offset = 0
        for m in RE_UNIFORM_MEMBER.finditer(body):
            t, n = m.group("type"), m.group("name")
            if m.group("array"):
                rep.error(f"{name}: material uniform array '{n}' is not supported (the engine drops the layout)")
            ok, why = default_ok(t, m.group("comment"))
            if not ok:
                rep.error(f"{name}: material uniform '{n}' {why}")
            offset = (offset + ALIGN[t] - 1) & ~(ALIGN[t] - 1)
            members.append((n, t, offset))
            offset += SIZE[t]
        size = (offset + 15) & ~15
    if size > MAX_UNIFORM_BYTES:
        rep.error(f"{name}: material uniform block is {size} bytes, max is {MAX_UNIFORM_BYTES}")
    return bname, members, size, textures, buffers


def vertex_inputs(src, name, rep):
    src = RE_STRIP_BLOCK.sub("", src)
    for m in RE_VERTEX_INPUT.finditer(src):
        n, kind = m.group("name"), m.group("kind")
        if kind == "instance":
            if m.group("semantic") not in INSTANCE_SEMANTICS:
                rep.error(f"{name}: instance input '{n}' has unknown instance semantic '{m.group('semantic')}'")
            if m.group("presence") != "required":
                rep.error(f"{name}: instance input '{n}' must use '@instance <semantic> required'")
            continue
        if kind != "vertex":
            rep.error(f"{name}: vertex input '{n}' is missing a '// @vertex <semantic> required|optional' annotation")
            continue
        if m.group("semantic") not in VERTEX_SEMANTICS:
            rep.error(f"{name}: vertex input '{n}' has unknown vertex semantic '{m.group('semantic')}'")
        if m.group("presence") not in ("required", "optional"):
            rep.error(f"{name}: vertex input '{n}' must be annotated as required or optional")
        ok, why = default_ok(m.group("type"), m.group("tail"), "vec4")
        if not ok:
            rep.error(f"{name}: vertex input '{n}' {why}")
        elif m.group("presence") == "required" and RE_DEFAULT.search(m.group("tail") or ""):
            rep.error(f"{name}: required vertex input '{n}' cannot declare @default")


def varyings(src, direction):
    src = strip_comments(src)
    if RE_INTERFACE_BLOCK.search(src):
        return None
    out = {}
    for m in RE_VARYING.finditer(src):
        if m.group("dir") != direction:
            continue
        loc = layout_int(RE_LOCATION, m.group("layout"))
        if loc is None:
            continue
        if "component" in m.group("layout"):
            return None
        count = 1
        if m.group("array"):
            a = m.group("array").strip("[] \t")
            if not a.isdigit():
                return None
            count = int(a)
        t = RE_VARYING_TYPE.match(m.group("type"))
        if not t:
            return None
        base = t.group("scalar")[0] if t.group("scalar") else ((t.group("prefix") or t.group("mprefix") or "f")[:1])
        if base == "d":
            return None
        if t.group("columns"):
            slots, comps = int(t.group("columns")), int(t.group("rows") or t.group("columns"))
        else:
            slots, comps = 1, int(t.group("size") or 1)
        for i in range(count * slots):
            out.setdefault(loc + i, []).append((m.group("name"), m.group("type"), base, comps))
    return out


def stage_interface(vsrc, fsrc, vname, fname, rep):
    vo, fi = varyings(vsrc, "out"), varyings(fsrc, "in")
    if vo is None or fi is None:
        rep.warn(f"{vname} -> {fname}: interface blocks, components or unsized arrays: the engine skips its stage check")
        return
    for loc, ins in sorted(fi.items()):
        for (n, t, base, comps) in ins:
            outs = vo.get(loc)
            if not outs:
                rep.error(f"{fname} reads '{n}' ({t}) at location {loc}, but {vname} writes nothing there")
            elif not any(o[2] == base and o[3] >= comps for o in outs):
                rep.error(f"{fname} reads '{n}' ({t}) at location {loc}, but {vname} writes '{outs[0][0]}' ({outs[0][1]}) there")


def block_members(src):
    for blk in RE_UNIFORM_BLOCK.finditer(RE_STRIP_BLOCK.sub("", src)):
        lay = blk.group("layout")
        if layout_int(RE_SET, lay) == 2 and layout_int(RE_BINDING, lay) == 0:
            body = RE_STRIP_LINE.sub("", blk.group("body"))
            return [(m.group(1), m.group(2)) for m in
                    re.finditer(r"([A-Za-z_][A-Za-z0-9_]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?\s*;", body)]
    return None


def set2_resources(src):
    code = strip_comments(src)
    res = {}
    for m in RE_MATERIAL_TEXTURE.finditer(code):
        if layout_int(RE_SET, m.group("layout")) == 2:
            res[layout_int(RE_BINDING, m.group("layout"))] = ("texture", m.group("name"))
    for m in RE_STORAGE_BUFFER.finditer(code):
        if layout_int(RE_SET, m.group("layout")) == 2:
            res[layout_int(RE_BINDING, m.group("layout"))] = ("buffer", m.group("name"))
    return res


def slang_names(src, name, rep):
    code = strip_comments(src)
    for m in re.finditer(r"\b(?:float|int|uint|bool|vec[234]|ivec[234]|uvec[234]|bvec[234]|mat[234]|void|struct)\s+"
                         r"([A-Za-z_][A-Za-z0-9_]*)", code):
        n = m.group(1)
        if n in SLANG_RESERVED:
            rep.warn(f"{name}: '{n}' is reserved in Slang (the game's compiler) but not in GLSL: rename it")
        elif n in SLANG_INTRINSICS and re.search(r"\b" + re.escape(n) + r"\s*\([^;{]*\)\s*\{", code):
            rep.warn(f"{name}: function '{n}' has the name of a Slang core-module intrinsic: rename it")


# ---------------------------------------------------------------- glslang
def reflected_offsets(out, bname):
    offs = {}
    for line in out.splitlines():
        m = re.match(r"^" + re.escape(bname or "MaterialUniforms") + r"\.([A-Za-z_][A-Za-z0-9_]*): offset (\d+),", line.strip())
        if m:
            offs[m.group(1)] = int(m.group(2))
    return offs


def compile_variant(tool, spirv_val, text, stage, sname, lmap, shift, workdir, rep):
    path = os.path.join(workdir, sname + ".glsl")
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    spv = os.path.join(workdir, sname + ".spv")
    r = subprocess.run([tool, "-V", "--target-env", "vulkan1.1", "-S", stage, "-DWAYLITH_SLANG_COMPILER=1",
                        "-q", "--reflect-all-block-variables", "-o", spv, path], capture_output=True, text=True)
    out = r.stdout + r.stderr
    if r.returncode != 0:
        for line in out.splitlines():
            m = re.match(r"^(ERROR|WARNING): (.*?):(\d+): (.*)$", line)
            if m:
                ln = int(m.group(3))
                # line 1 is #version, then `shift` define lines the engine inserted
                orig = ln if shift <= 0 or ln == 1 else (ln - shift if ln > 1 + shift else None)
                where = lmap[orig - 1] if orig and 0 < orig <= len(lmap) else (path, ln)
                shown = os.path.relpath(where[0], REPO) if where[0].startswith(REPO + os.sep) else where[0]
                (rep.error if m.group(1) == "ERROR" else rep.warn)(f"{sname}: {shown}:{where[1]}: {m.group(4).strip()}")
            elif line.startswith("ERROR") and "compilation errors" not in line:
                rep.error(f"{sname}: {line}")
        rep.info(f"exact variant source: {path}")
        return None
    if spirv_val:
        v = subprocess.run([spirv_val, "--target-env", "vulkan1.1", spv], capture_output=True, text=True)
        if v.returncode != 0:
            rep.error(f"{sname}: spirv-val: {(v.stdout + v.stderr).strip()}")
    return out


def offline(args):
    rep = Report()
    tool, spirv_val = shutil.which("glslangValidator"), shutil.which("spirv-val")
    if not tool and not args.list:
        print("glslangValidator not found: sudo apt install glslang-tools spirv-tools")
        return 2
    game_shaders = os.path.join(args.game, "Rendering", "Shaders")
    data = os.path.join(args.game, "Data")
    if not os.path.isdir(game_shaders) or not os.path.isdir(data):
        print(f"game shaders not found under {args.game} (set --game or RIMWORLD_DIR): the vanilla stages and "
              f"#include \"MaskGate.glsl\" can't be resolved")
        return 2
    # running mods last first: IsoRim (in Multiplayer, loaded last), the DLCs, Core; then the engine's folder
    data_roots = sorted((os.path.join(data, d, "Shaders") for d in os.listdir(data)),
                        key=lambda p: (os.path.basename(os.path.dirname(p)) == "Core", p))
    loader = Loader([args.shaders] + data_roots + [game_shaders], rep)
    game_loader = Loader(data_roots + [game_shaders], rep)
    nl = "\r\n" if args.newline == "crlf" else "\n"

    for f in sorted(os.listdir(args.shaders)):
        if f.endswith(".glsl") and game_loader.resolve(f):
            rep.error(f"{f}: a game shader has the same file name; mod Shaders folders come first, so every vanilla "
                      f"ShaderDef using it would get IsoRim's copy")
    defs = read_defs(args.defs, rep)
    if not defs:
        print("no ShaderDefs found under " + args.defs)
        return 2

    cache = {}

    def load(fname):
        if fname not in cache:
            p = loader.resolve(fname)
            cache[fname] = None
            if not p:
                rep.error(f"shader file '{fname}' not found in {loader.roots}")
            else:
                try:
                    cache[fname] = (p,) + loader.expand(p)
                except (RuntimeError, OSError) as e:
                    rep.error(str(e))
        return cache[fname]

    own = os.path.abspath(args.shaders) + os.sep
    jobs, seen, done = [], set(), set()
    for d in defs:
        v, f = load(d.vert), load(d.frag)
        if not v or not f:
            continue
        for (p, src, _), fname in ((v, d.vert), (f, d.frag)):
            if ("file", fname) in done:
                continue
            done.add(("file", fname))
            if not src.startswith("#version"):
                rep.warn(f"{fname}: doesn't start with '#version' on its first line: harmless until the file uses "
                         f"USE_TEXTURE, INSTANCED or OUTLINE_MRT, then the engine puts its #defines above #version")
            if re.search(r"^\s*#\s*include\s*<", src, re.M):
                rep.warn(f"{fname}: #include <...> is not expanded by the engine (only quoted includes)")
            if p.startswith(own):
                slang_names(src, fname, rep)
        if ("frag", d.frag) not in done:
            done.add(("frag", d.frag))
            material_layout(f[1], d.frag, rep)
        if ("vert", d.vert) not in done:
            done.add(("vert", d.vert))
            vertex_inputs(v[1], d.vert, rep)
        if ("pair", d.vert, d.frag) not in done:
            done.add(("pair", d.vert, d.frag))
            stage_interface(v[1], f[1], d.vert, d.frag, rep)
            fm, vm = block_members(f[1]), block_members(v[1])
            if vm is not None and fm is not None and vm != fm[:len(vm)]:
                rep.error(f"{d.vert}: its material uniform block is not the start of {d.frag}'s (the engine lays it "
                          f"out from the fragment file only, so the vertex stage reads wrong offsets): vertex {vm} "
                          f"vs fragment {fm}")
            fres, vres = set2_resources(f[1]), set2_resources(v[1])
            tex = material_layout(f[1], d.frag, Report(quiet=True))[3]
            for b, (kind, n) in sorted(vres.items()):
                if b == 0:
                    continue
                if b not in fres or fres[b][1] != n:
                    rep.error(f"{d.vert}: set 2 binding {b} '{n}' is not declared (same name, same binding) in "
                              f"{d.frag}, so the engine never binds it")
                elif kind == "texture" and not RE_VERTEX_HINT.search(tex.get(b, ("", "", ""))[2]):
                    rep.error(f"{d.vert}: samples '{n}' but {d.frag} doesn't mark it '@vertex', so the descriptor "
                              f"isn't visible to the vertex stage")
        instancing = "INSTANCED" in v[1] or "gl_InstanceIndex" in v[1]   # ShaderVariantEnumerator.VertexSupportsInstancing
        for instanced in ([False, True] if instancing else [False]):
            for stage, (p, src, lmap), file, mrt in (("vert", v, d.vert, False), ("frag", f, d.frag, False),
                                                     ("frag", f, d.frag, True)):
                block = mentioned_defines(all_defines(d.use_texture, mrt, instanced, d.keywords, nl), src)
                if (file, stage, block) in seen:
                    continue
                seen.add((file, stage, block))
                jobs.append((d, stage, file, src, lmap, block))

    if args.list:
        for d in defs:
            print(f"{d.name}: {d.vert} + {d.frag}, useTexture {str(d.use_texture).lower()}")
        print()
        for d, stage, file, src, lmap, block in jobs:
            defines = block.replace("\r", "").strip().replace("#define ", "").replace(" 1\n", ", ").replace(" 1", "")
            print(f"  {source_name(file, stage, block):38s} {defines or '-':24s} first used by {d.name}")
        return 1 if rep.errors else 0

    workdir = args.keep or tempfile.mkdtemp(prefix="isorim-shaders-")
    os.makedirs(workdir, exist_ok=True)
    nfail = 0
    for d, stage, file, src, lmap, block in jobs:
        text, shift = inject(src, block)
        sname = source_name(file, stage, block)
        if shift < 0:
            rep.error(f"{sname}: {file} doesn't start with '#version', so the engine's '{block.strip()}' lands above "
                      f"#version")
            nfail += 1
            continue
        before = rep.errors
        out = compile_variant(tool, spirv_val, text, stage, sname, lmap, shift, workdir, rep)
        if out is None or rep.errors > before:
            nfail += 1
            continue
        # the engine lays the block out from the def's fragment file, for both stages; std140 must agree
        bname, members = material_layout(load(d.frag)[1], d.frag, Report(quiet=True))[:2]
        engine = {n: o for (n, t, o) in members}
        for n, o in reflected_offsets(out, bname).items():
            if n in engine and engine[n] != o:
                rep.error(f"{sname}: material uniform '{n}' is at byte {o} in the shader but the engine writes it at "
                          f"byte {engine[n]} (a float or int right after a vec3? the engine gives a vec3 16 bytes)")
    print(f"\n{len(defs)} ShaderDefs, {len(jobs)} variants compiled with glslang, {nfail} failed; "
          f"{rep.errors} errors, {rep.warnings} warnings")
    if args.keep:
        print("variant sources and SPIR-V: " + workdir)
    elif nfail:
        print("failed variants' sources kept in " + workdir)
    else:
        shutil.rmtree(workdir, ignore_errors=True)
    return 1 if rep.errors else 0


# ---------------------------------------------------------------- headless, the game's own compiler
MODS_CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>{version}</version>
  <activeMods>
    <li>brrainz.harmony</li>
    <li>ludeon.rimworld</li>
    <li>ludeon.rimworld.royalty</li>
    <li>ludeon.rimworld.ideology</li>
    <li>ludeon.rimworld.biotech</li>
    <li>ludeon.rimworld.anomaly</li>
    <li>ludeon.rimworld.odyssey</li>
    <li>rwmt.multiplayer</li>
  </activeMods>
  <knownExpansions>
    <li>ludeon.rimworld.royalty</li>
    <li>ludeon.rimworld.ideology</li>
    <li>ludeon.rimworld.biotech</li>
    <li>ludeon.rimworld.anomaly</li>
    <li>ludeon.rimworld.odyssey</li>
  </knownExpansions>
</ModsConfigData>
"""


def headless(args):
    def q(s):
        return "'" + s.replace("'", "'\\''") + "'"

    local_low = os.path.join(args.prefix, LOCAL_LOW)
    switch = "-isorimshadercheck"
    if args.headless == "all":
        switch += "=all"
    elif args.headless == "repo":
        switch += "=Z:" + os.path.abspath(args.shaders).replace("/", "\\")
    in_container = os.path.exists("/run/.containerenv") and shutil.which("distrobox-host-exec")

    def run_game(extra):
        inner = (f"cd {q(args.game)} && exec env STEAM_COMPAT_CLIENT_INSTALL_PATH=\"$HOME/.local/share/Steam\" "
                 f"STEAM_COMPAT_DATA_PATH={q(args.prefix)} DOTNET_TieredCompilation=0 timeout {args.timeout} "
                 f"python3 {q(args.proton)} run ./RimWorld.exe -Headless -QuitAfterBoot {q(extra) if extra else ''}")
        cmd = (["distrobox-host-exec", "bash", "-c", inner] if in_container else ["bash", "-c", inner])
        print("$ " + " ".join(q(c) if " " in c else c for c in cmd))
        if args.dry_run:
            return 0
        os.makedirs(args.prefix, exist_ok=True)
        with open(os.path.join(os.path.dirname(args.prefix), "shadercheck.stdout"), "w") as so:
            return subprocess.run(cmd, stdout=so, stderr=subprocess.STDOUT).returncode

    config = os.path.join(local_low, "Config", "ModsConfig.xml")
    if not os.path.isfile(config):
        if not os.path.isdir(os.path.join(args.prefix, "pfx")):
            print("new Proton prefix: one plain headless run to create it")
            run_game(None)
        if not args.dry_run:
            version = ""
            vt = os.path.join(args.game, "Version.txt")
            if os.path.isfile(vt):
                version = open(vt).read().strip()
            os.makedirs(os.path.dirname(config), exist_ok=True)
            with open(config, "w") as f:
                f.write(MODS_CONFIG.format(version=version))
            print("wrote " + config + " (Harmony, Core, DLCs, Multiplayer)")
    started = time.time()
    code = run_game(switch)
    if args.dry_run:
        return 0
    log = os.path.join(local_low, "Logs", "Player.log")
    report_dir = os.path.join(local_low, "IsoRimShaderCheck")
    report = os.path.join(report_dir, "report.txt")
    if not os.path.isfile(report) or os.path.getmtime(report) < started:
        print(f"no new report (game exit code {code}); see {log} and "
              f"{os.path.join(os.path.dirname(args.prefix), 'shadercheck.stdout')}")
        return 2
    errors = 0
    if os.path.isfile(log):
        with open(log, encoding="utf-8", errors="replace") as f:
            for line in f:
                if re.search(r"\[IsoRim\] shader check|ERROR \[Vulkan\]|ShaderPrewarm: failed|Failed to compile shader", line):
                    print(line.rstrip())
    with open(report, encoding="utf-8", errors="replace") as f:
        text = f.read()
    errors += len(re.findall(r"^(FAIL|LAYOUT) ", text, re.M))
    for line in text.splitlines():
        if not line.startswith("OK "):
            print(line)
    spirv_val = shutil.which("spirv-val")
    if spirv_val:
        for spv in sorted(p for p in os.listdir(report_dir) if p.endswith(".spv")):
            v = subprocess.run([spirv_val, "--target-env", "vulkan1.1", os.path.join(report_dir, spv)],
                               capture_output=True, text=True)
            if v.returncode != 0:
                errors += 1
                print(f"ERROR  {spv}: spirv-val: {(v.stdout + v.stderr).strip()}")
    print(f"report: {report}")
    return 1 if errors else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=DEFAULT_GAME, help="RimWorld 1.7 folder (default: RIMWORLD_DIR or the Steam one)")
    ap.add_argument("--shaders", default=os.path.join(REPO, "IsoRim", "Shaders"), help="IsoRim's Shaders folder")
    ap.add_argument("--defs", default=os.path.join(REPO, "IsoRim", "Defs"), help="folder with IsoRim's ShaderDefs")
    ap.add_argument("--keep", metavar="DIR", help="offline: write every variant's exact source and SPIR-V to DIR")
    ap.add_argument("--newline", choices=["crlf", "lf"], default="crlf",
                    help="line break of the engine's define block: crlf = the Windows build under Proton, so the "
                         "_kw names match Player.log")
    ap.add_argument("--list", action="store_true", help="offline: only list the ShaderDefs and their variants")
    ap.add_argument("--headless", choices=["installed", "repo", "all"],
                    help="compile with the game's own Slang in a headless Proton run instead")
    ap.add_argument("--proton", default=DEFAULT_PROTON, help="Proton's 'proton' script, on the host")
    ap.add_argument("--prefix", default=DEFAULT_PREFIX, help="Proton prefix for the test run (under $HOME)")
    ap.add_argument("--timeout", type=int, default=900, help="seconds before the headless game is stopped")
    ap.add_argument("--dry-run", action="store_true", help="headless: print the commands only")
    args = ap.parse_args()
    return headless(args) if args.headless else offline(args)


if __name__ == "__main__":
    sys.exit(main())
