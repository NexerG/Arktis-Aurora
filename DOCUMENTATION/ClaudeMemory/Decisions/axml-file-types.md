# Decision — every Aurora-owned XML file is `.axml`; `FileTypes` says what a suffix is

**Date:** 2026-10-18
**Scope:** `ArctisAurora.Core.Filing` — `FileTypes`, `FileType`, `FileKind`, `FileTypeMap`, `Arx.AppBuilder`; `ArctisAurora.Core.UI` — `RichTextDocument`, `DocumentEditorControl`, `NoteFileSync`, `SheetLinks`; `Thorium.Editor.CustomControls.VaultBrowserControl`; `SheetDocument`, `PlannerDocument`; data `FileTypes.filetypes.axml`, `Bootstrap.bootstrap.axml`

## What changed
- Every Aurora-owned XML file is `.axml`: engine/app data, font `.math.axml`/`.import.axml` stamps, `Thorium.build.axml`, `SchemaManifest.axml`, user settings `*.settings.axml`, `Vault.lock.axml`, `<App>.keys.axml`, test `results.axml`, `*.frames.axml` captures, `uitree*.axml`, notes `.axml`, sheets `.sheet.axml`, planners `.planner.axml`.
- Plain `.xml` is a separate, generic type: Thorium opens it in the XML source view, not as a rich note.
- New `FileTypes` (static): `all` (`List<FileType>`, longest suffix first), `Load()` tagged `[A_XSDActionDependency("FileTypes.Load", "Bootstrap")]`, `Of(string? path)` → `FileType?` (longest suffix, case-insensitive), `KindOf(string? path)` → `FileKind?`.
- New `FileType` record (`[A_XSDType("FileType","UI")]`): `suffix`, `kind`, `description`, `register`, `mime` (part B; `[A_XSDElementProperty("Mime", "UI", …)]`). New `FileKind` enum: Note, Markdown, PlainText, Tex, Xml, Sheet, Planner. New `FileTypeMap` (`[A_XSDType("FileTypes","UI", typeof(FileType))]`, root container).
- Data `FileTypes.filetypes.axml`: `.axml` Note (Register), `.sheet.axml` Sheet, `.planner.axml` Planner, `.xml` Xml (Register), `.md` Markdown (Register), `.txt` PlainText (Register), `.tex` Tex. UI schema category.
- `Bootstrap.bootstrap.axml`: `FileTypes.Load` is the first step, before `Settings.LoadAll`.
- `RichTextDocument.extensions` (string[]) removed; `RichTextDocument.Opens(string path)` replaces it (Note/Markdown/PlainText/Tex/Xml). `Load` maps `FileKind.Xml` → `LoadSource(path, "xml")`.
- `DocumentEditorControl`: private `Extension` (string) → private `Kind` (`FileKind?`); all format checks use it.
- `NoteFileSync.Segmented`: segmented writes for `FileKind.Note` (was `.xml`). `SheetLinks` link rewrite: Markdown check via `FileTypes.KindOf`.
- `VaultBrowserControl`: `IsXml` = Note/Sheet/Planner/Xml; new private `IsAuroraXml` = Note/Sheet/Planner, used by `Encrypt` and `WriteName`; `Accepts` = any `FileTypes` match or CSV; a new note in the Xml workspace is `.axml`.
- `SheetDocument.extension` = `.sheet.axml`, `PlannerDocument.extension` = `.planner.axml`.
- `AppBuilder` packs both `.axml` and `.xml` as `ArxKind.Xml`. `.gitattributes`: `*.axml linguist-language=XML`. `_Build/GenerateStressNotes.ps1` writes `.axml`.

## Why these choices

**`.axml` for everything Aurora writes, `.xml` left to generic XML.**
So the OS can associate Aurora's own documents separately from generic XML, Markdown and text (OS registration is part B, below). Rejected: (a) rename only user documents and keep engine data `.xml` — keeps Visual Studio XML/XSD tooling, but the user chose one suffix everywhere; (b) keep `.xml` for everything — no way to tell an Aurora note from someone else's XML file.

**No migration code and no legacy suffix support.**
The app has no users yet, so the few old-named files on the dev machine (3 `UserSettings.settings.xml`, 4 stress notes) were renamed once by hand. Rejected: move-on-first-access for settings, lock and keys, and recognising `.sheet.xml`/`.planner.xml`/`<Document>`-rooted `.xml` — code that only serves files nobody has.

