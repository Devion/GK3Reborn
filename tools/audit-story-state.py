"""Inventory literal story-state reads/writes in normalized assets and engine code.

Usage: python tools/audit-story-state.py CONTENT_ROOT OUTPUT_JSON [--check]
This is a candidate finder, not a reachability proof. Dynamic arguments and
control flow require separate runtime tests. No game assets are copied out.
"""
import collections
import argparse
import json
from pathlib import Path
import re
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('content_root', type=Path)
parser.add_argument('output_json', type=Path)
parser.add_argument('--check', action='store_true')
options = parser.parse_args()
root = options.content_root
for folder, pattern, minimum in [('scripts-disassembled', '*.sheep', 224), ('actions', '*.NVC', 390), ('scenes', '*.SIF', 572)]:
    actual = sum(1 for _ in (root / folder).rglob(pattern))
    if actual < minimum:
        parser.error(f'{root / folder}: found {actual} {pattern} files; expected at least {minimum}. Import the full game corpus first.')
engine = Path(__file__).resolve().parents[1] / 'src/GK3Reborn.Engine'
calls = []
functions = {}
counts = collections.Counter()
implicit_reads = []

def add(name, args, source):
    calls.append(dict(name=name.lower(), args=args, source=source))

for path in sorted((root / 'scripts-disassembled').glob('*.sheep')):
    counts['scripts'] += 1
    stack = []
    function = ''
    functions[path.stem.upper()] = []
    for lineno, line in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
        m = re.match(r'^  (\w+\$)\s*$', line)
        if m:
            function = m[1]
            functions[path.stem.upper()].append(function.rstrip('$').upper())
            counts['functions'] += 1
            stack = []
        m = re.match(r'\s*\d+\s+(\w+)\s*(.*)', line)
        if not m:
            continue
        op, operand = m.groups()
        if op == 'PushS':
            literal = re.search(r'// "(.*)"', operand)
            stack.append(literal[1] if literal else None)
        elif op in ('PushI', 'PushF'):
            try:
                stack.append(float(operand.strip()) if op == 'PushF' else int(operand.strip()))
            except ValueError:
                stack.append(None)
        elif op in ('GetString', 'BeginWait', 'EndWait'):
            pass
        elif op.startswith('CallSysFunction'):
            name = operand.split('//')[-1].strip()
            argc = stack.pop() if stack else None
            args = stack[-argc:] if isinstance(argc, int) and 0 < argc <= len(stack) else []
            if argc and args:
                del stack[-argc:]
            add(name, args, f'{path.name}:{lineno}:{function}')
            stack.append(None)
        elif op == 'Pop':
            if stack:
                stack.pop()
        else:
            # Do not guess constants across arithmetic, locals, or branches.
            stack = []

literal_call = re.compile(r'\b(\w+)\s*\(\s*("[^"\r\n]*"(?:\s*,\s*(?:"[^"\r\n]*"|-?\d+))*)')
for folder, extension in [('actions', '*.NVC'), ('scenes', '*.SIF')]:
    for path in sorted((root / folder).rglob(extension)):
        counts[folder] += 1
        for lineno, raw in enumerate(path.read_text(encoding='utf-8-sig', errors='replace').splitlines(), 1):
            line = raw.split('//')[0]
            source = f'{path.name}:{lineno}'
            for m in literal_call.finditer(line):
                args = [s[1:-1] if s.startswith('"') else int(s) for s in re.findall(r'"[^"\r\n]*"|-?\d+', m[2])]
                add(m[1], args, source)
            if folder == 'actions':
                m = re.match(r'\s*(\w+)\s*,\s*(T_\w+|Z_CHAT)\s*,', line, re.I)
                if m:
                    add('SetTopicCount' if m[2].upper().startswith('T_') else 'SetChatCount', list(m.groups()), source + ':automatic')
                m = re.match(r'\s*(\w+)\s*,\s*(\w+)\s*,\s*(1ST_TIME|2CD_TIME|2ND_TIME|3RD_TIME|OTR_TIME)\s*,', line, re.I)
                if m and not m[2].upper().startswith(('T_', 'Z_CHAT')):
                    implicit_reads.append(dict(family='nounverb', key=[m[1].upper(), m[2].upper()], source=source))

for path in sorted(engine.rglob('*.cs')):
    for lineno, raw in enumerate(path.read_text(encoding='utf-8-sig', errors='replace').splitlines(), 1):
        line = raw.split('//')[0]
        for m in literal_call.finditer(line):
            args = [s[1:-1] if s.startswith('"') else int(s) for s in re.findall(r'"[^"\r\n]*"|-?\d+', m[2])]
            add(m[1], args, f'{path.relative_to(engine)}:{lineno}')

