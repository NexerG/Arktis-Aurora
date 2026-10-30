---
name: aurora-pdfdiff
description: Compare Aurora's LaTeX PDF export against the LuaLaTeX reference cheaply — one command, `python _Build/pdfdiff.py <ref> <ours>`, prints only differing lines, page breaks and baseline shifts plus a one-line summary. Use for any thesis / Tex.ExportPdf check, page-break or line-spacing question, "does our PDF match LaTeX", and before writing a text-position verification claim. Never dump pdftotext output instead.
---

# Comparing our PDF against LaTeX

```bash
PYTHONIOENCODING=utf-8 python _Build/pdfdiff.py <ref.pdf> <ours.pdf> [--pages 11-14] [--tol 0.5] [--max 40]
```

- Needs only `pypdfium2` (installed). No pdfplumber, no page dumps.
- Output: one line per difference, then always one summary line —
  `pages N/M, I/P pages identical, T text diffs, K position diffs > tol pt, first divergence pX`.
  - `pX break: ref starts "…", ours "…"` — the page begins on a different line (a page-break difference).
  - `pX lines A vs B` — line counts differ.
  - `pX Ln text: "ref" ≠ "ours"` — line text differs (∅ = missing on that side).
  - `pX Ln y R vs O (+d) "…"` — same text, baseline moved more than `--tol` pt (measured from page top).
  A position line is printed only where the offset changes, so one shifted line under a run of followers costs
  one line; `(+0.0)` marks where it falls back into step. Line matching ignores spaces.
- Exit 0 = identical within tolerance, 1 = differences.
- Cost: full thesis at `--max 40` is about 1.3k tokens; one or two pages about 400.
- Extraction noise, not engine bugs: TeX's stacked delimiters (matrix brackets `⎡ ⎢ ⎣`) extract as blank or split
  lines in the reference, so matrix rows never match line for line — check those against the rendered page.
- That output is the whole answer. Start full-document, then rerun with `--pages` on the first divergence. One
  break shifts every later page, so read the first divergent page, not the count.

## The thesis

Folder `C:\Projects-Repositories\Thorium Notes\BakisTest\`:
- **Reference:** `Real_Time_Global_Illumination_in_Video_Games.pdf` (LuaTeX, Producer `LuaTeX-1.18.0`). Never overwrite it.
- **Ours:** `Sablonas.pdf` (Producer `Aurora`) — `Tex.ExportPdf` writes it next to `Sablonas.tex`.

Fresh export, headless, from Thorium's Debug bin (build first):

```bash
Thorium.exe "C:\Projects-Repositories\Thorium Notes\BakisTest\Sablonas.tex" --exec "Wait 6000; Focus; Tex.ExportPdf; Wait 6000; Quit"
```

- Check the export's timestamp changed before comparing; a stale `Sablonas.pdf` compares an older build.
- Any `[Vulkan]` line in the run stops the work (CLAUDE.md §10).
- Known gaps live in `ClaudeMemory/Decisions/latex-editor.md` ("Known gaps"); compare the diff against that list
  before calling a difference new.

## Claims

- A clean or explained diff is **shot-verified (text positions)**, the wording `latex-editor.md` already uses. It
  says nothing about glyph shapes, colour or images — pdfdiff compares text and baselines only.