**The registry is XML-declared, not a C# table.**
Data and config are XML in this repo, and the schema is generated. Matching is by longest suffix because `Path.GetExtension("x.sheet.axml")` is `.axml`, which would make every sheet a note.

**`Register` marks the single-extension types the OS will see.**
`.axml`, `.xml`, `.md`, `.txt`. `.sheet.axml` and `.planner.axml` are covered by `.axml` at the OS level. `.pdf` is deliberately absent: Thorium cannot display PDFs.

**Tradeoff accepted: Visual Studio's XML editor and XSD IntelliSense do not apply to `.axml`** until mapped per machine (Tools › Options › Text Editor › File Extension → `axml` = XML (Text) Editor).

## Known gaps
- Part B gaps are under "Part B" below. macOS deferred until a mac ship target exists.
- `.arx` pack unverified after the rename (needs build keys; packing would mint Thorium's permanent signing key).
- Old captures (`*.frames.xml`) and old `results.xml` under %APPDATA% no longer show in Carbon or test history.
- `bin/` output folders still hold stale `.xml` copies from earlier builds; nothing reads them.
- The ThoriumNotes vault (not on the dev machine) still has `.planner.xml` / `.xml` files; it needs the same one-off rename where it lives.
- Plain `.xml` files cannot be encrypted from the vault menu (guarded to Note/Sheet/Planner) — a choice made in implementation, not yet confirmed with the user.
- Verification: **test-verified** on Windows — all five projects build; full `_Build/test.sh` = 346 passed, 1 failed (Boot, baseline errors only, no new errors), 1 skipped; no `[Vulkan]` lines. Test `Notes.FileKinds` (Notes suite) checks suffix lookup (`.sheet.axml` vs `.axml`, case-insensitive, `.xml` = Xml, unknown = null) and that a `.xml` with a `<Document>` root loads as XML source. Not checked: GUI by hand; Linux; the `.arx` pack and the shipped exe's tests.

## Part B — OS registration and command-line open (2026-10-18)

**Scope:** `ArctisAurora.Core.Filing` — `FileAssociations`, `RegistryEntry`, `FileType.mime`; `ArctisAurora.EngineWork.Rendering.Background`; `ArctisAurora.Core.UI.Windows.SettingsWindow`; `Thorium.Thorium` (`Main`, `OpenStandalone`); `Thorium.Editor.CustomControls.VaultBrowserControl.BuildTab`; tests `FileAssociationTests` (suite `FileTypes.tests.axml`), `Workspace.StandaloneFile`

### What changed
- New `FileAssociations` (static): `enabled` (set by a host that offers its types in Settings), `Registered` (`FileTypes.all` where `register`), `Claim(FileType)`, `ClaimAll()`, `ProgId(app, type)` → `"<App>.<suffix without dot>"`, pure builders `WindowsEntries(app, exe, claimed)` → `List<RegistryEntry>`, `DesktopEntry(app, exe)` → string, `MimePackage()` → `XElement`; private `ApplyWindows`, `ApplyLinux`, `ChosenElsewhere`, `Run`.
- New `RegistryEntry` (readonly record struct): `key`, `name`, `value`; empty name = the key's default; keys are under HKEY_CURRENT_USER.
- `FileType.mime`; data `FileTypes.filetypes.axml`: `.axml` = `application/x-aurora+xml`, `.xml` = `application/xml`, `.md` = `text/markdown`, `.txt` = `text/plain`.
- `Background.standalone` (`public static bool`): the private `Tray()` returns false while it is set, so `canTray` is false, `Init` claims no single-instance mutex and makes no tray icon, and closing the primary window quits (`Close` → `Shutdown.Request`).
- `SettingsWindow`: new `fileTypesCategory = "File types"`, listed after Keybinds only when `FileAssociations.enabled`; `FillFileTypes()` — one row per registered type ("<description> (<suffix>)", button "Make default" → `Claim(type)`), then "All types" → "Make default for all" → `ClaimAll()`. Buttons act at once, not through Save/Apply.
- `Thorium.Main`: if `args[0]` is not a flag and names an existing file, sets `Background.standalone` before `Engine.Init` and, after init, calls new `internal static OpenStandalone(path)` instead of building `main` and restoring the session. Always sets `FileAssociations.enabled = true`. `OpenStandalone`: `SessionLayout.paused = true`, the primary window's document is `tab-window`, loads its workspace default, puts `VaultBrowserControl.BuildTab(path, false)` in the `Tabs` view.
- Tests: `FileTypes.ClaimOne`, `FileTypes.ClaimAll`, `FileTypes.LinuxFiles` (suite `FileTypes.tests.axml`); `Workspace.StandaloneFile`.

### Why these choices

**Windows: every press registers all types under HKCU (no admin); only the claimed type(s) get the default.**
Per registered type: a ProgID (`Software\Classes\Thorium.axml` etc.: description, `DefaultIcon` `"exe",0`, `shell\open\command` `"exe" "%1"`) and `Software\Classes\<ext>\OpenWithProgids\<ProgID>`; plus `Software\Thorium\Capabilities` (`ApplicationName`, `ApplicationDescription`, `FileAssociations\<ext>` = ProgID) and `Software\RegisteredApplications\Thorium`; then `SHChangeNotify(SHCNE_ASSOCCHANGED)`. Only the claimed type(s) get `Software\Classes\<ext>` default = ProgID. Registration for Open With is cheap and repeatable, so every press does all types; the default is claimed per type because the user asked for an independent choice per type plus one for all. Rejected: claim only types nothing owns (the user wanted per-type control); claim all always (overrides the machine's handler).

**A default the user picked themselves cannot be overwritten, so the user finishes the switch in Windows Settings.**
Windows protects `Explorer\FileExts\<ext>\UserChoice` / `UserChoiceLatest` (hash-protected, no app may write it). When a claimed type has one naming another ProgID, Thorium logs it and opens `ms-settings:defaultapps?registeredAppUser=Thorium`; the Capabilities/RegisteredApplications entries are what make Thorium appear on that page. Expect `.md`/`.txt` to land there usually; `.axml` switches directly.

**Linux: a `.desktop` file, a mime package, then the freedesktop tools.**
Writes `$XDG_DATA_HOME/applications/thorium.desktop` (`Exec="exe" %f`, `MimeType=` the four types) and `$XDG_DATA_HOME/mime/packages/thorium.xml` (freedesktop requires `.xml` there). The mime package declares only the unregistered `application/x-` types (`.axml`, sub-class-of `application/xml`) — the system already knows the IANA types. Then runs `update-mime-database`, `update-desktop-database`, and `xdg-mime default thorium.desktop <mime>` per claimed type; a missing tool is logged and skipped. On Linux a claim always takes effect.

**Builders are pure functions.**
Tests never touch the real registry or home folder; the apply methods are thin.

**A double-clicked file gets a new process with a bare `tab-window`, never forwarded to a running copy (user choice F4).**
The tray's single-instance mutex would make that second process show the first and exit, so a standalone process skips the tray entirely (user confirmed). The vault's session layout is paused so the standalone window is not recorded over it. This amends [[desktop-stickies]]: single instance is claimed unless the process is standalone.

**Placement is a Settings category behind `FileAssociations.enabled`.**
Thorium sets it, so Carbon and the Editor never offer to claim Aurora's types. Rejected: a footer button in Thorium's `Settings.ui.axml` (cannot be per type).

**Deferred: macOS** (no mac ship target; GLFW does not deliver Finder open-document events), and registering at install time.

### Known gaps
- Linux registration is built but never run.
- No on-screen confirmation after a Make default press; it only logs.
- No unregister. No install-time registration. macOS.
- Two Thorium processes share one settings file; whichever exits last writes it (already true with the tray off).
- Pressing the button in a Debug run registers the Debug exe's path.
- `Process.WaitForExit` on the Linux tools blocks the main thread for the press.
- Verification: **test-verified** on Windows — all four projects build; full `_Build/test.sh` = 349 passed, 1 failed (Boot, baseline errors only), 1 skipped, no `[Vulkan]` (midpoint, after `FileAssociations`); Workspace suite with `Workspace.StandaloneFile` 9 passed. Launched the Debug `Thorium.exe` with a temp `.axml` path: it booted through all 31 bootstrap steps into the frame graph and stayed alive, logging only the baseline sampler error. Not checked: what the standalone window looks like (not GUI-verified), pressing the Make default buttons (writes the user's registry — the user does that), double-clicking a file, Linux at all.

Related: [[note-file-formats]], [[xml-editor]], [[xml-type-suffix]], [[sheets]], [[arx-archives]], [[desktop-stickies]], [[tray-availability-gate]]
