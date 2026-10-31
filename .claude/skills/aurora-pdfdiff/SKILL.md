---
name: aurora-pdfdiff
description: Compare Aurora's LaTeX PDF export of any .tex against its LaTeX reference PDF cheaply — one command, `_Build/pdfcheck.sh <file.tex> <reference.pdf>`, builds, exports headless and prints only real differences (reflows, text, page breaks, baseline shifts, page overflow) plus a one-line summary. Use for any Tex.ExportPdf check (the thesis, the CV, any document), page-break or line-spacing question, "does our PDF match LaTeX", and before writing a text-position verification claim. Never dump pdftotext output instead.
---

# Comparing our PDF against LaTeX

```bash
_Build/pdfcheck.sh <file.tex> <reference.pdf> [--pages 11-14] [--tol 0.5] [--max 40] [--shot 27[:500-842]] [--raw] [--strict]
```

Builds Debug, exports `<file>.tex` headless to `<file>.pdf` beside it, prints any `[Vulkan]` line (stops the work,
CLAUDE.md §10), fails if that PDF was not rewritten, then runs `_Build/pdfdiff.py <ref> <ours>` with the arguments.
Refuses when `<file>.pdf` is the reference itself. To diff without re-exporting:
`PYTHONIOENCODING=utf-8 python _Build/pdfdiff.py <ref.pdf> <ours.pdf> [args]`.

## Known documents
Never overwrite a reference.

| Document | `.tex` | Reference (engine) |
|---|---|---|
| Thesis | `C:/Projects-Repositories/Thorium Notes/BakisTest/Sablonas.tex` | `…/BakisTest/Real_Time_Global_Illumination_in_Video_Games.pdf` (LuaTeX) |
| CV | `C:/Projects-Repositories/Thorium Notes/CVTest/Gytis-Mockevicius-Resume.tex` | `…/CVTest/Gytis_Mockevicius_Resume.pdf` (pdfTeX) |

## Output
- `pX overflow: last line Y below the text bottom B` — our page runs past the lowest reference baseline.
- `pX break: ref starts "…", ours "…"` — the page begins on a different line.
- `pX La-b reflow r/o lines: ref "…tail|head…" ours "…"` — same words, broken differently; shows the first line
  end the two do not share.
- `pX Ln text: "ref" ≠ "ours"` — text differs (∅ = missing on that side).
- `pX Ln y R vs O (+d) "…"` — same line, baseline moved more than `--tol`; printed only where the offset changes,
  `(+0.0)` is where it falls back into step.
- Summary, always: `pages, identical, reflow, text, pos, overflow, noise, first divergence`. Exit 0 unless `--strict`.
- `--shot` writes ref | ours side by side as a PNG in `%TEMP%\aurora-pdfdiff\` and prints the path; read that
  image instead of writing a render script. With `--shot` and no `--pages` nothing else runs.

## Filtered as extraction noise (counted, not printed; `--raw` turns it off)
Hyphens and minus signs (the reference drops them), `⋅`/`·`, `∘`/`◦`, our bullet (no text), and changed lines whose
characters match once stacked delimiters, big operators (`Z X ∫ ∑ ⎡⎢⎣`) and invisible glyphs are removed —
matrices, limits and integrals. Look at those with `--shot`.

Not filtered: pdfTeX (OT1/T1 without ToUnicode) writes accented letters as a base letter plus a spacing accent, so a
pdfTeX reference reads `Mockevicˇius` where ours reads `Mockevičius`. Those `text` lines are extraction noise too.

## Reading it
- Start full-document; one break shifts every later page, so read the first divergent page, not the count.
- Known gaps live in `ClaudeMemory/Decisions/latex-editor.md` ("Known gaps"); check before calling one new.

## Claims
A clean or explained diff is **shot-verified (text positions)**. It says nothing about glyph shapes, colour or images.
