#!/usr/bin/env python3
"""Self-test for check_links.py: it accepts what resolves and rejects each kind of broken Markdown link."""
import pathlib
import subprocess
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import check_links


def require(condition, message):
    if not condition:
        raise SystemExit(f"FAIL: {message}")


def problems(files, **texts):
    return check_links.check(files, lambda path: texts[path.replace("/", "__").replace(".", "_")])


def main():
    good = {
        "README_md": "# Top\n[doc](docs/a.md) [section](docs/a.md#the-second-part-2) [dir](docs/) [here](#top)\n"
                     "![pic](docs/pic.png) [code](src/x.cs#L10-L20) [web](https://example.org/a.md#nope) [mail](mailto:a@b.c)\n"
                     "[wrapped\ntext](docs/a.md#a) <https://example.org>\n",
        "docs__a_md": "# A\n## The `second` part (2)\n## The `second` part (2)\n[back](../README.md#top) [up](./b.md)\n"
                      "<a id=\"custom\"></a> [custom](#custom)\n",
        "docs__b_md": "[x](a.md#a)\n",
    }
    files = ["README.md", "docs/a.md", "docs/b.md", "docs/pic.png", "src/x.cs"]
    require(problems(files, **good) == [], "valid links were rejected: " + "; ".join(problems(files, **good)))
    # Duplicate headings number from -1; punctuation, backticks and parentheses fall out of a heading's anchor.
    require(check_links.anchors("## The `second` part (2)\n## The `second` part (2)\n") ==
            {"the-second-part-2", "the-second-part-2-1"}, "heading anchors differ from GitHub's")

    def broken(kind, **replaced):
        texts = dict(good, **replaced)
        found = problems(files, **texts)
        require(len(found) == 1 and kind in found[0], f"expected exactly one problem naming {kind!r}: {found}")

    broken("not a tracked file", README_md="# Top\n[gone](docs/missing.md)\n")
    broken("not a tracked file", README_md="# Top\n![gone](docs/missing.png)\n")
    broken("not a tracked file", README_md="# Top\n[moved][ref]\n\n[ref]: docs/missing.md\n")
    broken("not a tracked file", README_md="# Top\n<img src=\"docs/missing.png\">\n")
    broken("not a tracked file", docs__a_md=good["docs__a_md"] + "[up](../b.md)\n")
    broken("has no heading #nowhere", README_md="# Top\n[bad](docs/a.md#nowhere)\n")
    broken("has no heading #nowhere", README_md="# Top\n[bad](#nowhere)\n")
    broken("which has no anchors", README_md="# Top\n[bad](docs/pic.png#frame)\n")
    broken("leaves the repository", README_md="# Top\n[bad](../outside.md)\n")
    # Links and headings inside code are not links and headings.
    ignored = dict(good, README_md="# Top\n```\n[gone](docs/missing.md)\n# Fake\n```\n`[gone](docs/missing.md)`\n")
    require(problems(files, **ignored) == [], "links inside code were checked")
    require("fake" not in check_links.anchors("```\n# Fake\n```\n"), "a heading inside a code fence was an anchor")
    # A file that exists but is not tracked does not count.
    require(len(problems(["README.md", "docs/a.md", "docs/b.md", "src/x.cs"], **good)) == 1, "an untracked image resolved")

    # The command itself, in a scratch repository: exit status and the line that names the broken link.
    with tempfile.TemporaryDirectory(prefix="wptest-links-") as directory:
        scratch = pathlib.Path(directory)
        (scratch / "docs").mkdir()
        (scratch / "tools/dev").mkdir(parents=True)
        (scratch / "tools/dev/check_links.py").write_text((pathlib.Path(check_links.__file__)).read_text())
        (scratch / "README.md").write_text("# R\n[a](docs/a.md)\n")
        (scratch / "docs/a.md").write_text("# A\n")

        def command():
            return subprocess.run([sys.executable, "-B", "tools/dev/check_links.py"], cwd=scratch, text=True,
                                  stdout=subprocess.PIPE, stderr=subprocess.STDOUT)

        subprocess.run(["git", "init", "-q"], cwd=scratch, check=True)
        subprocess.run(["git", "add", "-A"], cwd=scratch, check=True)
        result = command()
        require(result.returncode == 0 and "PASS" in result.stdout, "the command rejected a good tree: " + result.stdout)
        (scratch / "README.md").write_text("# R\n[a](docs/a.md)\n[b](docs/b.md)\n")
        result = command()
        require(result.returncode != 0 and "README.md:3: docs/b.md is not a tracked file" in result.stdout,
                "the command accepted a broken link: " + result.stdout)
        (scratch / "docs/b.md").write_text("# B\n")
        result = command()
        require(result.returncode != 0, "the command accepted a link to an untracked file: " + result.stdout)
        subprocess.run(["git", "add", "-A"], cwd=scratch, check=True)
        require(command().returncode == 0, "the command rejected a link to a newly staged file")
    print("PASS: the Markdown link check accepts valid links and rejects missing files, images, headings and untracked targets")


if __name__ == "__main__":
    main()
