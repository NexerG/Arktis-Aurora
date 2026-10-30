# Mistake — a TeX export from a scratch copy imported a font from %TEMP% for good

**What I did wrong (2026-10-29, L11a):** to measure the thesis export I copied the thesis folder into a
session scratchpad under `%TEMP%` and exported from there. The template's fontspec `\setsansfont[Path=fonts/Calibri/]`
made `TexLowering.Imported` import Calibri from that copy, and `FontLibrary.Register` recorded it in the user's
`UserFonts.imports.axml` with full `%TEMP%` paths. The import is permanent user data, so every later boot
re-baked Calibri from a folder that would be deleted with the session.

**Why it was wrong:** an import is not part of the run that triggers it. It outlives the session, and it points
at whatever path the document sat in at the time. A scratch copy is exactly the path that must never be
recorded.

**Fixed (2026-10-30):** fontspec names resolve to the installed family first and use `Path=` files only when the
font is not installed (`TexLowering.Imported`, see `Decisions/font-variants.md`); the user's Calibri entry was
removed and re-imported from `C:\WINDOWS\Fonts`.

## Rules

- Before exporting or compiling a `.tex` that names fonts, know where its fonts would be imported from. Never
  run such an export against a copy in a scratch or temp folder on a machine whose user data is real.
- After any run that may import a font, read `%LOCALAPPDATA%\Arktis\Data\XML\Imports\UserFonts.imports.axml`
  and check no `Source` points into `%TEMP%` or a scratchpad.
- A file the app records as user data (imports, assets, settings) is the user's from the moment it is
  written. Treat a path written into it as a promise that the path will stay.
