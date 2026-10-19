#!/usr/bin/env python3
"""Lists the game methods Multiplayer's code looks up by compiler-generated name (lambdas, local functions)
and the methods it transpiles, from the C# source. Input for Source/Tools/RwDiff.

usage: mp_targets.py <repo root> > targets.txt
lines: lambda <Type> <Method or .ctor> <ordinal>   |   transpiled <Type> <Method>
"""
import os, re, sys

root = sys.argv[1]
src_dirs = [os.path.join(root, 'Source', 'Client'), os.path.join(root, 'Source', 'MultiplayerLoader')]

TYPE = r'typeof\((?P<type>[\w.]+)(?:<[^>]*>)?\)'
NAME = r'(?:nameof\(\w+\.(?P<name>\w+)\)|"(?P<sname>\w+)")'

lambda_res = [
    # MpMethodUtil.GetLambda(typeof(X), nameof(X.Y), ..., lambdaOrdinal: N) / positional
    re.compile(TYPE + r'\s*,\s*' + NAME + r'(?:[^;]*?lambdaOrdinal:\s*(?P<ord>\d+))?', re.S),
]
sync_lambda_re = re.compile(r'(?:SyncDelegate|SyncMethod|MethodOf)\.(?:Lambda|LambdaInGetter|LambdaInSetter)\(\s*' + TYPE + r'\s*,\s*' + NAME + r'\s*,\s*(?P<ord>\d+)', re.S)
getlambda_re = re.compile(r'GetLambda\(\s*' + TYPE + r'(?P<rest>[^;]*?)\)\s*[;,.]', re.S)
transpiled_class_re = re.compile(r'\[HarmonyPatch[^\]]*\][\s\S]{0,400}?class\s+\w+[\s\S]*?\n\s*\}\n', re.S)

lambdas, transpiled = set(), set()

for d in src_dirs:
    for dirpath, _, files in os.walk(d):
        for f in files:
            if not f.endswith('.cs'):
                continue
            text = open(os.path.join(dirpath, f), encoding='utf-8-sig', errors='replace').read()
            text = re.sub(r'//[^\n]*', '', text)  # drop line comments, commented-out lookups aren't targets

            for m in sync_lambda_re.finditer(text):
                lambdas.add((m.group('type').split('.')[-1], m.group('name') or m.group('sname'), int(m.group('ord'))))

            for m in getlambda_re.finditer(text):
                rest = m.group('rest')
                name = re.search(r'nameof\(\w+\.(\w+)\)|"(\w+)"', rest)
                ctor = 'MethodType.Constructor' in rest
                ordm = re.search(r'lambdaOrdinal:\s*(\d+)', rest)
                ordinal = int(ordm.group(1)) if ordm else 0
                method = '.ctor' if ctor else (name.group(1) or name.group(2) if name else None)
                if method:
                    lambdas.add((m.group('type').split('.')[-1], method, ordinal))

            # Transpiler targets: every HarmonyPatch class whose body contains "Transpiler("
            for cls in re.finditer(r'((?:\[HarmonyPatch[^\n]*\n\s*)+)(?:public |internal )?static class (\w+)\s*\{', text):
                start = cls.end()
                # crude brace matching for the class body
                depth, i = 1, start
                while depth and i < len(text):
                    depth += {'{': 1, '}': -1}.get(text[i], 0)
                    i += 1
                body = text[start:i]
                if 'Transpiler(' not in body:
                    continue
                attrs = cls.group(1)
                t = re.search(TYPE, attrs)
                n = re.search(NAME, attrs)
                if t and n:
                    transpiled.add((t.group('type').split('.')[-1], n.group('name') or n.group('sname')))
                for tm in re.finditer(r'AccessTools\.(?:Method|DeclaredMethod|PropertyGetter|PropertySetter)\(\s*' + TYPE + r'\s*,\s*' + NAME, body):
                    transpiled.add((tm.group('type').split('.')[-1], tm.group('name') or tm.group('sname')))

for t, m, o in sorted(lambdas):
    print(f'lambda {t} {m} {o}')
for t, m in sorted(transpiled):
    print(f'transpiled {t} {m}')
