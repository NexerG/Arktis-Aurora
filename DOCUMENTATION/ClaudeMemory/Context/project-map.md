# Project map — abstract names → real folders

`CLAUDE.md`'s *Solution Structure* table uses abstract names. They map to actual top-level
folders/projects as follows (source of truth for namespaces: `NAMESPACES.md` at repo root):

| CLAUDE.md name | Real folder         | Root namespace(s)                                                  | Notes                                                                                                                        |
| -------------- | ------------------- | ------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------- |
| `Engine`       | `AuroraEngine`      | `ArctisAurora`, `ArctisAurora.Core.*`, `ArctisAurora.EngineWork.*` | The engine lives in `AuroraEngine/` (folder renamed from `ParticleSimulator` 2026-07). Project file `AuroraEngine.csproj`; assembly + root namespace pinned to `ArctisAurora`. Engine core under `AuroraEngine/Core`. |
| `Editor`       | `AuroraEditor`      | `AuroraEditor.*`                                                   | Visual editor; consumer of the Engine.                                                                                       |
| `TextEditor`   | `Thorium`           | `Thorium`, `Thorium.Editor.*`                                      | A host app whose `Main` boots `Engine` and loads UI from XML. Folder, project and namespaces renamed from `Periodic`/`AuroraPeriodic` (2026-08-31). |
| `Hackathon`    | —                   | —                                                                  | No such folder exists in the repo.                                                                                           |

Additional top-level items not in the table:

- `AuroraTesting` — test project.
- `_Build` — tooling. `_Build/GenerateNamespaces.cmd` regenerates `NAMESPACES.md`.
- `DOCUMENTATION` — Obsidian vault (human docs) + this `ClaudeMemory` folder.

## Engine internal layout (under `AuroraEngine/Core`)

- `Core/` — `Engine.cs`, `Bootstrapper.cs`, `InputHandler.cs`, `AuroraScene.cs`.
- `Core/ECS/` — entities + components (`EngineEntity/`, `RenderingComponents/`).
- `Core/Registry/` — `AssetRegistries.cs`, `EntityRegistry.cs`, `XSDGenerator.cs`, `Assets/`.
- `Core/Rendering/` — Vulkan renderer, modules, pipelines, mesh subcomponents.
- `Core/UI/` — the control tree, layout, input dispatch, actions, gradients. Namespace follows folder
  (`ArctisAurora.Core.UI.<Folder>`): `Containers/` (+ `Tabs/`, `Workspace/`), `Controls/` (small
  controls), `Menus/`, `Windows/` (dialogs), `Text/`, `Document/`, `Tex/`, `Sheets/`, `Planner/`,
  `Graph/`, `Notes/`, `Files/`.
- `Core/Filing/` — `AuroraFont`, `Glyph`, `Bezier`, `IconSet`.
- `Core/Filing/Serialization/` — `Serializer`, `AssetImporter`, `Paths`, `VirtualFileSystem`.

> Detailed per-system facts live in `CLAUDE.md`. This file only fixes the name↔folder mapping
> that is *not* derivable from `CLAUDE.md` alone.
