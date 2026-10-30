# Decision — link graph between files and vaults, built on a vault id and manifest

**Date:** 2026-10-25
**Scope:** `Thorium.Editor.VaultManifest`, `Thorium.KnownVault`, `Thorium.KnownVaults`, `Thorium.Editor.CustomControls.VaultBrowserControl`, `Thorium.Tests.VaultManifestTests`, `ArctisAurora.Core.UI.LinkIndex`, `ArctisAurora.Core.UI.LinkKind`, `ArctisAurora.Core.UI.LinkEdge`, `ArctisAurora.Core.UI.SheetFormula`, `ArctisAurora.Core.UI.SheetLinks`, `ArctisAurora.Core.UI.NotePropertiesControl`, `Thorium.Tests.LinkIndexTests`, `Thorium/Data/Notes/.aurora/Vault.manifest.axml`, `Thorium/Data/XML/Documents/Tests/LinkIndex.tests.axml`, `Thorium.Editor.VaultWindows`, `ArctisAurora.Core.UI.SheetBook`, `ArctisAurora.Core.UI.SheetEditorControl`, `Thorium.Tests.CrossVaultTests`, `Thorium/Data/XML/Documents/Menus/Vault.menu.axml`, `Thorium/Data/XML/Documents/Tests/CrossVault.tests.axml`, `ArctisAurora.Core.UI.LinksPanelControl`, `ArctisAurora.Core.UI.InspectorControl`, `ArctisAurora.Core.UI.GraphDocument`, `ArctisAurora.Core.UI.GraphLayout`, `ArctisAurora.Core.UI.GraphEditorControl`, `ArctisAurora.Core.Filing.FileKind`, `Thorium.Tests.LinkGraphTests`, `Thorium/Data/XML/Documents/Tests/LinkGraph.tests.axml`

## What changed
Stages 1, 2, 3 (3a, 3b) and 4 of the five-stage plan in [[link-graph-plan]] landed (design user-confirmed 2026-10-25; stages 2, 3a and 3b landed 2026-10-25, stage 4 landed 2026-10-26). Stage 3 was split (J1) and is complete with 3b; stage 4 was built as one stage (D1); stage 5 is designed, not built.

### Stage 1

- New `Thorium.Editor.VaultManifest` (static): `PathOf(vault)`, `IdOf(vault)`, `Depends(vault)` -> `(id, alias)` list, `AddDependency(vault, id, alias)`, `AliasTarget(vault, alias)` -> id or null.
- `IdOf` creates the manifest on first ask. A locked vault adopts `VaultLock.IdOf`. It returns "" for a missing folder (never creates it) and for an unreadable or unwritable manifest.
- `AddDependency` returns false when the id or alias (alias case-insensitive) is already listed, or the manifest is unreadable.
- Private `Load` (XmlException -> `Log.Warn`, file left untouched) and `Save` (IOException / UnauthorizedAccessException -> `Log.Warn`, false). Log channel "Vault".
- File: `<vault>/.aurora/Vault.manifest.axml`, `<Vault Version="1" Id="<Guid N>"><Depends Id Alias/></Vault>`. `<Source Kind Url>` children under `<Depends>` are reserved for stage 5, not written or read yet.
- `Thorium.KnownVault.id` (`[A_XSDElementProperty("Id", "Settings", ...)]`). `KnownVaults.Remember(path)` fills `id` from `VaultManifest.IdOf` when empty, so existing entries get their id on the next open. New `KnownVaults.PathOf(id)` -> folder or null.
- `VaultBrowserControl.InDotFolder(root, path)` (internal static); `VaultNotes(home)` skips files under any dot-folder, the same rule as the browser's `AcceptsFolder`. `NoteModel.SegmentMap` skips dot-folders when scanning the Notes vault.
- Tests: `Thorium.Tests.VaultManifestTests` (`VaultManifest.Identity`, `AdoptsLockId`, `MalformedUntouched`, `Depends`, `KnownId`), suite `VaultManifest.tests.axml`.
- Default vault's manifest committed: `Thorium/Data/Notes/.aurora/Vault.manifest.axml` (written on first run).

### Stage 2 — link index, data only
Stage 4 is the first caller: the inspector's Links section and the graph view read the index (see Stage 4 below).
- New `ArctisAurora.Core.UI.LinkIndex` (sealed class), log channel "Links".
  - `enum LinkKind { Embed, Math, Formula, Property, Link }`; `readonly record struct LinkEdge(string source, string target, LinkKind kind, string? resolved)` - target as written, resolved = full path or null.
  - Host-set statics: `Func<string?, string, string?>? findNote` (home, name -> path) and `string? cacheFolder`. `Thorium.Thorium` wires `findNote = VaultBrowserControl.FindNote` and `cacheFolder = %LOCALAPPDATA%\Arktis\Thorium\Links`.
  - `static LinkIndex For(string vault, string id)` (one instance per vault, loads the cache); `static void Close(string vault)` drops the in-memory index, the cache stays on disk.
  - `Refresh()`: lists files through `SheetBook.vaultNotes` + `SheetBook.vaultSheets` (with a home path inside the vault), skips files whose size and last-write ticks are unchanged, re-reads changed ones, drops deleted ones, re-resolves every edge, saves the cache when anything changed. Resolution is one lookup per distinct target name per refresh: `SheetBook.findSheet` for Embed / Math / Formula, `findNote` for Property / Link.
  - Queries: `Outgoing(path)`, `Incoming(path)`, `Unresolved()`, `IsSealed(path)`; `public int reads { get; private set; }` = files read by the last `Refresh`.
