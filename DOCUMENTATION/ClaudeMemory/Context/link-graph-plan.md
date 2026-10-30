# Link graph across vaults — stages 1 to 4 landed, stage 5 not built

**Agreed:** 2026-10-25 (user-confirmed design). **Stage 1 landed 2026-10-25** (test-verified, NOT GUI-verified). **Stage 2 landed 2026-10-25** (test-verified, NOT GUI-verified). **Stage 3a landed 2026-10-25** (test-verified, NOT GUI-verified); stage 3 was split (J1). **Stage 3b landed 2026-10-25** (test-verified, NOT GUI-verified) — stage 3 is complete. **Stage 4 landed 2026-10-26** (test-verified, NOT GUI-verified), built as one stage.
**What was built and why:** `../Decisions/link-graph.md`.
**Checklist form:** the "Link graph across vaults" entry in `DOCUMENTATION/Work in Progress List.md`.

## Stages

| Stage | Content | Status |
|---|---|---|
| 1 | Vault id + manifest + `KnownVault.Id`: `VaultManifest`, `KnownVaults.PathOf(id)`, vault scans skip dot-folders | landed 2026-10-25 |
| 2 | Link index, data only | landed 2026-10-25 |
| 3a | Cross-vault references: `@Alias/` resolution, referenced vaults in the sidebar, Locate, Add referenced vault, `#VAULT!` | landed 2026-10-25 |
| 3b | Cross-vault rewrites (G2) + Paste link writing `@Alias/` | landed 2026-10-25 |
| 4 | Graph view + backlinks UI | landed 2026-10-26 |
| 5 | Server vaults | design only until a server exists |

## Stage 2 — link index, data only (landed 2026-10-25)
- `ArctisAurora.Core.UI.Notes.LinkIndex`: edges (source, target as written, `LinkKind` Embed / Math / Formula / Property / Link, resolved path or null).
- API: `For(vault, id)`, `Close(vault)`, `Refresh()`, `Outgoing(path)`, `Incoming(path)`, `Unresolved()`, `IsSealed(path)`.
- Extraction through the editor's own parsers, `SheetFormula.FileParts(raw)` and `SheetLinks.MathReferences(source)`.
- Cache at `%LOCALAPPDATA%\Arktis\Thorium\Links\<id>.links.axml` (outside the vault, keyed per file by size + mtime); `Refresh` rescans changed files by itself, there are no save hooks.
- Renames keep their vault scan; switching them to the index is separate work.
- Stage 4 is the first caller (inspector Links section, graph view). Details: `../Decisions/link-graph.md`.

## Stage 3a — cross-vault references (landed 2026-10-25)
- `@Alias/` resolution: the referencing vault's manifest alias -> id -> `KnownVaults` path -> that folder becomes home. Lands in `VaultBrowserControl.FindSheet` / `FindNote` (private `Redirect`), which formulas, note embeds, math, `OpenLink` and `LinkIndex` all resolve through. No `SheetFormula` parser change was needed.
- "Add referenced vault..." in `Vault.menu.axml` (`Vault.AddReference`: FolderPicker -> manifest `AddDependency`).
- Sidebar groups per G1 (own group, tagged "via A"), transitive (J4), built by `VaultWindows.Sync`.
- A missing vault shows by its alias with "not on this machine" and a Locate... button; a note embed or math into it reads `#VAULT!` (J2), formulas `#REF!`.
- `SheetCalc`'s recalc graph spans vaults: a cross-vault cycle is `#CYCLE!`, no change needed (settled, test-verified).
- J1 (user): stage 3 split into 3a and 3b. J2 (user): `#VAULT!` for embeds / math, the sentence sits on the sidebar header. J3 (user): alias = stripped folder name, no prompt. J4 (user): vaults referenced by a referenced vault also show.
- Details and gaps: `../Decisions/link-graph.md`.

## Stage 3b — cross-vault rewrites (landed 2026-10-25)
- G2 across vaults: file rename, page rename and row/column insert in B rewrite `@B/` references in every open vault that references B. Matching is vault-aware (`SheetBook.Points(home, file, target)`: host callback `vaultPart`, then `Names`); on-disk files and notes come from `SheetBook.Linked(home, list)` (home's vault, then each referencing vault via host callback `referencingVaults`).
- Paste link across vaults writes `@Alias/` (`SheetBook.FilePart`, host callback `filePrefix`), adding the reference when the target vault lacks one (K1).
- K2 (user): fixes the bug where a rename in B also rewrote a loaded A sheet's own same-named reference.
- Referencing vaults that aren't open are left alone (F1).
- Details and gaps: `../Decisions/link-graph.md`.
- Still open: no way to remove or rename a vault reference; "Close vault" on a referenced group's header does nothing.
## Stage 4 — graph view + backlinks UI (landed 2026-10-26)
- Built as one stage (D1, user), not split into 4a / 4b.
- Inspector "Links" section (`LinksPanelControl`) lists a file's backlinks and outgoing links in both the docs and sheets halves; refreshed on the main thread when the target changes or after a save.
- `*.graph.axml` (`FileKind.Graph`, `GraphDocument`, `GraphEditorControl`, `GraphLayout`): an ordinary file holding filters and pins; force-directed Barnes-Hut layout, pooled panels, no new shader. A's graph shows B's files as boundary nodes only where linked (F4).
- The index is created per open vault on first ask (`LinkIndex.indexOf`) and closed on settle.
- Decisions D1-D8, P1-P2, measurements and gaps: `../Decisions/link-graph.md`. Open: `LinkIndex.Refresh` cost, unpin / saved pan-zoom / local graph, Graph in Vault.menu and row menus.

## Stage 5 — server vaults (design only until a server exists)
- `<Depends>` holds an ordered `<Source Kind Url>` list tried in turn; credentials per machine, never in the vault.
- The whole vault is mirrored under `%LOCALAPPDATA%` (F4), loaded lazily, always latest (F5).
- Resolution states: Local (folder here), Remote (server answered), Unreachable (server vault, no answer - show the mirror marked stale; formulas compute from it), Missing (no mapping for the id - Locate... / Connect...), Locked (`#LOCKED!`).
- Unreachable never offers relink; relink (F1) only when reachable and the file is gone.
- Links from a remote vault into a local one are only known if the server publishes its index.

Related: [[link-graph]], [[multi-vault-workspace]], [[sheets]], [[encryption]]
