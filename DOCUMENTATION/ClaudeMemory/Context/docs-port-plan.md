# Port the docs vault to Thorium .axml — stages 1 and 2 landed, 3 to 5 not started

**Agreed:** 2026-10-31/11-01. **Stage 1 landed 2026-11-01** (test-verified, NOT GUI-verified). **Stage 2 landed 2026-11-01** (test-verified, NOT GUI-verified).
**What was built and why:** `../Decisions/note-links.md` (stage 2), `../Decisions/note-file-formats.md` (stage 1, pipe tables).
**Checklist form:** the "port the docs vault to Thorium .axml" entry in `DOCUMENTATION/Work in Progress List.md`.

## Stages

| Stage | Content | Status |
|---|---|---|
| 1 | Markdown pipe tables read into `<Table>` and write back (`MarkdownFormat` `ReadTable`, `TableLines`) | landed 2026-11-01 |
| 2 | Note links as `<Run Text Link>`: Ctrl+click follow, typed `]]`, `[[` autocomplete, rename rewrites | landed 2026-11-01 |
| 3 | Table of contents: live `<Toc/>` block | not started |
| 4 | Vault "Convert to .axml" action | not started |
| 5 | Run on `DOCUMENTATION/Engine` + `Extras`; manual fix-ups: excalidraw → PNG, `.base`, mermaid stays code, callout → quote, 44 HTML lines | not started |

## Forks chosen
- F1a — only `Engine/` and `Extras/` are ported; ClaudeMemory, WIP and Changelog stay Markdown.
- F2a — ToC is a live `<Toc/>` block.
- F3a — follow a link with Ctrl+click.
- F4a — arxtis.dev gets an `.axml` reader (`scripts/fetch-docs.mjs` reads `DOCUMENTATION/Engine/*.md` today).
- F5a — the `aurora-docs` skill and `aurora-scribe` write `.axml` in the same house style.
- F6a — typing a closing `]]` converts `[[target|alias]]` to a link.
- F7a — `[[a#h]]` shows its target verbatim.
- F8b — after `#` the autocomplete lists the target's headings.
- F9c — editing a heading's text rewrites links to it.

## Known gaps carried
- Column spans do not survive Markdown (written as the cell plus empty cells).
- Folder moves do not rewrite path-qualified links.
