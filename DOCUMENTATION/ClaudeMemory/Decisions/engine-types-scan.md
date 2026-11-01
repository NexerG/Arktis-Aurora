# Decision — one shared lazy index for engine attribute reflection scans

**Date:** 2026-11-01
**Scope:** `ArctisAurora.Core.Registry` — `EngineTypes`, `Context`, `AssetRegistries`, `SettingsRegistry`, `XSDGenerator`, `AnyXMLType`; `Bootstrapper`, `Shutdown`, `CommandConsole`, `TestRunner`, `InputHandler`, `Control` (ControlXml), `DataManager`

## What changed
- New static class `EngineTypes`, built lazily on first use:
  - `Type[] All` — every type in the engine assembly and in each loaded assembly that references the engine (by assembly name), in load order.
  - `(MethodInfo method, A_XSDActionDependencyAttribute attr)[] Actions` — every static `[A_XSDActionDependency]` method (Public|NonPublic|Static|DeclaredOnly) across `All`.
  - private `CollectTypes`, `CollectActions`, `DoesReference(Assembly, string)`.
- About 15 separate whole-AppDomain scans collapsed to one type list plus one action list.
- Read `Actions` (each keeps its own category filter): `Bootstrapper.Load`, `Shutdown.Load`, `CommandConsole.Build`, `TestRunner.Discover`, `InputHandler.ParseXML`, `Control.TaggedActions` (private `_taggedActions` cache removed), `SettingsRegistry.Actions`, `DataManager.ResolveSortProvider` (previously rescanned every assembly on every call).
- Read `All`: `Context.LoadContexts`, `AssetRegistries.RegisterSerializableTypes`, `SettingsRegistry.LoadAll`, `AnyXMLType.FindType`, `XSDGenerator.GenerateXSD`, `XSDGenerator.BuildTypeMap`, `XSDGenerator.BuildCategoryMap`.
- `XSDGenerator` keeps its own method scan over `All`, because its action XSD includes instance methods.
- `Context.LoadContexts` walks `GetProperties` + `GetFields` (Static|Public|NonPublic|DeclaredOnly) instead of `GetMembers`; `A_ActiveContextAttribute` targets only properties and fields.
- No data XML changed.

## Why these choices

**Only the engine assembly and assemblies that reference it can carry engine attributes.**
A type cannot be tagged with `A_XSDType`, `A_XSDActionDependency`, `A_ActiveContext` or `Serializable`, or inherit one, without referencing ArctisAurora, so System.Private.CoreLib, Silk.NET, ImageSharp etc. are skipped.

**One lazy cache is shared by everyone, and the assembly set is frozen at first use.**
First use is `XSDGenerator.GenerateXSD` in the host's `Main`, when the host exe and the engine are both loaded. Nothing in the tree loads assemblies dynamically (no `Assembly.Load`, `LoadFrom`, `AssemblyLoadContext`). Consequence: a plugin assembly loaded after boot would be invisible to every scan; supporting one needs an explicit rescan.

**Rejected: keep per-site scans and only narrow the assembly filter at each site.**
It would keep ~8 duplicate method scans of the same types, and `ResolveSortProvider` would still rescan on every call.

**Rejected: a source generator / build-time attribute index.**
It adds tooling, and the runtime scan is now ~20 ms in total.

**Not thread-safe, deliberately.**
The lazy init has no lock, same as the old `_taggedActions ??=`. All first uses are on the main thread at boot. A race would only build the arrays twice, with equal contents.

**`CollectTypes` counts, then fills (aurora-code rule 2), calling `GetTypes` twice per engine-facing assembly.**
Holding the first call's arrays would need a temporary `Type[][]` (rule 8). The second call cost nothing measurable: main thread start → Bootstrap start stayed 125–135 ms over 3 Release+PROFILE runs. `CollectActions` keeps a `List` with capacity 1024, because its count isn't known without a second `GetMethods` pass over every type (rule 7).

**Regression context.**
The 2026-09-15 baseline had the Bootstrap phase at 709 ms plus ~245 ms of XSDGenerator. The whole-domain scans grew with assemblies and types; `Context.LoadContexts` alone had reached 2.4 s.

## Measurements
Release+PROFILE, 3 cold launches each side, `Thorium.exe --profile=30 --exec "Wait 2500; Quit"`, boot capture plus `[Bootstrap]` log lines, same machine. Before → after, ms:

| Span | Before | After |
|---|---|---|
| main thread start → Bootstrap phase start | 958/574/584 | 124/123/133 |
| Bootstrap phase | 4012/3610/3838 | 1131/1154/1125 |
| Bootstrap allocation | 570 MB | 50 MB |
| `Context.LoadContexts` | 2352/2311/2505 (281 MB) | 20/19/19 |
| `InputHandler.LoadInputs` | 175/153/138 (208 MB) | 4.7/4.1/4.6 (125 KB) |
| `Settings.LoadAll` | 64/41/56 | 20/20/22 |
| `DataManager.ParseXML` | 30/24/31 (29 MB) | 5.1/4.9/5.6 (1.3 MB) |
| `RegisterSerializableTypes` | 31/45/33 | 4.5/4.4/4.4 |
| `AssetRegistries.PreloadAssets` | 815/618/643 | 618/668/656 (unchanged, now the largest step) |
| `Renderer.Initialize` | ~240–310 | ~240–310 (Vulkan driver work) |

Cold Thorium startup, main thread start to end of Bootstrap: ~4.4–5.0 s → ~1.25 s.

## Verification
Test-verified, plus Release+PROFILE measured.
- `_Build/test.sh` (full suite, Debug): 508 passed, 8 failed, 2 skipped, 0 new error kinds against `_Build/test-baseline.txt`.
- The 8 failures (Boot: known default-sampler `LoadDefault` error; 7 LatinModern goldens, 28–61 px differences) reproduce identically on a clean `git archive HEAD` copy, so they predate this change.
- Regenerated XSD schemas in the Release bin are byte-identical to those before the change.
- No "action not found" warnings in the bootstrap log.

## Known gaps
- Not checked: Carbon and AuroraEditor startup were not launched; time before `Main` and up to the first presented frame was not measured.
- `AssetRegistries.PreloadAssets` (~620–670 ms) is now the dominant bootstrap step; it has no sub-zones and isn't understood.
- `Renderer.Initialize` (~250 ms) is Vulkan and driver work, not looked at.
- The 7 LatinModern golden failures predate this change and are not investigated.

Related: [[engine-profiling]], [[xsd-generator-cross-category]], [[declared-contexts]]
