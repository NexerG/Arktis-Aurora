---
date: 2026-09-27
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
  - "[[Expander]]"
Parent Class:
  - "[[Vulkan Control]]"
Interfaces:
Used by:
  - "[[Rich Text Document]]"
Type:
  - Public
Attributes:
  - A_XSDType
  - A_XSDElementProperty
  - A_Animatable
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Containers/ExpanderControl.cs
VerifiedAgainst: 2026-09-27
---
## Description

A collapsible section drawn as a horizontal rule with a round grip in its middle. Closed, the grip is one circle sitting on the rule with an up arrow over a down arrow. Pressing either half splits the circle and the rule in two: the upper half stays with a rule on top, the lower half moves down with a rule under it, and the content slides open between them while each half's arrow crossfades to point back toward the other. Pressing again closes it the same way. Declarable from UI XML as `<Expander>`; the first child is the content.

A note's properties sit in one at the top of the first page — see [[Rich Text Document]].

## Members

- `Expanded` (`expanded`) — whether it is open. Setting it jumps there without animating; the XML attribute sets the starting state.
- `reveal` — 0 closed, 1 open. Animatable, stored in the layout row, and a change re-measures, so the parent makes room as it moves.
- `Toggle()` — flips the state and animates `reveal` and the arrows over 0.2 s with a cubic ease-out, starting from wherever they are, so a press during the motion turns it round.

## Layout

The grip is 24 px across. Closed, the control is exactly the grip's height; open, it adds the content's height times `reveal`.

#### Measure (available)
measure the content at the available width, unbounded height
width = the available width, or the content's when the offer is unbounded
height = grip diameter + content height × `reveal`

#### Arrange (rect)
`seam` = top of `rect` + grip radius
`shown` = height of `rect` − grip diameter
place the content window from `seam`, `shown` tall, the content held against its bottom edge
place the upper rule just above `seam` and the lower rule just above `seam` + `shown`
place the upper half on top of `seam` and the lower half below `seam` + `shown`

Each half is a clip half the grip's height over a whole circle, pushed up or down so only one half of it shows. A panel cannot be drawn as a semicircle directly — the shader limits a corner radius to half the panel's shorter side — and the clip also leaves the flat side without a stroke.
