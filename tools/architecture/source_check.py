#!/usr/bin/env python3
"""Review-sensitive source sites. Exact per-file token counts ratchet; no file-wide exemptions."""
import collections
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
# Mask comments and literal contents while preserving newlines/offsets. Interpolated expressions are
# deliberately retained: the compiler check covers them too; a clock inside interpolation is still a site.
MASK = re.compile(r'//[^\n]*|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'', re.S)
PATTERNS = {
    "engine-scale": r'\bTimeScale\s*(?:\?\?=|[+*/-]?=(?!=)|\+\+|--)',
    "engine-catchup": r'\bMaxPhysicsStepsPerFrame\s*(?:\?\?=|[+*/-]?=(?!=)|\+\+|--)',
    "tree-pause": r'\.\s*Paused\s*(?:\?\?=|[&|^]?=(?!=))',
    "shader-global": r'\bGlobalShaderParameter(?:Set|SetOverride|Add|Remove)\s*\(',
    "direct-quit": r'\b(?:Quit|Environment\s*\.\s*(?:Exit|FailFast))\s*\(',
    "timer": r'\b(?:Task\s*\.\s*Delay|CreateTimer|Thread\s*\.\s*Sleep)\b|\bnew\s+(?:(?:System\.)?(?:Threading|Timers)\.|Godot\.)?(?:PeriodicTimer|Timer)\b',
    "wall-clock": r'\b(?:Time\s*\.\s*Get\w+|DateTime(?:Offset)?\s*\.\s*(?:UtcNow|Now|Today)|Stopwatch\s*\.\s*(?:GetTimestamp|GetElapsedTime|StartNew)|Environment\s*\.\s*TickCount(?:64)?|new\s+(?:System\.Diagnostics\.)?Stopwatch|TimeProvider\s*\.\s*System)\b|\bStopwatch\s+\w+\s*=',
    "worker-entry": r'\b(?:Task\s*\.\s*(?:Run|Factory\s*\.\s*StartNew)|ThreadPool\s*\.\s*(?:QueueUserWorkItem|UnsafeQueueUserWorkItem)|new\s+(?:System\.Threading\.)?Thread)\s*\(',
}


def sites(sources):
    found = collections.Counter()
    locations = {}
    for path, source in sources.items():
        # Preserve interpolation expressions (including nested quoted strings) conservatively. Metadata
        # checks supply the semantic backstop; this lexical check also catches unused source references.
        def mask(match):
            text = match.group()
            if match.start() and source[match.start() - 1] == '$':
                return text
            return re.sub(r'[^\n]', ' ', text)
        code = MASK.sub(mask, source)
        # Resolve simple type aliases before matching, including global-qualified aliases.
        for alias, target in re.findall(r'\busing\s+(\w+)\s*=\s*(?:global::)?([\w.]+)\s*;', code):
            code = re.sub(r'\b' + re.escape(alias) + r'\b', target, code)
        for target in re.findall(r'\busing\s+static\s+(?:global::)?([\w.]+)\s*;', code):
            members = {'Task': r'Delay|Run|Factory', 'Thread': r'Sleep', 'Time': r'Get\w+',
                       'DateTime': r'Now|UtcNow|Today', 'DateTimeOffset': r'Now|UtcNow|Today',
                       'Environment': r'TickCount|TickCount64', 'Stopwatch': r'GetTimestamp|GetElapsedTime|StartNew'}
            short = target.split('.')[-1]
            if short in members:
                code = re.sub(r'(?<![\w.])(' + members[short] + r')\b', short + r'.\1', code)
        if not path.startswith('src/Dev/') and re.search(r'\bPropane\s*\.\s*Dev\b|\bDev\s*\.[A-Z]|\bDevArgs\b', code):
            key = ('runtime-dev-source', path, 'Propane.Dev')
            found[key] += 1
            space = re.search(r'namespace\s+([\w.]+)', code)
            locations[key] = f'{path}:1 [{space[1] + "." if space else ""}{pathlib.Path(path).stem}]'
        for rule, pattern in PATTERNS.items():
            for match in re.finditer(pattern, code):
                if rule == 'direct-quit':
                    prefix = code[:match.start()]
                    if re.search(r'\b(?:ShutdownGuard|AnimationReview)\s*\.\s*$', prefix):
                        continue
                    # A declaration named Quit isn't an application quit call.
                    if re.search(r'\b(?:void|int|bool)\s+$', prefix):
                        continue
                token = re.sub(r'\s+', '', match.group())
                key = (rule, path, token)
                found[key] += 1
                line = code.count('\n', 0, match.start()) + 1
                space = re.search(r'namespace\s+([\w.]+)', code)
                declarations = re.findall(r'\b(?:class|struct|record|interface)\s+(\w+)', code[:match.start()])
                name = (space[1] + '.' if space else '') + (declarations[-1] if declarations else pathlib.Path(path).stem)
                locations[key] = f'{path}:{line} [{name}]'
    return found, locations


def check(sources, baseline):
    actual, locations = sites(sources)
    failures = []
    for key, count in sorted(actual.items()):
        if baseline.get(key) != count:
            failures.append(f'NEW {locations[key]}: {key[0]} {key[2]} count={count}, approved={baseline.get(key, 0)}')
    for key in sorted(baseline.keys() - actual.keys()):
        failures.append(f'STALE source site (remove it): {" ".join(key)}')
    return failures


def main():
    sources = {p.relative_to(ROOT).as_posix(): p.read_text() for p in (ROOT / 'src').rglob('*.cs')}
    if sys.argv[1:] == ['candidate']:
        for key, count in sorted(sites(sources)[0].items()):
            print('\t'.join((*key, str(count), 'REVIEW', 'Supply clock domain/owner and exit condition')))
        return 0
    baseline = {}
    lines = (ROOT / 'tools/architecture/source-sites.tsv').read_text().splitlines()
    if lines != sorted(lines) or len(lines) != len(set(lines)):
        raise ValueError('source-sites.tsv must have unique sorted lines')
    for line in lines:
        rule, path, token, count, owner, reason = line.split('\t')
        if not owner or owner == 'REVIEW' or not reason or rule == 'runtime-dev-source':
            raise ValueError('invalid source approval: ' + line)
        key = (rule, path, token)
        if key in baseline:
            raise ValueError('duplicate source approval: ' + line)
        baseline[key] = int(count)
    failures = check(sources, baseline)
    for failure in failures:
        print(failure, file=sys.stderr)
    print(f'Architecture source: {len(baseline)} approved sites, {len(failures)} failures')
    return bool(failures)


if __name__ == '__main__':
    sys.exit(main())
