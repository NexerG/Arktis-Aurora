---
name: aurora-docs
description: Write up a landed change into Aurora's documentation — ClaudeMemory notes, the Obsidian vault under Internal/DOCUMENTATION/Engine, and the Work in Progress List. Use after a system or decision lands, or whenever asked to update the docs, write this up, or record a decision. Three destinations with three different house styles that are easy to mix up.
---

# Writing up a change

The writing happens in the `aurora-scribe` subagent, not here. This session holds the reasoning; the scribe
holds the house styles and starts from a small context. Why: `ClaudeMemory/Decisions/docs-pass-in-subagent.md`.

Do not read the docs indexes, the WIP list or vault pages in this session to prepare — the scribe finds the
places. Three steps.

## 1. Write the brief

The brief is the only thing the scribe knows. Anything missing from it is missing from the docs, so this is
where the judgment goes. Fill every heading; write `none` rather than dropping one.

```
## Landed
One or two sentences: what now exists or behaves differently.

## Symbols
Namespace + type + member for everything added, renamed, moved or removed. Old → new for renames.
Data XML files read or added.

## Decisions
Per choice: the claim; the alternatives rejected and what each would have cost; any measurement, with units.
Name an existing Decisions/ note if this extends or supersedes it.

## Verification
The honest word from aurora-verify (builds clean / test- / golden- / shot- / GUI-verified) and what was
actually checked. What was not checked.

## Known gaps
Not done, not verified, inconsistent.

## Destinations
- Decisions/ note: new `<name>` | extend `<name>` | none
- Context/where-things-live.md: concept row to add or correct | none
- Context/ui-orientation.md: component entry | none
- Vault: page(s) under Internal/DOCUMENTATION/Engine or Extras | none
- WIP list: entry to land (quote its headline) / open follow-ups to add | none
- Plan file: Context/<name>-plan.md to create or append | none
- Retractions: earlier claims now wrong, and where they were written | none
```

A repaired bug with no decision behind it gets no brief — the docs just describe the fixed behaviour.

## 2. Spawn the scribe

`Agent` with `subagent_type: "aurora-scribe"` and the brief as the prompt, `run_in_background: false`. One
scribe per write-up.

## 3. Check what came back

Its report is not evidence (CLAUDE.md §8).

- `git -C Internal diff --stat -- DOCUMENTATION`, then `git -C Internal diff -- DOCUMENTATION` on the files that matter.
- Each `GAP` or `CONFLICT` line in the report: answer it with an `Edit` here, or tell the user.
- Read the vault prose for voice; Sonnet's vault prose can need a sentence fixed.
- The verification word in the WIP / Changelog entry matches the brief exactly.
- A WIP / Changelog change has its twin in `D:\Repositories\ThoriumNotes\*.planner.axml`.