- Extraction, de-duplicated per file:
  - Markdown: frontmatter (`Frontmatter.Split` / `Entries`) `[[...]]` values -> Property; body `![[...]]` that `SheetLinks.IsLink` -> Embed; any other `[[...]]` / `![[...]]` -> Link; `\sheet{...}` -> Math.
  - `.axml` notes: `Frontmatter` attribute -> Property; `<Run Sheet>` -> Embed; `<Run Math>` `\sheet{}` -> Math; `<Run Text>` `[[...]]` -> Link (typed embed text in a run is text, so Link).
  - Sheets: `<Cell>` text that is a formula -> `SheetFormula.FileParts` -> Formula; same-file references add nothing.
- Sealed: a password-encrypted file (`EncryptedFile.IsEncrypted`) or a `LockedFileException` is recorded `Sealed` with no edges. `XmlException` -> no edges, warning. `IOException` -> size -1, so the next refresh retries.
- Cache: `<cacheFolder>/<id>.links.axml` = `<Links Version="1"><File Path(vault-relative) Size Ticks Sealed?><Edge Kind Target/></File></Links>`; resolved paths are not stored. Neither loaded nor written when the vault has a lock file (`VaultLock.HasLock`) - G3, memory only. An unreadable cache is logged and rebuilt.
- New `SheetFormula.FileParts(string raw)` -> `List<string>`: distinct file parts of a formula's references, empty when it doesn't parse. New `SheetLinks.MathReferences(string source)` -> the references inside `\sheet{...}`.
- `VaultBrowserControl`: new `public static string? FindNote(string? home, string name)` - first file in home's vault (via `VaultOf`) whose path-without-extension or path ends with `/name`, that `FileTypes.Of` knows, not a CSV, not under a dot-folder. REMOVED the private instance `FindNote(FileObject folder, string name)` (walked the browser's tree, obeyed the workspace filter, searched `CurrentVault`). `OpenLink(string link)` -> `OpenLink(string? home, string link)`, uses the static `FindNote`.
- `NotePropertiesControl.openLink`: `Func<string, bool>` -> `Func<string?, string, bool>`; the Open button passes `editor.path` as home.
- Tests: `Thorium.Tests.LinkIndexTests` (`Extract`, `Resolve`, `Incremental`, `Cache`, `Sealed`), suite `LinkIndex.tests.axml`.

### Stage 3a — cross-vault references (landed 2026-10-25)
- Resolution, `Thorium.Editor.CustomControls.VaultBrowserControl`:
  - New private `Redirect(ref string? home, ref string name)`: an `@Alias/rest` target moves home into the vault the alias names (home becomes `<vault>\_`) and strips the alias; false when the alias is unlisted or its vault isn't on this machine. `FindSheet` and `FindNote` call it first.
  - New private `ReferencedVault(home, name, out rest)` -> `(bool aliased, string? id, string? vault)`.
  - New `public static bool VaultMissing(string? home, string file)`: true when the alias IS listed in the home vault's manifest but its vault isn't on this machine. An unlisted alias is not "missing", just unresolved.
- Errors:
  - `ArctisAurora.Core.UI.SheetBook`: new `public static Func<string?, string, bool>? vaultMissing`, host-set; wired in `Thorium.cs` to `VaultBrowserControl.VaultMissing`.
  - `ArctisAurora.Core.UI.SheetFormula`: new `public const string vault = "#VAULT!"`.
  - `SheetLinks.Missing(home, file)` (private): `#VAULT!` when `vaultMissing` says so, else `#LOCKED!` / `#REF!` as before. Formulas into a missing vault stay `#REF!` (`SheetBook.Resolve` returns null, unchanged path).
