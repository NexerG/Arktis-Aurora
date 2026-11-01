---
date: 2026-05-30
tags:
  - d_System
cssclasses:
  - Aurora.css
Status: Current
Linker:
  - "[[Arctis Aurora]]"
System:
  - "[[Bootstrapper]]"
Dependencies:
  - "[[XML-XSD]]"
  - "[[Paths]]"
Implementors:
  - "[[Bootstrapper]]"
Namespace: ArctisAurora.EngineWork
SourceFiles: AuroraEngine/Core/Bootstrapper.cs
VerifiedAgainst: 2026-05-30
---
## Overview

The bootstrapper decides **what order the engine starts up in** â€” and nothing else. It does not contain startup logic itself; each step is an engine method living in its own system (registry, input, renderer, â€¦). The sequence is declared in `Bootstrap.bootstrap.axml`, so reordering or inserting a startup step is an XML edit, not a code change. This replaces the old hardcoded `A_BootstrapStage` enum approach.

## Architecture

```mermaid
graph TD
  XML[Bootstrap.bootstrap.axml] -->|Phase / Step| Steps[ordered step names]
  Reflect[scan assemblies for A_XSDActionDependency category=Bootstrap] --> Map[name â†’ MethodInfo]
  Steps --> Run[RunPhase: invoke each step in XML order]
  Map --> Run
```

### Discovery

Step actions are found through `EngineTypes.Actions`, a lazily built index shared by every attribute scan in the engine, so `Bootstrapper.Load` does not walk every type in every loaded assembly. The index covers the engine assembly and assemblies that reference it, and is frozen at first use. See `ClaudeMemory/Decisions/engine-types-scan.md`.

### Step resolution
`Bootstrapper.Load(xmlPath)` does two things:
1. Reads `EngineTypes.Actions` and collects each method tagged `[A_XSDActionDependency(name, "Bootstrap")]` into a `name â†’ MethodInfo` map.
2. Parses `Bootstrap.bootstrap.axml` into `phase name â†’ ordered list of step (action) names`.

`RunPhase("Bootstrap")` then walks the step list in declared order, looks each name up in the map, and invokes it. An unknown step name is logged and skipped (non-fatal).

## Lifecycle / Flow
1. `Engine.Init()` calls `Bootstrapper.Load(Paths.BOOTSTRAP)` â€” `Paths.BOOTSTRAP` resolves `Bootstrap.bootstrap.axml` through the [[XML-XSD]] / VFS layer (engine default unless an app overrides it).
2. `Bootstrapper.RunPhase("Bootstrap")` invokes the steps.

The default step order is listed in [[Attributes & Conventions#Bootstrap step names]].

## Data / XML formats

```xml
<BootstrapSequence xmlns="http://arctisaurora/AuroraBootstrapTypes">
  <Phase Name="Bootstrap">
    <Step Action="EntityRegistry.ParseXML"/>
    <Step Action="InputHandler.LoadInputs"/>
    <!-- â€¦ -->
    <Step Action="Renderer.CreateSyncObjects"/>
  </Phase>
</BootstrapSequence>
```

`BootstrapSequence` â†’ `Phase` â†’ `Step` map to the `[A_XSDType]`-tagged classes `Bootstrapper`, `BootstrapPhase`, `BootstrapStep`, so the format is part of the generated XSD.

## Invariants & gotchas
- A `Step`'s `Action` string must match the `[A_XSDActionDependency]` **Name** exactly.
- Order is whatever the XML declares â€” **not** reflection order (reflection order is undefined).
- A missing/typo'd action is skipped with a console warning, not an exception â€” a step silently not running is the most likely failure mode.
- Planned rework (see engine notes): dependency-based sequencing driven further by XSD/XML.

## Key types
- `Bootstrapper` (static) Â· `BootstrapPhase` Â· `BootstrapStep`

## Related systems
- [[XML-XSD]] â€” supplies the attribute + parsing machinery
- [[Asset Registries]], [[INPUT]], [[VULKAN]] â€” register the steps that run here
