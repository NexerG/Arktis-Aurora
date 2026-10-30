---
date: 2026-09-29
Status: Current
tags:
  - d_UI
cssclasses:
  - Aurora.css
Linker:
  - "[[Vulkan Control]]"
System:
Class:
  - "[[Image Control]]"
Parent Class:
Interfaces:
Used by:
Type:
  - Public
Attributes:
  - A_XSDType("Image", "UI")
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Controls/ImageControl.cs
VerifiedAgainst: 2026-09-29
---
## Description

A picture from a file, drawn as one quad of the `ImageControl` kind. The fragment shader multiplies the control's colour by the sampled texel, so the control paints itself white and the picture shows exactly as the file holds it, rounded corners and edges included.

Textures come from `TextureAsset.ForFile`, which keeps one texture per file path. Two controls naming the same file share one slot of the 256-slot texture table, and a file that fails to load, or arrives after the table is full, gives a warning and an empty control rather than an exception. Nothing frees a slot yet; that waits for the engine image upgrade.

Every picture is uploaded with a full mip chain, so one shown smaller than its file stays smooth instead of shimmering. The chain is built on the CPU, each level half the one above and averaged in linear light, and goes up in the same transfer submit as the base image. A picture larger than the GPU's texture limit is shrunk to fit before upload.

This is the first half of pictures in notes. Pasting into a Thorium note, inline and wrapped pictures and resize handles follow the plan in `ClaudeMemory/Context/note-images-plan.md`.

## Authoring

```xml
<Image Source="Pictures/splash.png"/>
<Image Source="C:/Users/me/Pictures/cat.jpg" Width="240"/>
```

`Source` is an absolute path, or one relative to the data mounts. With no size the picture measures at its native size, capped to the width it is offered; `Width` or `Height` sets one side and the picture's aspect gives the other; both set stretch it.

## Clipboard

`ClipboardImage.TryGet` reads a picture off the Windows clipboard. It tries, in order, the `PNG` format browsers and editors put there, the two device-independent bitmap formats a screenshot tool puts there, and finally the first image file among files copied in Explorer. A bitmap whose alpha is zero everywhere is read as opaque, since that is how Windows writes one with no alpha at all.

## Methods

`MeasureCore(availableSize)`
	native = the texture's size, or zero with no texture
	aspect = native height / native width
	if Width is set
		width = Width
		height = Height if set, else width * aspect
	else if Height is set
		height = Height
		width = height / aspect
	else
		width = min(native width, available width)
		height = width * aspect

`TextureAsset.ForFile(path)`
	full = the absolute path
	if a texture is cached for full, return it
	if the table is full, warn and return null
	load the file, upload it with mipmaps, cache it and return it
	on any failure, warn and return null

`ClipboardImage.TryGet(out image)`
	open the clipboard, or warn and give up if another application holds it
	if the PNG format is present, decode it
	else if a DIBV5 or DIB is present
		prefix a bitmap file header and decode it as BMP
		if it is 32-bit and every alpha is zero, make it opaque
	else if files were copied, decode the first one with an image extension
	close the clipboard

## Related
- [[Vulkan Control]] — the quad row the picture is drawn with
- [[Gradients]] — the other way a control's colour is replaced in the shader
