---
name: aurora-orient
description: Find your way around Aurora without spending the session's context on orientation. Use at the start of any change, whenever you are about to read an index, a large source file, the WIP list, or a build log — the index files CLAUDE.md tells you to start from total over 100 KB, and every one of them is a grep target, not a read target.
---

# Orienting cheaply

Every turn re-sends the whole conversation. A file read at turn 5 is still being paid for at turn 40, so the
cost of a read is its size times the turns remaining — not its size once. This is why the budget in CLAUDE.md
§2 exists, and this is the arithmetic behind it.

## What the orientation files actually cost

| File | Treat as |
|---|---|
| `Internal/DOCUMENTATION/Work in Progress List.md` | one phase at a time, below |
| `CLAUDE.md` | already in context, free |
| `Context/where-things-live.md`, `NAMESPACES.md`, `Thorium/NAMESPACES.md`, `Decisions/INDEX.md` | **grep only** |

`Context/` and `Decisions/` are under `Internal/DOCUMENTATION/ClaudeMemory/`.

Each index is tens of KB and growing (`wc -c` for today's sizes); reading them costs ~25k tokens before
any analysis, grepping them ~1k.

## The chain

Stop at the first rung that answers. Do not skip to a whole-tree grep.

| # | Question | Where | How |
|---|---|---|---|
| 1 | which decision settles this? | `Decisions/INDEX.md` | grep — one row per note, each names its Key symbols |
| 2 | how does this system work? | its vault page, `Internal/DOCUMENTATION/Engine/` | `grep -n '^#'`, then `sed` the section |
| 3 | where does this concept live? | `Context/where-things-live.md` | grep the concept |
| 4 | which file holds the symbol? | `NAMESPACES.md`, `Thorium/NAMESPACES.md` | grep the namespace |
| 5 | what methods does the class have? | the file | method grep, below |
| 6 | what does the method do? | the file | `sed -n 'START,ENDp'` |

A row in `INDEX.md` that does not match the task means that note does not need opening. That is what the
"Settles" column is for.

**There is no method index and there should not be one.** It is a grep — on a large class, ~40 lines
out for a 1,000+ line file, and it cannot go stale:

```bash
grep -nE '(public|private|internal|protected).*\(.*\)\s*$' path/to/File.cs
```

A written index would rot exactly the way `Patterns/finding-code.md` says recorded paths rot, and faster.

**UI work starts at `Context/ui-orientation.md`** — one entry per `UI` component: what it does, its XML
element, entry points, region names. It is a component index, not a method index: the entry says which region
to `sed`, and methods stay a grep. Read the entry, not the source file; dig into code only when the entry is
not enough.

**A note over ~10 KB is read like a source file** — `grep -n '^#' note.md`, then `sed` the one section.
`Decisions/ui-engine-stack.md` is 48 KB and ordered by landing, not topic; the orientation file links its
sections, never the whole note.

## When a whole-file read is right

Almost never. 225 `.cs` files, 1.88 MB, largest 1810 lines, median 8.4 KB. CLAUDE.md §2's three cases —
changing control flow, changing a type's shape, working somewhere the indexes do not name — widen the read to
the regions the change touches or calls into, not to the file. Map it, then read only those regions:

```bash
grep -nE '#region|(public|private|internal|protected).*\(' path/to/File.cs
sed -n 'START,ENDp' path/to/File.cs
```

An off-topic region stays unread — `Control`'s authored XML properties were no input to context menus, and
reading all 702 lines of it cost more than any other read in that session. Read end to end only when the map
cannot say which regions matter.

Otherwise: grep the symbol, `sed` the window around the hit, and change the lines you came for. "No plan built
on a guess" means do not guess about the lines you are changing — not read 1,200 to change 3.

**Do not re-read a file you just edited.** `Edit` errors if it did not apply.

## The WIP list

Landed entries live in `Internal/DOCUMENTATION/Changelog.md` and multi-part plans in `ClaudeMemory/Context/*-plan.md`,
so the list is open work only. One phase is usually all you want:

```bash
grep -n '^# ' "Internal/DOCUMENTATION/Work in Progress List.md"          # phase line numbers
sed -n 'START,ENDp' "Internal/DOCUMENTATION/Work in Progress List.md" | grep '\[ \]'   # one phase, open only
```

Tripwire, one call, ~20 tokens — if this is over 40 KB the file needs curating (see `aurora-docs`):

```bash
wc -c "Internal/DOCUMENTATION/Work in Progress List.md"
```

## Build output

A full `dotnet build` log is thousands of tokens of restore chatter; a `--no-incremental` one is 900 KB. Build
through the filter script (default target is the solution; pass a `.csproj` or `--rebuild` to change it):

```bash
cd "$(git rev-parse --show-toplevel)" && _Build/build.sh
```

A clean build is then **one line**, `BUILD OK`. A failure prints deduplicated errors, a `LOCKED:` line when a
running app holds a DLL, and the path of the full log. Do not add `warning CS` to that filter — the tree carries dozens of
pre-existing nullable warnings and they bury the result. Ask for warnings deliberately, in their own command,
scoped to the file you touched.

Same rule for any long command: pipe it through `grep`, `tail`, or `wc -l` before it reaches context.

## Handing the reading to someone else

When the reading is much larger than the answer and you do not know where to look, that is `aurora-handoff`
and `aurora-scout`. When you already know the file, read it yourself — a spawn costs more than the read.