families = {
    'nounverb': (2, ['getnounverbcount', 'getnounverbcountint'], ['setnounverbcount', 'setnounverbcountboth', 'incnounverbcount', 'incnounverbcountboth', 'incrementnounverbcount']),
    'topic': (2, ['gettopiccount', 'gettopiccountint'], ['settopiccount']),
    'chat': (1, ['getchatcount', 'getchatcountint'], ['setchatcount', 'incchatcount', 'incrementchatcount']),
    'flag': (1, ['getflag'], ['setflag']),
    'variable': (1, ['getgamevariableint', 'getvariable'], ['setgamevariableint', 'incgamevariableint', 'setvariable']),
    'score': (1, ['hasscored'], ['changescore', 'awardscore']),
}
missing = []
for family, (arity, getters, setters) in families.items():
    reads, writes = collections.defaultdict(list), collections.defaultdict(list)
    for call in calls:
        args = call['args']
        if len(args) < arity or not all(isinstance(a, str) for a in args[:arity]):
            continue
        key = tuple(a.upper() for a in args[:arity])
        if call['name'] in getters:
            reads[key].append(call['source'])
        if call['name'] in setters:
            writes[key].append(call['source'])
    counts[family + '_read_keys'] = len(reads)
    counts[family + '_write_keys'] = len(writes)
    for key in reads.keys() - writes.keys():
        missing.append(dict(family=family, key=key, reads=reads[key]))

broken_calls = []
for c in calls:
    if c['name'] == 'callsheep' and len(c['args']) >= 2 and all(isinstance(a, str) for a in c['args'][:2]):
        script, function = c['args'][:2]
        script = script.upper().removesuffix('.SHP')
        if script not in functions or function.rstrip('$').upper() not in functions[script]:
            broken_calls.append(c)
    if c['name'] == 'call' and c['args'] and isinstance(c['args'][0], str) and '.sheep:' in c['source']:
        script = c['source'].split('.sheep:')[0].upper()
        if c['args'][0].rstrip('$').upper() not in functions[script]:
            broken_calls.append(c)

written_pairs = {tuple(str(a).upper() for a in c['args'][:2]) for c in calls
                 if c['name'] in families['nounverb'][2] and len(c['args']) >= 2}
orphan_repeat_rules = [r for r in implicit_reads if tuple(r['key']) not in written_pairs]
scores = (engine / 'Assets/Story/Scores.txt').read_text(encoding='utf-8-sig')
known_scores = {s.lower() for s in re.findall(r'\b(e_\w+)\s*=', scores, re.I)}
unknown_scores = [c for c in calls if c['name'] == 'changescore' and c['args']
                  and isinstance(c['args'][0], str) and c['args'][0].lower() not in known_scores]
counts['literal_calls'] = sum(bool(c['args']) for c in calls)
counts['score_events_in_table'] = len(known_scores)

# This checks references, not whether the award is reachable in every gameplay path.
quest_text = (engine / 'Assets/Story/Quests.txt').read_text(encoding='utf-8-sig')
quest_scores = {s.lower() for s in re.findall(r'\be_\w+', quest_text, re.I)}
awarded_scores = {c['args'][0].lower() for c in calls if c['name'] == 'changescore' and c['args'] and isinstance(c['args'][0], str)}
engine_scores = set()
for path in engine.rglob('*.cs'):
    engine_scores.update(s.lower() for s in re.findall(r'\be_\w+', path.read_text(encoding='utf-8-sig')))
unreferenced_quest_scores = sorted(quest_scores - awarded_scores - engine_scores)
counts['journal_score_references'] = len(quest_scores)

result = dict(counts=counts, missing_writers=sorted(missing, key=lambda x: (x['family'], x['key'])),
              orphan_repeat_rules=orphan_repeat_rules, broken_calls=broken_calls, unknown_scores=unknown_scores,
              unreferenced_quest_scores=unreferenced_quest_scores, calls=calls)
options.output_json.write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(counts, indent=2))
print('Candidate missing writers:', len(missing), 'Candidate broken calls:', len(broken_calls))
print('Candidate orphan repeat rules:', len(orphan_repeat_rules), 'Unknown score events:', len(unknown_scores))
print('Journal score requirements without script/engine references:', unreferenced_quest_scores)
if options.check:
    baseline = json.loads(Path(__file__).with_name('story-state-audit-baseline.json').read_text(encoding='utf-8'))
    candidates = {'missing:' + r['family'] + ':' + '|'.join(r['key']) for r in missing}
    candidates.update('repeat:' + r['source'] + ':' + '|'.join(r['key']) for r in orphan_repeat_rules)
    new = sorted(candidates - baseline.keys())
    for candidate in new:
        print('UNREVIEWED:', candidate)
    if new or broken_calls or unknown_scores or unreferenced_quest_scores:
        sys.exit(1)
