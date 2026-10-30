# Decision — a control added under a destroyed parent is warned about, and Thorium's vault browser stops reacting once it is off the primary window

**Date:** 2026-10-08
**Status:** LANDED. Guard and DEBUG warning test-verified. **NOT GUI-verified.**
**Scope:** `ArctisAurora.Core.ECS.EngineEntity.Entity` (`AddChild`, `WarnIfDestroyed`), `ArctisAurora.Core.UI.Containers.ContainerControl` (`AddChild`), `Thorium.Editor.CustomControls.VaultBrowserControl` (`OnWorkspaceChanged`), `WorkspaceControl.changed`

## What changed
- `Entity`: NEW channel `LogChannel.For("Entity")`; NEW `[Conditional("DEBUG")] protected WarnIfDestroyed(Entity)`, called from `Entity.AddChild` and `ContainerControl.AddChild`. Warns "'x' added under destroyed 'y'; it will outlive it".
- `VaultBrowserControl.OnWorkspaceChanged` returns unless this browser is still in `Engine.primary`'s tree.
- Not covered by the check: `Entity.CreateChildEntity`, and controls that add to `children` directly (`CalendarControl`, `ContextMenuControl`, `DropdownControl`, `ExpanderControl`).

## Why these choices

**The cause: `Entity.Destroy` enqueues the subtree at call time, so anything added to it afterwards is never destroyed.**
When a test replaces the primary window's content, Boot's `VaultBrowserControl` is queued for destroy but stays subscribed to `WorkspaceControl.changed`. A test that added a non-General workspace in its first frame made that browser rebuild its rows under its already-destroyed row stack. Those rows are outside the destroy queue and no root reaches them.

**The symptoms were two logs, every tick.**
`UIElements` resequence refused ("order count 336 != live count 381 — skipping") and `LayoutEngine.VerifyStructure` logged "is row N, pre-order puts it at M" in every later test. Proven by instrumented runs: the lost controls were `FileRowControl` subtrees whose grandparent `VaultBrowserControl` had `destroyed=True`.

**The fix is the guard; the warning is so the next case names itself.**
The guard stops this one subscriber. The warning is DEBUG-only (`[Conditional]`) so Release pays nothing. The only other `WorkspaceControl.changed` subscriber, `WorkspaceBarControl`, already answers only its own window's workspace and posts its rebuild behind a `destroyed` check.

## Known gaps
- The check is not on `Entity.CreateChildEntity` or on controls that add to `children` directly (list above).

Related: [[entity-lifecycle-queues]], [[workspaces]], [[vault-browser-and-shell]]
