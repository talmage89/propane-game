#!/usr/bin/env python3
"""Check that every relative link and image in the tracked Markdown files resolves.

A target must be a tracked file (or a directory holding tracked files), and a #fragment must name a heading, or an
explicit id/name anchor, in the Markdown file it points into. External links (a scheme such as https:) are not fetched.
Standard library only; run it from anywhere inside the repository: python3 -B tools/dev/check_links.py
"""
import pathlib
import re
import subprocess
import sys
import urllib.parse


ROOT = pathlib.Path(__file__).resolve().parents[2]

FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
CODE_SPAN = re.compile(r"(`+)((?:(?!\n[ \t]*\n).)+?)\1", re.DOTALL)
HEADING = re.compile(r"^ {0,3}#{1,6}[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$")
# [text](target "title") and ![alt](target): the text may wrap lines and hold one level of brackets.
INLINE = re.compile(r"\[(?:[^\[\]]|\[[^\[\]]*\])*\]\(\s*(<[^<>\n]*>|[^\s()]*(?:\([^\s()]*\)[^\s()]*)*)(?:\s+(?:\"[^\"]*\"|'[^']*'))?\s*\)")
DEFINITION = re.compile(r"^ {0,3}\[[^\]\n]+\]:[ \t]*(<[^<>\n]*>|\S+)", re.MULTILINE)
HTML_ATTRIBUTE = re.compile(r"""<[a-zA-Z][^<>]*?\b(?:src|href)\s*=\s*(?:"([^"]*)"|'([^']*)')""")
HTML_ANCHOR = re.compile(r"""<[a-zA-Z][^<>]*?\b(?:id|name)\s*=\s*(?:"([^"]*)"|'([^']*)')""")
SCHEME = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.-]*:")
LINE_ANCHOR = re.compile(r"^L\d+(?:C\d+)?(?:-L\d+(?:C\d+)?)?$")


def blank_code(text):
    """The text with fenced blocks and inline code spans blanked out (same length, so offsets and line numbers hold)."""
    lines, fence = [], None
    for line in text.split("\n"):
        match = FENCE.match(line)
        if fence is None and match:
            fence = match.group(1)[0] * len(match.group(1))
            lines.append(" " * len(line))
        elif fence is not None:
            if match and match.group(1).startswith(fence) and not line.strip().strip(fence[0]):
                fence = None
            lines.append(" " * len(line))
        else:
            lines.append(line)
    text = "\n".join(lines)
    return CODE_SPAN.sub(lambda m: re.sub(r"[^\n]", "x", m.group(0)), text)


def slug(heading):
    """GitHub's anchor for a heading: markup stripped, lowercased, punctuation dropped, spaces to hyphens."""
    heading = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", heading)
    heading = re.sub(r"<[^>]+>", "", heading).replace("`", "")
    heading = re.sub(r"[*~]", "", heading).lower()
    return re.sub(r"[^\w\- ]", "", heading).replace(" ", "-")


def anchors(text):
    """Every fragment a link into this Markdown text may use."""
    found, seen = set(), {}
    stripped = blank_code(text)
    for line, original in zip(stripped.split("\n"), text.split("\n")):
        match = HEADING.match(original) if line.strip() else None
        if match:
            base = slug(match.group(1))
            count = seen.get(base, 0)
            seen[base] = count + 1
            found.add(base if count == 0 else f"{base}-{count}")
    for match in HTML_ANCHOR.finditer(stripped):
        found.add(match.group(1) or match.group(2))
    return found


def targets(text):
    """(line, raw target) for every link, image and definition outside code."""
    stripped = blank_code(text)
    found = []
    for pattern in (INLINE, DEFINITION):
        for match in pattern.finditer(stripped):
            found.append((match.start(1), match.group(1)))
    for match in HTML_ATTRIBUTE.finditer(stripped):
        group = 1 if match.group(1) is not None else 2
        found.append((match.start(group), match.group(group)))
    return [(text.count("\n", 0, offset) + 1, raw) for offset, raw in sorted(found)]


def check(files, read):
    """Problems for the tracked `files` (repository-relative posix paths); `read(path)` returns a file's text."""
    tracked = set(files)
    directories = {parent.as_posix() for path in tracked for parent in pathlib.PurePosixPath(path).parents}
    cache = {}

    def fragments(path):
        if path not in cache:
            cache[path] = anchors(read(path))
        return cache[path]

    problems = []
    for source in sorted(path for path in tracked if path.endswith(".md")):
        for line, raw in targets(read(source)):
            target = raw[1:-1] if raw.startswith("<") else raw
            target = target.strip()
            if not target or SCHEME.match(target) or target.startswith("//"):
                continue
            path, _, fragment = target.partition("#")
            path = urllib.parse.unquote(path.partition("?")[0])
            fragment = urllib.parse.unquote(fragment)
            if not path:
                resolved = source
            else:
                base = pathlib.PurePosixPath(source).parent
                joined = pathlib.PurePosixPath(path[1:]) if path.startswith("/") else base / path
                parts = []
                for part in joined.parts:
                    if part == "..":
                        if not parts:
                            parts = None
                            break
                        parts.pop()
                    elif part != ".":
                        parts.append(part)
                resolved = "/".join(parts) if parts is not None else None
                if resolved is None:
                    problems.append(f"{source}:{line}: {raw} leaves the repository")
                    continue
                resolved = resolved or "."
            if resolved not in tracked and resolved not in directories and resolved != ".":
                problems.append(f"{source}:{line}: {raw} is not a tracked file")
            elif fragment and resolved in tracked:
                if resolved.endswith(".md"):
                    if fragment not in fragments(resolved):
                        problems.append(f"{source}:{line}: {raw} has no heading #{fragment} in {resolved}")
                elif not LINE_ANCHOR.match(fragment):
                    problems.append(f"{source}:{line}: {raw} points into {resolved}, which has no anchors")
    return problems


def main():
    listing = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, check=True, stdout=subprocess.PIPE).stdout
    files = [name for name in listing.decode().split("\0") if name and (ROOT / name).is_file()]
    markdown = [name for name in files if name.endswith(".md")]
    problems = check(files, lambda path: (ROOT / path).read_text(encoding="utf-8"))
    for problem in problems:
        print(problem)
    if problems:
        print(f"FAIL: {len(problems)} broken Markdown link(s)", file=sys.stderr)
        return 1
    print(f"PASS: every relative link and image in {len(markdown)} tracked Markdown files resolves")
    return 0


if __name__ == "__main__":
    sys.exit(main())
