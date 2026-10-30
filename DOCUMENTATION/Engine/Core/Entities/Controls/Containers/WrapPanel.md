---
date: 2026-10-09
Status: Current
tags:
  - d_UI
  - d_Entity
cssclasses:
  - Aurora.css
Linker:
  - "[[Entity]]"
System:
Class:
  - "[[WrapPanel]]"
Parent Class:
  - "[[Vulkan Control]]"
Interfaces:
Used by:
  - "[[Planner Editor]]"
Type:
  - Public
Attributes:
  - A_XSDType
  - A_XSDElementProperty
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Containers/WrapPanelControl.cs
VerifiedAgainst: 2026-10-09
---
## Description

A container that places its children left to right at their desired size and starts a new line when the next child would run past its width. Declarable from UI XML as `<WrapPanel>`. It is its own layout kind, `LayoutNodeKind.Wrap`, so the layout engine lays it out from the row data like a [[StackPanel]] rather than through a virtual call.

It exists because a horizontal [[StackPanel]] offers every non-star child unlimited width, so a row of chips grew its parent instead of folding. The planner board's assignee chips were the first case.

## Fields & Properties

```C#
[A_XSDElementProperty("Spacing", "UI", "Space between children and between lines in pixels.")]
public float Spacing;
```

One `Spacing` serves both directions, so the layout node needed no new field.

## Methods

### Measure
The box width is the panel's own `Width` when set, otherwise the offer, less padding. Each child is measured against that width and placed on the current line if it fits; otherwise a new line starts. A child wider than the box gets a line to itself. Desired width is the widest line; desired height is the lines plus the spacing between them. `Width`/`Height` are a floor, as on [[StackPanel]].

### Arrange
```
Arrange(finalRect)
	inner = finalRect shrunk by padding
	for each visible child
		width = desired width, clamped to inner's width
		if the line has a child and this one would run past inner's right edge
			start a new line below the tallest child so far, plus Spacing
		place the child at its desired height, top of the line
```

## XML
```xml
<WrapPanel Spacing="4">
  <!-- child controls -->
</WrapPanel>
```

## Related
- [[StackPanel]] — the one-axis sibling
- Test `Layout.WrapPanelWraps`
