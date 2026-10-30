# A control that eases itself in OnTick sleeps under idle wait

2026-10-10, planner calendar scroll settle.

## What happened
- The calendar's scroll settle and all-day-lane height easing "jumped" in the real app (Thorium, `<Idle Wait>` on) but looked fine in tests.
- Both eases advance in `ArctisAurora.Core.UI.Planner.CalendarControl.OnTick` (plain fields `slide` / `shownLanes`) and call `InvalidateLayout`.
- They only advanced when something else woke a frame (touchpad reports, cursor motion, the minute redraw), so each step was large.

## Cause
- `MainSystem.Pending` counts `LayoutDirty` rows. A control that eases in `OnTick` and calls `InvalidateLayout` has its row drained by `Main.Layout` in that same frame, so at end of frame nothing is pending and main sleeps.
- Ticking entities do not keep frames coming; only wake times and animation tracks do (see [[idle-frames]]).

## Fix
- While mid-ease, `OnTick` calls `FrameScheduler.RequestFrameAt(Engine.totalTime + 0.001)` — in the `settling` branch (t < 1) and in the lane easing (`shownLanes` not at target).
- `+ 0.001` is the minimum that is not ignored: `RequestFrameAt` drops a time at or before the current `Engine.totalTime`. The next frame then runs at the frame cap's pace.
- Rejected: `Animations.Tween`. It only drives `[A_Animatable]` properties stored in a pool column (e.g. `ArrangeData.reveal` for `ExpanderControl`); `slide` / `shownLanes` are plain fields, so it would need a new column type for one float each.

## Why tests cannot see it
- `--test` sets `idle.wait = false`. A test that turned it on would stall the runner itself, and a test that requests frames to keep going would mask exactly this bug.
- Rule: any control easing itself in `OnTick` must request a wake time each frame it is still moving. Check it in the real app, not only in `--test`.

Related: [[idle-frames]], [[scroll-gesture-end]], [[verify-what-the-user-sees]]
