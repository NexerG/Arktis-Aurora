# Mistake — a hand-arranged control must Measure a child panel at the size it will Arrange it to

**Date:** 2026-10-23
**Found by:** a golden of `Startup.ToggleSwitch` (the Accent layer never drew) and the DEBUG `UIEngine` log
**Scope:** `ArctisAurora.Core.UI.Controls.ToggleSwitchControl`

## The trap
- A code-built control that arranges its children by hand measured its track with `track.Measure(availableSize)`.
- An empty/childless `PanelControl` measures to its offer, so the track's own child (`lit`) was single-child-arranged against the window width and an infinite height: rect (-578.9, -1.7e38, 1256, 3.4e38).
- The Accent layer never showed, although a test of its alpha passed.
- Arranging `lit` a second time by hand fixed its rect but left the track's maintained `subtreeBounds` stale; DEBUG `UIEngine` logged `'entity' subtreeBounds (...) != recomputed (12, 12, 74.2, 24)` every frame (12 errors in the run).

## The fix
- `track.Measure(new Vector2(TrackWidth(caption), trackHeight))` in `MeasureCore`, so the single-child arrange fills the track on the first pass. The knob already did this: `knob.Measure(new Vector2(knobSize, knobSize))`.

## Lesson
- Measure a child panel at the size it will be arranged to, never at `availableSize`.
- Do not arrange a child a second time by hand to repair a rect; the parent's `subtreeBounds` goes stale.
- Check a golden, not just a value: an alpha check passed while nothing drew.

Related: [[switch-controls]], [[arrange-must-exit-clean]], [[verify-what-the-user-sees]]
