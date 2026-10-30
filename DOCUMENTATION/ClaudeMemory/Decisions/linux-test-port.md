# Decision — the in-engine test suite runs on Linux through per-site Win32 guards, not a platform layer

**Date:** 2026-10-16
**Scope:** `ArctisAurora.EngineWork.Rendering` — `AGlfwWindow`, `TouchpadInput`, `DisplayNames`; `ArctisAurora.EngineWork` — `Engine`; `ArctisAurora.Core.Filing` — `ClipboardImage`, `FileDialog`; `ArctisAurora.Core.Filing.Serialization` — `Paths`, `MeshImporter`; `Registry.Assets.FontAsset`; `Rendering.Helpers.AVulkanHelper.GetSwapchainSurfaceFormat`; `ArctisAurora.Core.Security` — `WindowsHello`; `ArctisAurora.Tests.BackgroundTests`; `_Build/test.sh`; `ArctisAurora.Core.UI` — `IconControl.Emit`; `LINUX.md` (repo root)

## What changed
- Thorium's `--test` suite builds and runs on Linux (Ubuntu 24.04, cloud container). Every Win32 P/Invoke the boot and test paths reach is behind `if (!OperatingSystem.IsWindows()) return …`.
- `AGlfwWindow`: `RoundCorners`, `AllowSnapping`, `Raise`, `DragByCaption` no-op off Windows; `SetBounds` goes through `glfw.SetWindowPos` + `SetWindowSize`; `GetPlacement` returns the GLFW pos/size (with the maximized flag).
- `TouchpadInput.Register` no-op. `DisplayNames.At` returns null (falls back to GLFW's monitor name; also keeps its WinRT `DisplayMonitor` type unloaded). `ClipboardImage.TryGet` returns false. `FileDialog.Pick` returns without opening (the callback never fires).
- `Engine()` logs `Environment.ProcessId` instead of kernel32 `GetCurrentThreadId` off Windows (Linux main-thread tid == pid).
- `Paths`: the `Data\\…` constants now use `/` (`GetPath("Data/XML")` etc.); `FontAsset` atlas paths join with `/`.
- `MeshImporter`: new static constructor; off Windows it calls `NativeLibrary.SetDllImportResolver(typeof(AssimpContext).Assembly, …)` mapping `"libdl.so"` to `NativeLibrary.Load("libdl.so.2")`.
- `_Build/test.sh`, `uname -s` = Linux: builds `Thorium/Thorium.csproj -f net10.0` alone with `-p:EnableWindowsTargeting=true` (still needed: restore sees the engine's Windows target), runs `Thorium` (no .exe) from `Thorium/bin/Debug/net10.0` under `xvfb-run -a -s "-screen 0 1920x1080x24"` with `VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json`. On both platforms the run's ERROR/FATAL lines go through `tr -d '\r'` before the baseline compare (the log writes CRLF; only the baseline side was stripped before, so every baseline error read as new on Linux).
- `AssetImporter.HashFiles` drops `\r` bytes from each source before hashing, so an icon or font stamp is the same on a CRLF (Windows) and an LF (Linux) checkout.
- `AVulkanHelper.GetSwapchainSurfaceFormat` takes `R8G8B8A8Unorm`, else `B8G8R8A8Unorm` (both sRGB-nonlinear), before falling back to `formats[0]`.
- Minimize-to-tray gating, found on the way: [[tray-availability-gate]].
- Multi-target (2026-10-17): `AuroraEngine.csproj` and `Thorium.csproj` build `net10.0-windows10.0.22621.0;net10.0`, so a Linux build carries no WinRT assemblies. `ArctisAurora.Core.Security.WindowsHello` is wholly under `#if WINDOWS` with a `#else` stub (`IsAvailable()` false, `Sign(...)` null, `Delete(...)` completed task). `DisplayNames` keeps `using Windows.Devices.Display` and the `DisplayMonitor` lookup in `PanelName` under `#if WINDOWS` (off Windows it falls back to the device-id split). `AVulkanHelper`'s unused `using Windows.Foundation.Metadata` is gone. `_Build.csproj`'s `PostBuild` (`GenerateNamespaces.cmd`) is conditioned on `$(OS) == 'Windows_NT'`. Reasoning and measurements: [[arx-archives]].
- Regression fixed 2026-10-28: `ArctisAurora.Core.UI.Controls.IconControl.Emit` returns when `sampler == null` — an icon with nothing bound draws nothing. Before it, every `--test` run segfaulted inside lavapipe (see Why).
- New `LINUX.md` at the repo root: what to install (dotnet-sdk-10.0, mesa-vulkan-drivers, libvulkan1, vulkan-validationlayers, libglfw3, xvfb, xauth, libminizip1t64; optional glslc, vulkan-tools, gdb, pypdf/pdfplumber), how to build, test and export headless, the expected failures, and the GPU-assisted-validation recipe for driver crashes.

## Why these choices

**Each Win32 site gets an `OperatingSystem.IsWindows()` guard rather than a platform-abstraction layer.**
It is a JIT-time constant: zero cost on Windows, and the Windows paths stay byte-identical. The goal was tests running on Linux, not a Linux product, so an abstraction would be speculative.

**The AssimpNet `libdl` fix is a resolver in `MeshImporter`'s static constructor.**
Rejected: upgrading to AssimpNet 5.0.0-beta1 — its package was inspected and its loader still P/Invokes the unversioned `"libdl.so"`, so it fixes nothing. Rejected: a container symlink `libdl.so` to `libdl.so.2` — every Linux machine would need it. The resolver lives in `MeshImporter` because `MeshImporter.ImportFBX` is the only `new AssimpContext()` in the engine. It loads on glibc 2.34 and later.

**Data paths use `/`.**
`Path.GetFullPath` normalizes `/` to `\` on Windows, so the resolved values there are unchanged; on Linux `\` is a filename character, not a separator.

**Import stamps hash with `\r` stripped, for every source (icons and fonts).**
`* text=auto` checks SVGs out LF on Linux and CRLF on Windows, and a raw-bytes hash made each platform rewrite the other's stamp. Rejected: `*.svg text eol=crlf` in `.gitattributes` — existing Linux clones would need a re-checkout. Rejected: stripping for icons only — the user chose one rule for all of `HashFiles`. Cost: every existing font and icon stamp changes once, so the next Windows run re-bakes every font atlas once (the committed atlas PNGs may change then).

**The swapchain falls back to `B8G8R8A8Unorm` in a second pass, not by accepting either UNORM in one.**
The UI shaders write sRGB-encoded colour, so they need a UNORM swapchain. Lavapipe on X11 offers only `B8G8R8A8_SRGB` (first) and `B8G8R8A8_UNORM`; the old helper accepted only `R8G8B8A8Unorm` and fell to `formats[0]`, an SRGB format, whose store encoded every pixel a second time (golden `(46,92,138)` read back `(118,162,194)` — exactly one more sRGB encode). Two passes keep Windows on the format it picks today whatever order its driver lists; one pass could move an NVIDIA driver (which tends to list `B8G8R8A8_UNORM` first) onto BGRA. `ScreenReadback.TryTake` already reads both. Rejected: re-approving the goldens on Linux (would bake the double encode in); a pixel tolerance (would hide a real colour bug).

**A control with no texture bound is not emitted; the shader is not guarded (2026-10-28).**
From commit 39b5bbf (2026-10-21) `bash _Build/test.sh` died with a segfault inside lavapipe (an `llvmpipe` thread in JIT-compiled shader code); `git bisect` between 6bbeab0, the last Linux pass, and HEAD found it. GPU-assisted validation (`VK_LAYER_ENABLES=VK_VALIDATION_FEATURE_ENABLE_GPU_ASSISTED_EXT`) turned the crash into `[Vulkan] … Descriptor index out of bounds … (set = 2, binding = 0) Index of 4294967295 used to index descriptor array of length 256 … Stage = Fragment`. `UIEngine.frag`'s glyph path (`msdfDistance`) samples `samplers[fragTextureIndex]` with no `NO_TEXTURE` check, and `FileBrowserControl.AddRow` gives file rows an `IconControl` gutter with `iconName = ""` (kept so names line up with folder rows); `Rebind` never binds a texture, but the control was still emitted as an MTSDF quad. 39b5bbf added `testNote.md` and `testsheet.sheet.axml` to Thorium's test vault, the first file rows. A GPU returns garbage silently; lavapipe segfaults. The user picked the root-cause fix (`IconControl.Emit` returns when `sampler == null`). Not done: also guarding `msdfDistance()` against `NO_TEXTURE`, which would stop any future texture-less glyph crashing lavapipe but needs the four shader copies rebuilt.

**GPU-assisted validation is the diagnosis recipe for a driver crash.**
It turns the lavapipe segfault into a `[Vulkan]` line naming the binding and index, and lets the run finish.

**Container provisioning is environment, not repo.**
`apt install dotnet-sdk-10.0 mesa-vulkan-drivers libvulkan1 vulkan-tools libglfw3 xvfb xauth vulkan-validationlayers libminizip1t64`. The maintained list (vulkan-tools is optional there) lives in `LINUX.md` at the repo root.
- builds.dotnet.microsoft.com is blocked by the cloud proxy; Ubuntu's archive carries dotnet-sdk-10.0 (10.0.112 when first provisioned; `LINUX.md` says 10.0.113 on 2026-10-28).
- Validation layers are required because Debug enables `VK_LAYER_KHRONOS_validation` and instance creation fails without it (ErrorLayerNotPresent).
- `libminizip1t64` is a dependency of AssimpNet 4.1.0's bundled `libassimp.so`.
- Lavapipe (Mesa 25.2.8, Vulkan 1.4) meets the renderer's 1.3 floor ([[renderer-minimum-requirements]]).

## Verification
Test-verified on Linux: `bash _Build/test.sh` gave 275 passed, 52 failed, 1 skipped, 0 new; no `[Vulkan]` lines; no new error kinds. The 52 failures are `Boot` (the pre-existing baseline sampler error `failed to load the default sampler asset`, same as Windows) plus 51 golden-image mismatches — at first not rasterization but the SRGB swapchain's second encode (every pixel lighter; e.g. `Visual.PanelAndLabel` 15928 px, max delta 92). After the swapchain format fix the same 51 still fail (`Visual.PanelAndLabel` 246 px, max delta 19; `Visual.Expander` 19 px, max delta 6), on 61 failing shots in three classes, measured against the goldens with "edge" = golden 3×3 luminance range ≥ 8: 42 shots differ on edges only (anti-aliasing, mostly glyph outlines); 13 also differ by 1–3 levels across large flat areas (Planner week/month/Gantt, `Sheet.GridDraws`, `Tex.FloatPage`, `TextInput.ColorPickerPicks`, `TextInput.JustifyLines` — invisible, likely blend or gradient rounding); 6 TeX shots (`Tex.Preview`, `Tex.Floats`, `Tex.Amsmath`, `Tex.Pages`, `Tex.FootnotePage`, `Tex.FloatPage`) also draw faint dots on the baseline of some inter-word spaces (delta 11–31 on flat page background) — a real rendering difference, not anti-aliasing. Unverified suspicion for the dots: bilinear sampling at a space's empty atlas cell picks up a neighbouring cell on lavapipe. `Background.HideShow`, `MenuClamp` and `ClosePrompt` pass. Runs after the `HashFiles` change gave 274 passed, 53 failed — the extra one `Notes.LocalWins` (see Known gaps). Windows: NOT run — builds clean on Linux only; Windows behaviour rests on every Windows branch being unchanged.

2026-10-28 (the `IconControl.Emit` fix): test-verified on Linux. Before the fix every `--test` run segfaulted before reporting; after it the full suite gave 360 passed, 57 failed, 5 skipped (Boot, `Sheet.PasteLinks`, `Sheet.NotePasteLinks`, 54 goldens on lavapipe), 0 `[Vulkan]` lines, and also 0 under GPU-assisted validation for the Tex suite. Not run on Windows (the change only skips drawing a control that had no texture).

## Known gaps
- 54 goldens fail on lavapipe on 2026-10-28 (51 on 2026-10-16, after the swapchain format fix; the breakdown here is of those 51): 42 shots edges only, 13 with 1–3 level flat-area deltas, 6 TeX shots with baseline dots at inter-word spaces; there are no Linux goldens or tolerance. A tolerant compare (delta ≤ 3 anywhere, or any delta where the golden's 3×3 luminance range ≥ 8) was measured to pass 55 of 61 shots and fail only the 6 TeX shots on the dots; not built.
- On Linux: the title bar does not drag the window (`DragByCaption` no-op; `TitleBarControl` still posts its synthetic button-up, so GLFW's real release arrives as a second up — believed harmless, unverified); Raise-without-focus is a no-op; maximized placement saves the maximized rect, not the restore rect; image paste and file/folder pickers do nothing; no tray, no single-instance claim; console output is uncoloured (`ConsoleSink`'s try/catch fallback).
- `Notes.LocalWins` failed in 2 of 4 full Linux runs (`XmlException`, a torn note file) and passed 3/3 alone. Suspected: the test's outside write races the session's save, and Linux file locks are advisory where Windows `FileShare` blocks a mid-write read. Unconfirmed.
- `_Build` itself builds on Linux since 2026-10-17 (its `PostBuild` is Windows-only); `test.sh` still builds Thorium only, and a full-solution Linux build was not checked (Carbon and AuroraEditor are Windows-TFM only).
- `test.sh` on the new `net10.0` path on Linux: done 2026-10-28 (see Verification). The 2026-10-17 Windows runs are in [[arx-archives]].
- The shader still has no `NO_TEXTURE` guard on the glyph path.
- Carbon and AuroraEditor have not run on Linux. There is no SessionStart hook to provision the container.

Related: [[arx-archives]], [[tray-availability-gate]], [[engine-testing]], [[renderer-minimum-requirements]], [[desktop-stickies]]
