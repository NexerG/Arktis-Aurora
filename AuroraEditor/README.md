# AuroraEditor

Depends on: **Engine project**

## Purpose
Visual editor for MyEngine. Built on top of the Engine — uses the same ECS, rendering, and asset systems.

## Status
Early stage. Core editor shell is being set up.

## Architecture Notes
- Editor is a consumer of the Engine, not a fork of it
- UI is rendered through Engine's Vulkan pipeline
- [Add: docking, panels, tool windows — describe once they exist]

## What Claude Should Know
- Editor-specific code lives here; Engine internals are described in the root `CLAUDE.md`
- Don't duplicate engine logic in the editor — extend via engine APIs