- Sidebar, `Thorium.Editor.VaultWindows`:
  - New `private const string referencedName = "Referenced"` (name of a referenced group's StackPanel) and `private static readonly Dictionary<RenderWindow, List<string>> referenced` (vaults each window shows by reference).
  - `IsOpen(vault)` also counts vaults a window shows by reference.
  - `AddBrowser(window, vault, string? via = null)` (was `(window, vault)`): header built by new private `Header(title, via)` (bold title + muted "via <name>" label).
  - New private `AddMissing(window, id, alias, via)`: header without a context menu, showing the alias, "not on this machine" and a "Locate..." button -> `FolderPicker` -> `Locate`; on refusal the label reads "that folder is a different vault".
  - New private `Sync(window)`: destroys the window's referenced groups and rebuilds them breadth-first from the roots' manifests (`VaultManifest.Depends` -> `KnownVaults.PathOf(id)`), transitively, skipping vaults already shown or listed as roots; each missing id is shown once; settles (`Settle`) vaults it stopped showing that no window shows. Called from `Wire`, `OpenHere` (new root) and `CloseVault`. New private `SyncAll()`.
  - `CloseWindow` also settles the window's referenced vaults.
  - New action `[A_XSDActionDependency("Vault.AddReference", "UI", ...)] public static void AddMenuReference()`: `FolderPicker` (starts in the vault's parent folder) -> `internal static bool AddReference(string vault, string picked)`. It refuses the vault itself; `Remember` + `Commit`; `VaultManifest.IdOf(picked)`; alias = folder name with `# [ ] ! / @ |` stripped ("Vault" if empty), suffixed " 2", " 3"... while taken; `AddDependency`; `SyncAll`.
  - New `internal static bool Locate(string id, string picked)`: accepts only a folder whose existing manifest carries that id; drops KnownVault entries with that id whose folder is gone; `Remember` + `Commit`; `SyncAll`.
- Data: `Vault.menu.axml` gets "Add referenced vault..." (`Vault.AddReference`) above "Close vault".
- Tests: new `Thorium.Tests.CrossVaultTests` (`CrossVault.Resolve`, `Formulas`, `Index`, `Sidebar`), suite `CrossVault.tests.axml`.

### Stage 3b — cross-vault rewrites and Paste link (landed 2026-10-25)
- `ArctisAurora.Core.UI.SheetBook`, new host callbacks (wired in `Thorium.cs`):
  - `Func<string?, string, string, string?>? vaultPart` (home, file part, target) -> the file part without its `@Alias/` when the reference written in home points into target's vault, else null.
  - `Func<string?, IEnumerable<string>>? referencingVaults` (home) -> homes inside open vaults whose manifests reference home's vault.
  - `Func<string?, string, string?>? filePrefix` (destination, source) -> `"@Alias/"` across vaults, null within one.
- `SheetBook`, new statics:
  - `internal static bool Points(string? home, string file, string target)`: `vaultPart` then `Names`; plain `Names` when no host set `vaultPart`, so other hosts are unchanged.
  - `public static IEnumerable<string> Linked(string? home, Func<string?, IEnumerable<string>>? list)`: home's vault's files from `list`, then each referencing vault's.
  - `public static string FilePart(string? destination, string source)`: `filePrefix` + `FileName(source)`.
- `SheetBook.PageRenamed`, `Shifted`, `Renamed`: the per-document rename / shift closures take the owner's path and match with `Points` instead of `Names`; on-disk sheets come from `Linked(home, vaultSheets)`, notes from `Linked(home, vaultNotes)` (`PageRenamed`, `Shifted`).
- `ArctisAurora.Core.UI.SheetLinks`: private `RewriteNotes(notes, Func<string, string, string?>)` (was `Func<string, string?>`) binds each note's path; the `Renamed` / `PageRenamed` / `Shifted` closures take `(note, reference)` and match with `SheetBook.Points`.
- `ArctisAurora.Core.UI.SheetEditorControl`: `PasteLink` writes the file part through `SheetBook.FilePart(path, sourcePath)`; `CopiedReference(string clipboardText)` -> `CopiedReference(string clipboardText, string? home)`, prefixing `filePrefix(home, source)`. Callers: `DocumentControl.PasteLink` passes `session?.path`; `FormulaPopup.PasteLink` passes `open.editor.path`.
- `Thorium.Editor.CustomControls.VaultBrowserControl`:
  - New `public static string? VaultPart(home, file, target)`.
  - New `public static IEnumerable<string> ReferencingVaults(string? home)`: known vaults other than home's that `VaultWindows.IsOpen` and whose manifest `Depends` on home's vault id.
  - New `public static string? FilePrefix(string? destination, string source)`: null when either file is outside every known vault or both are in one; otherwise the alias for the source's vault in the destination vault's manifest, adding it through `VaultWindows.AddReference` when missing.
  - New private `AliasOf(vault, id)`. `RenameNote` private -> internal; it passes `SheetBook.Linked(target, VaultSheets)` / `SheetBook.Linked(target, VaultNotes)`.
- Tests: `Thorium.Tests.CrossVaultTests` gains `CrossVault.Rename`, `CrossVault.PageAndShift`, `CrossVault.PasteLink`; suite `CrossVault.tests.axml` now 7 tests.

### Stage 4 — graph view and backlinks UI (landed 2026-10-26)
- `ArctisAurora.Core.UI.LinkIndex`: new host-set statics `Func<string, LinkIndex?>? indexOf` (file path -> index of its open vault) and `Action<string>? openFile`; new `public IEnumerable<string> Files()` (the files the index knows: notes and sheets); field `vault` changed private -> `public readonly string vault`.
- New `ArctisAurora.Core.UI.LinksPanelControl` (StackPanelControl):
  - `Show(string? path)` refreshes the file's own index plus the index of each vault from `SheetBook.referencingVaults(path)`, then rebuilds two lists: "Backlinks (n)" (incoming from all those indexes) and "Outgoing (n)".
  - Each row is a `TabToolsControl` text button (file base name) plus a muted kind caption (embed / math / formula / property / link); a click calls `LinkIndex.openFile`.
  - Unresolved targets and `LinkKind.Link` edges use MutedInk (H3 "desaturated").
  - New `public static string NameOf(string path)` = file name minus its `FileTypes` suffix.
- `ArctisAurora.Core.UI.InspectorControl`: new third child after docs / sheets, `Header("Links")` + `LinksPanelControl links`; fields `linksFor`, `linksDirty`; new private `ReflectLinks(IFileEditor?)` called from `OnTick` with `RibbonControl.Target(page) as IFileEditor`; rebuilds when the target path changes or its `isDirty` goes true -> false (a save).
- `ArctisAurora.Core.Filing.FileKind`: new `Graph`; `FileTypes.filetypes.axml` gains `<FileType Suffix=".graph.axml" Kind="Graph" Description="Aurora graph"/>`.
- New `ArctisAurora.Core.UI.GraphDocument`: `extension = ".graph.axml"`, `name`, `showOrphans`, `showUnresolved`, `pins` (Dictionary: vault-relative path or "?target" -> Vector2), `unsaved`, `IsGraph`, `Blank`, `Load` (`EncryptedFile.LoadXml`), `Save` (`VaultFiles.SaveXml`). Format `<Graph Version="1" Name Orphans Unresolved><Pin Path X Y/></Graph>`.
- New `ArctisAurora.Core.UI.GraphLayout` (pure data):
  - Ctor `(int count, IReadOnlyList<(int a,int b)> edges)` with deterministic phyllotaxis start positions; `position`, `pinned`, `Pin(node, at)`, `Unpin`, `Reheat()`, `Step()`, `settled`.
  - Barnes-Hut repulsion over a pooled quadtree array (theta 0.9, max depth 24), springs along edges, pull to the origin.
  - d3-style cooling: alpha decays 0.0228 per step, settled below 0.001 (about 300 steps), `Reheat` to 0.3.
  - Preallocated arrays; `Step` allocates nothing after the first.
- New `ArctisAurora.Core.UI.GraphEditorControl` (ContainerControl, `IFileEditor`):
  - `LoadPath`; `Rebuild()` refreshes the index and builds nodes from `Files()`; sealed files get a MutedInk lock tint; resolved targets outside the vault become Boundary nodes (MutedInk); unresolved targets become Ghost nodes (Line role) keyed "?target"; edges are deduped, strong = kind != Link; orphans dropped when `showOrphans` is off; pins applied.
  - `layout`, `nodeCount`, `KeyOf(int)`, `Screen(int)`, `NodeAt(point)`; `tools` = a `TabToolsControl` with "Orphans" and "Unresolved" toggles.
  - Drawing: nodes are round pooled `PanelControl`s (Doc / Sheet / Planner role, size grows with degree); edges are hairline `PanelControl`s rotated with `Control.rotation`; labels only at zoom >= 0.8 or for the hovered node and its neighbours.
  - Ticks (`SetTicking`) only while the layout isn't settled or a node is dragged.
  - Pointer: scroll zooms about the pointer (0.1-4); dragging the background pans; dragging a node pins it and marks the file unsaved; a left release on a non-ghost node calls `LinkIndex.openFile`; hover dims non-neighbours to 0.2.
  - Refreshes on load and when its tab becomes active (`TabViewControl.activeChanged`). Pan / zoom are not saved; `ViewState` returns an empty SessionTab.
- `Thorium.Editor.CustomControls.VaultBrowserControl`: new `internal static LinkIndex? IndexOf(string path)` (`KnownVaultOf` + `!VaultWindows.Closed(path)` -> `LinkIndex.For(vault, VaultManifest.IdOf(vault))`); `Open(string)` private -> internal; new `BuildGraphTab`; new actions `Graphs.New` (`NewGraph`) and `Graphs.NewHere` (`NewGraphHere`) over private `NewGraph(folder)` / `CreateGraph(folder, name)`; `IsAuroraXml` includes `FileKind.Graph`; `VaultNotes` skips graph files; `Extension` knows `.graph.axml`.
- `Thorium.Editor.VaultWindows.Settle` calls `LinkIndex.Close(vault)`. `Thorium/Thorium.cs` wires `LinkIndex.indexOf = VaultBrowserControl.IndexOf` and `LinkIndex.openFile = VaultBrowserControl.Open`.
- Menus: `File.menu.axml` New > "Graph" (`Graphs.New`); `VaultFolder.menu.axml` New > "Graph" (`Graphs.NewHere`).
- Tests: `Thorium.Tests.LinkGraphTests` (`LinkGraph.Backlinks`, `LinkGraph.Layout`, `LinkGraph.View`), suite `LinkGraph.tests.axml`. Goldens `Inspector.Docs.Panel.png` and `Inspector.Sheets.Panel.png` re-approved (only the new Links header differs).

## Why these choices

**Files stay the source of truth; the graph is a derived, rebuildable cache.**
Matches [[sheets]]: formula text is the only source of truth.

**One graph per vault; nodes are files.**
Sheet pages / cells and headings are anchors on an edge, not nodes. Cell-level edges stay in `SheetCalc` ([[sheets]] rejected 2.6M-edge range backlinks). Unresolved edges are kept and point at a ghost node, as Obsidian does.

**One resolver.**
The graph resolves through the same `FindSheet` / `FindNote` / `SheetBook.Resolve(home, ...)` code the editor uses. A separate resolver would disagree with formulas (e.g. two sheets with the same base name).

**The cache lives outside the vault:** `%LOCALAPPDATA%\Arktis\Thorium\Links\<id>.links.axml`, keyed per file by size + mtime.
Rejected: inside `.aurora/` - it would be committed and conflict on every merge, and any clone can rebuild it.

**Vault identity is a GUID in a committed manifest, mapped to a folder per machine (KnownVaults).**
Rejected: absolute paths (break on other machines); relative `../Other/` paths (only work with an identical clone layout, fail silently); git submodules as the link model (compatible - Locate can point inside one - but a VCS choice, not the model). Same shape as a package manifest + local resolution.

**A dependency is declared by the referencing vault:** `<Depends Id Alias>` in its manifest; links name the alias, so link text stays readable.

**F1 (user): a rename in vault B while referencing vault A is closed or absent leaves A's link unresolved, offering relink.**
Rejected: a committed rename-redirect list in B.

**F2 (user): cross-vault formulas are fine** - a formula loads the sheet it reads anyway.

**F3 (user): a referenced vault is browsed as if it were a local vault**, visible in the left vault browser.

**F4 (user): a referenced vault's files are all listed (for a server vault, all pulled), but only the needed ones are loaded**; otherwise only their names show in the browser.
Cycles A<->B are harmless for links; the graph view of A shows B's files as boundary nodes. A formula cycle across vaults is an ordinary `#CYCLE!` if the recalc graph spans both vaults - stage 3a settled it: `SheetBook` holds one global document set and one `SheetCalc`, so the recalc graph spans vaults and a cross-vault cycle is `#CYCLE!` with no change (test-verified).

**F5 (user): links follow the latest version** of a referenced vault; pinning to a revision is not planned.

**G1 (user): referenced vaults are their own sidebar group, tagged "via A"**; nothing is added if the window already lists that vault.
Rejected: nested inside A's group (shows a vault twice when also opened directly).

**G2 (user): a rename in B rewrites references in A when A is open locally**, like an in-vault rename; F1 covers A closed or absent. Built in stage 3b; "open" is `VaultWindows.IsOpen` (a window lists it, or shows it as referenced), and a referencing vault that isn't open is left alone (test-verified).

**G3 (user): a locked vault's link index stays in memory only** and is rebuilt on unlock.
Rejected for now: encrypting it under the master key. A plaintext index would leak which files link to which.

**G4 (user): `LinkIndex` lives in the Engine (`ArctisAurora.Core.UI`, beside `SheetLinks`); `VaultManifest` in Thorium beside `KnownVaults`.**

**G5 (user): cross-vault syntax is an `@Alias/` prefix on the file part:** `[[@Docs/Note]]`, `![[@Docs/Budget.sheet.axml#Data!A1]]`, `\sheet{@Docs/...}`, formula `[@Docs/Budget]Data!A1`.
A file name containing `#`, `]]` or a page name with `!` still breaks references.

**A locked vault reuses its `Vault.lock.axml` Id when the manifest is first created.**
A vault locked after it has a manifest still gets a fresh lock Id from `VaultLock.Create`: two ids, and the lock's is only the Windows Hello credential name.

**An unreadable manifest is never rewritten** (never destroy user data): `IdOf` returns "" and logs.

**Vault scans skip dot-folders** (user, 2026-10-25).
`VaultNotes` was already reading `.aurora/Vault.lock.axml` in locked vaults as a note; with every vault now carrying `.aurora/Vault.manifest.axml` it always would. Rejected: fixing only the failing test, which would leave sheet-link rename rewrites opening manifest and lock files as notes.

**H1 (user): one resolver for `[[note]]`.**
The static `FindNote(home, name)` searches the files of the note's own vault, without the workspace filter; the index and `OpenLink` both use it, so "Open" on a property now searches the note's vault (it used to search the selected vault and could miss files the workspace filter hid). Rejected: keeping two resolvers that disagree whenever the filter hides a file or the selected vault isn't the note's.

**H2 (user): the cache is under `%LOCALAPPDATA%\Arktis\Thorium\Links`, set by the host (`LinkIndex.cacheFolder`).**
Rejected: an engine-level `%LOCALAPPDATA%\Arktis\Links` beside `Paths.JOURNAL`. This corrects nothing - it matches the original design.

**H3 (user): `[[note]]` written in a note's body is indexed (kind `Link`) even though Thorium can't follow it yet.**
Stage 4 shows such links desaturated, as Obsidian does.

**No save hooks or file watcher.**
`Refresh` checks size + last-write ticks itself, which also catches edits outside Thorium and `git pull`. Rejected: hooking `VaultFiles` writes - many save paths, and external edits would still be missed.

**Resolved paths are recomputed on every `Refresh`, not cached.**
Any added or renamed file can change what another file's link resolves to.

**`reads` is public read-only and `Close(vault)` is public (user).**
The tests live in Thorium (they need Thorium's resolvers) and the Engine has no `InternalsVisibleTo`; `Close` also serves stage 3 (closing a vault drops its index). Rejected: `InternalsVisibleTo("Thorium")`, which exposes every Engine internal to Thorium.

**Renames keep their whole-vault scan** (deliberately unchanged).

**Two resolvers cover every reference kind (stage 3a).**
Formulas (`SheetCalc.PageNamed` -> `SheetBook.Resolve` -> `findSheet`), note embeds and `\sheet{}` math, `OpenLink` and `LinkIndex` all resolve through `FindSheet` / `FindNote`, so teaching those two `@Alias/` made every kind cross-vault. No parser change: `SheetFormula`'s `[...]` file part already takes `@` and `/`. Formulas Thorium writes quote such a name (`='[@Docs/Budget]Data'!A1`) because `Prefix`'s `Plain` doesn't allow `@`; typed unquoted also parses. Left as is.

**In 3a `SheetBook.Names` didn't match `@X/` references, so a rename in B left A's `@X/` references alone (no wrong rewrite).**
Stage 3b rewrites them deliberately (G2) through `SheetBook.Points`.

**Vault-aware matching replaces name-only matching for rewrites (3b).**
A reference names the changed file only when, resolved from the file that holds it, it points into the target's vault (its own vault, or the one its `@Alias/` names) and the name matches. It is a host callback (`vaultPart`) because vault membership and manifests are Thorium's.

**K2 (user): the existing bug fix rides with 3b.**
Before, `SheetBook.Renamed` / `PageRenamed` / `Shifted` rewrote every loaded sheet from every open vault by name alone, so renaming B's `Budget` also rewrote a loaded A sheet's own `[Budget]`. The vault-aware check fixes it (test-verified: A's own `[Budget]` is left alone). Rejected: splitting the fix into its own change first.

**K1 (user): Paste link from a vault the target vault doesn't reference adds the reference automatically.**
Folder-name alias, same as "Add referenced vault"; it writes the target vault's manifest and syncs the sidebar, then writes `@Alias/`. Rejected: pasting plain values (as into a CSV).

**Paste link from or into a file outside every known vault writes no prefix,** and never adds a reference to the default vault.

**Renaming keeps the alias:** `SheetBook.Renamed(file, ...)` already keeps any leading folder part, so `@X/Budget` -> `@X/Money` needed no extra code.

**J1 (user): stage 3 is split.**
3a is cross-vault resolution and the sidebar (landed). 3b is G2 cross-vault rewrites on file rename, page rename and row/column insert plus Paste link writing `@Alias/` (landed 2026-10-25).

**J2 (user): a note embed or math into a missing vault shows `#VAULT!`,** matching `#LOCKED!` / `#REF!`; the sidebar header carries the sentence "not on this machine".
Rejected: the long "vault not on this machine" inline in notes. Formulas stay `#REF!` as designed.

**J3 (user): Add referenced vault writes the folder name as the alias,** stripped of `# [ ] ! / @ |`, suffixed when taken, no prompt.
Rejected: a name prompt. Renaming an alias later would mean rewriting every link that uses it - out of scope.

**J4 (user): vaults referenced by a referenced vault also show,** transitively, breadth-first, each once, tagged "via" the vault they came through.
Overrides the recommendation of direct-only.

**Referenced vaults count as open (`IsOpen`)** so their files open live, not as closed placeholders; a vault that stops being shown and is listed nowhere is settled like a closed vault. Referenced groups are not recorded in the session; `Wire` rebuilds them on restore.

**Locate never writes into the picked folder.**
A folder with no manifest, or another vault's, is refused (the header says "that folder is a different vault"); another vault's id is never rewritten.

**`AddReference` writes the picked vault's manifest when it has none** - that assigns the chosen vault its identity, intended.

**Supersedes** the "link cache file in the vault" deferral in [[sheets]]: the cache is outside the vault, keyed by vault id.
It also answers [[encryption]]'s "every encrypted sheet prompts on each rename because nothing says whether it holds a reference" - the stage-2 index records that.

**D1 (user): stage 4 is built as one stage.**
Rejected: the recommended split into 4a backlinks and 4b graph.

**D2 (user): backlinks live in an inspector "Links" section, in both the docs and sheets halves.**
A sheet's incoming formulas are backlinks. Rejected: a footer under the note body (notes only); a left-sidebar panel.

**D3 (user): the index refreshes on the main thread — the inspector on a target change and after a save; the index is created on first ask and `Close`d on settle.**
Rejected: a periodic timer (cost unmeasured); a background thread (conflicts with the standing rule that a slow system gets fixed, not spread across cores).

**D4 (user): the graph view is an ordinary file, `*.graph.axml`, following the planner pattern.**
Tabs, session restore, rename and save then need no new code path; the file holds the filters and the pins and is committed in the vault (like Obsidian's graph.json). Rejected: a virtual tab with `path = null` (needs a non-path session scheme and would not be restored); a separate RenderWindow.

**D5 (user): force-directed Barnes-Hut layout, stepped per frame while cooling, zero cost at rest; dragging a node pins it.**
Rejected: a static layout computed once.

**D6 (user): drawn with pooled round panels and rotated hairline panels, no new shader; the Vulkan pipeline is untouched.**
Rejected: a Vulkan line module (pipeline work CLAUDE.md rules out). The risk was control count (about one per node plus one per edge); measured below.

**D7 (user): every indexed file is a node; ghosts for unresolved targets; a lock tint for sealed files; referenced-vault files only where linked, as boundary nodes (F4).**
Rejected: showing linked files only.

**D8 (user): click opens, hover highlights neighbours, drag pins, scroll zooms, drag pans; whole vault only, a local graph later.**

**P1 (user): the graph refreshes on load and when its tab becomes active.**
Rejected: also on a timer.

**P2 (user): graph files show where `Shows` already puts them (Docs and General workspaces).**
Rejected: a new `WorkspaceKind`.

**Departures approved 2026-10-26.**
- `LinkIndex.vault` made public so pins are vault-relative.
- `nodeCount` / `KeyOf` exposed for the View test.
- `Extension` learns `.graph.axml` (otherwise a rename yields "x.graph").
- A node opens on left-button Release at any tap count, because the engine dispatches the Tap phase only for double-taps (`UIEngine.SolveRelease`) — a click soon after a drag arrives as tapCount 2.

**Pins are keyed vault-relative ("y.md"); ghost pins by "?target"; boundary pins become "..\Other\x" paths (machine-layout dependent).**

**Measurements** (optimized build: configuration `DebugPerf`, Optimize=true, TRACE;PROFILE, 3 runs, throwaway in-engine tests, 1,000 generated .md notes with 3 links each + 1 unresolved per 20 -> 1,050 nodes, about 3,000 edges):
- `LinkIndex.Refresh`: first build 552-563 ms; unchanged 418-435 ms; after one edit 349-381 ms; fresh index from cache 321-394 ms; `LinksPanelControl.Show` 309-313 ms. Cause: `Resolve` re-resolves every edge on each Refresh and the static `FindNote` enumerates the whole vault per distinct target (O(targets x files)).
- Graph settling (Main, 120 frames): frame p50 1.68-1.87 ms, p95 4.57-4.64, max 7.99-8.62, 14.2 KB/frame; `Step.Main.Logic` (the layout Step) p50 about 0.92 ms, p95 2.2-2.6; `Step.Main.Layout` p50 0.42-0.54, p95 1.85-2.13; DrawLists p50 0.26-0.42.
- Graph settled (301 frames to settle): frame p50 0.41-0.57 ms, p95 0.81-0.96, 0 KB/frame; Logic and Layout about 0.001-0.002 ms; DrawLists p50 0.38-0.54 ms (emitting the quads).
- **User decision (2026-10-26): leave the Refresh cost as a recorded known gap.** Rejected for now: skipping `Resolve` when nothing was read or dropped; a batch resolver (one vault listing per Refresh, same matching rule as `FindNote`).

**Release builds currently fail, not caused by this change.**
SixLabors.ImageSharp 4.1.3's MSBuild licence check (`ValidateLicenseTask`) passes only configurations whose name starts with "Debug", and the repo has no sixlabors.lic / SixLaborsLicenseKey. It blocks a Release+PROFILE build and Release shipping. Measuring used a custom `DebugPerf` configuration with Optimize=true and DefineConstants TRACE;PROFILE — not DEBUG, because `Engine.isDebug` follows DEBUG and measured tests skip under it. Recorded in [[engine-profiling]].

## Known gaps
- Stage 5 not built (see [[link-graph-plan]]).
- Stage 4 verification: test-verified — `LinkGraph` suite 3/3. Backlinks (vault A open in a window: y lists x as a backlink; A's Budget lists Calc's formula; B's Budget lists A's cross.md embed via the referencing vault; x lists its outgoing y; after x drops the link, y shows Backlinks (0); after the window closes `indexOf` returns null). Layout (a 6-node graph settles in <1000 steps; linked pairs end closer than unlinked; a pinned node stays put; 0 bytes allocated over 100 Steps after the first). View (a graph file opens in `GraphEditorControl`; 7 nodes = 5 files + ghost + boundary, the graph file is not indexed; hiding orphans gives 5; the shown graph settles; dragging a node pins it, marks unsaved, and it stays where dropped; the pin survives save + reload; a click on the node calls `openFile` with its path).
- Stage 4 shot-verified on a throwaway golden (deleted): the settled View fixture draws diagonal rotated edges cleanly, Link edges fainter than embed edges, ghost faint, boundary grey, sheet green, notes blue, labels under nodes. Inspector.Docs / Inspector.Sheets goldens re-approved after a diff showing only the Links header row.
- Stage 4 full Thorium suite: midpoint 399 passed / 6 failed / 1 skipped; end 401 passed / 6 failed / 1 skipped — the 4 known (Boot baseline error, `Sheet.PasteLinks`, `Sheet.NotePasteLinks`, `Sheet.MathLinks`) plus the two inspector goldens, which passed after re-approval. No `[Vulkan]` lines in any run.
- Stage 4 NOT GUI-verified: nothing clicked in a normally launched app — the File / folder "Graph" menu entries, the toggles, inspector Links rows opening files, Thorium's real `Open` from a graph node (the test replaced `openFile` with a recorder), hover dimming and scroll zoom are correct by inspection only.
- `LinkIndex.Refresh` costs about 0.3-0.56 s per inspector target change, save or graph activation on a 1,000-note vault (user chose to leave it; see Measurements).
- No way to unpin a node; pan / zoom not saved; no local graph, name search / filter, colour groups or label collision avoidance; graph rows use the generic "file" icon.
- "Graph" is only in File > New and the folder context menu, not in Vault.menu's New submenu or the row context menus where Planner also appears.
- Labels overlap on dense graphs; hit-testing nodes is O(N) per pointer move.
- The inspector's Links section shows only in the Docs / Sheets workspaces with the inspector open.
- Boundary-node pins are stored as machine-layout dependent relative paths.
- Graph files can't be encrypted (saved without a key).
- Note renames don't rewrite `[[note]]` links (true inside one vault too).
- Vaults that aren't open aren't rewritten (by design, F1): their `@X/` references go unresolved.
- The note editor's Paste link across vaults is not driven by a test, only its `filePrefix` callback.
- Stage 3b verification: test-verified - `CrossVault` 7/7, new ones: Rename (A open in a window, referencing B; renaming B's Budget -> Money through `RenameNote` rewrites A's on-disk sheet, a loaded sheet (which still reads 10), a markdown embed and its `\sheet{}` math, and an `.axml` note link; A's own `[Budget]` and own-vault embed stay; with A's window closed a second rename leaves A alone), PageAndShift (renaming B's page Data -> Figures and inserting a row at the top rewrite A's on-disk `[@B/Budget]Data!A2` -> `Figures...A3` and a markdown embed the same), PasteLink (Paste link in an A sheet from a B sheet writes `[@B/Budget]Data` and reads 10; `filePrefix` gives an A note `@B/`; pasting from an unreferenced vault C adds C to A's manifest under its folder name and writes `@<C>/`). Full Thorium suite at the midpoint: 400 passed, 4 failed, 1 skipped - the same four as before (Boot baseline, no new error; `Sheet.PasteLinks`, `Sheet.NotePasteLinks`, `Sheet.MathLinks`); no `[Vulkan]` lines. End suite: the same 400 passed, 4 failed, no `[Vulkan]` lines.
- Stage 3b NOT GUI-verified.
- No way to remove or rename a vault reference.
- "Close vault" on a referenced group's header does nothing (it closes only a window's own vaults); the vault menu's other entries act on the referenced vault.
- Thorium-written formulas quote `@` file parts.
- Stage 3a verification: test-verified - `CrossVault` 4/4. Resolve: an `@B/` sheet and note resolve in B; an unlisted alias resolves to nothing and is not "missing"; a listed vault this machine doesn't know is missing. Formulas: an A formula reads B's cell; a cycle A->B->A is `#CYCLE!`; a note embed reads B's sheet; with B unknown a note embed shows `#VAULT!` and a newly loaded formula `#REF!`. Index: A's `LinkIndex` resolves `@B/` embed and `[[@B/note]]` edges into B. Sidebar: a window listing A shows B and B's reference C as groups, both `IsOpen`, with "via" labels; a missing vault shows by alias with "not on this machine"; C's file opens live; Locate refuses a folder with no manifest without creating one, refuses another vault, accepts the right one and its group replaces the missing one; `AddReference` lists a vault under its folder name and shows it; closing A removes the referenced groups and B, C stop being open. Full Thorium suite at the midpoint: 397 passed, 4 failed, 1 skipped - the same four as before (Boot baseline, no new error; the three unexplained Sheet Ctrl+Shift+V tests); no `[Vulkan]` lines. End suite: the same 397 passed, 4 failed, no `[Vulkan]` lines.
- Stage 3a NOT GUI-verified: nothing clicked - the menu entry, the folder picker, the Locate button and the look of the "via" headers are correct by inspection only.
- The first build's cost on a large vault is measured under stage 4 (552-563 ms for 1,000 notes).
- A `[[link]]` split across differently formatted runs in an `.axml` note is not found; links inside a sealed block are not indexed.
- Nested known vaults: one lookup per distinct target name per refresh, so a file in a vault nested inside another resolves as if in the outer one.
- With two files matching a `[[name]]`, the static `FindNote` takes the first in file-system enumeration order, which may differ from the old tree walk.
- Stage 2 verification: test-verified - `LinkIndex` suite 5/5 (Extract: every link kind from .md, .axml and a sheet, same-file formulas add no edge, the `.aurora` manifest is not read; Resolve: Incoming to a sheet from embeds / math / formulas, a property, aliased link and heading link resolve to a note, Unresolved, a file created later resolves its link and is the only file read; Incremental: an edit re-reads one file, a deleted file's edges drop and links to it go unresolved; Cache: written under the vault id, a fresh index after `Close` re-reads nothing and finds the same links, a vault with a lock file is indexed but writes no cache; Sealed: a password-encrypted note is recorded sealed with no edges). Full Thorium suite at the midpoint: 393 passed, 4 failed, 1 skipped - the same four as before the change (Boot baseline error, no new error; `Sheet.PasteLinks`, `Sheet.NotePasteLinks`, `Sheet.MathLinks`, the unexplained Ctrl+Shift+V failures). No `[Vulkan]` lines. End suite: the same 393 passed, 4 failed, no `[Vulkan]` lines.
- NOT GUI-verified: the properties panel's Open button after the `OpenLink` change was not clicked.
- `VaultLock.Create` makes its own Id even when a manifest exists (two ids per vault once locked later).
- Existing KnownVault entries get their id only when the vault is next opened.
- Verification: `VaultManifest` suite 5/5 at the midpoint; full suite then 387 passed, 5 failed - Boot (baseline error, no new error), `NoteModel.SegmentMap` (caused by the manifest, fixed by the dot-folder skip), and `Sheet.PasteLinks`, `Sheet.NotePasteLinks`, `Sheet.MathLinks`, which fail at their Ctrl+Shift+V step, also when the Sheet suite runs alone. None reaches `KnownVaults` or `VaultManifest` (by inspection, no baseline run); the tree carries someone else's uncommitted `TextInputActions.cs` changes. The three Sheet paste-link failures are not explained.
- End-of-change full suite (after the dot-folder skip): 388 passed, 4 failed, 1 skipped - `NoteModel.SegmentMap` passes; Boot and the three Sheet paste-link tests as above.
- Not GUI-verified: nothing user-visible changed.

Related: [[link-graph-plan]], [[multi-vault-workspace]], [[sheets]], [[encryption]], [[engine-profiling]]
