---
name: aurora-verify
description: Verify a change to Aurora and work out which verification claim is honest (builds clean, test-, golden-, shot- or GUI-verified). Use before reporting a change as done or working, before calling something an engine defect, whenever a golden image or readback PNG is offered as evidence, and whenever driving the app with synthetic input or screen capture — the capture and input paths here have both produced false results before.
---

# Verifying a change

`<Host>.exe --test` runs the test suites inside the host and exits with the failure count; whatever no test
covers is still tested by hand. `--profile-scenario` drives typing, resize and animation but asserts nothing.
So the only honest claim is the one matching what was actually done, and the words below are the ones the WIP
list already uses. Running and writing tests, and poking a running app with `--send`, are `aurora-test`.

## The ladder — claim the rung you reached, not the one above

| Claim | Means | How |
|---|---|---|
| **builds clean** / **compile-verified** | it compiles | `dotnet build` |
| **boot-verified** | the app started and got through bootstrap | launch it, watch for the throw |
| **test-verified** | named tests covering the change passed under `--test` | `<Host>.exe --test[=<Suite>]` (`aurora-test`); a logic check, not a visual one |
| **golden-verified** | a `t.Golden` shot matched, pixel for pixel, a golden a person approved | `--test` (`aurora-test` § Golden images); only as good as the approval |
| **shot-verified** | you looked at a readback PNG of the presented frame yourself | a throwaway `t.Golden` test, Read its `actual.png` (`aurora-test`); for a TeX PDF export, `aurora-pdfdiff` (text positions) |
| **GUI-verified** | someone looked at the window and saw the behaviour | capture, or the user looked |
| **NOT GUI-verified** | the honest default | say it out loud |

`NOT GUI-verified` is not a failure state and is written all over the WIP list. Claiming a rung you did not
reach is the failure state.

Performance is a separate axis: a timing claim names its build, run count and tool, and follows `aurora-perf`.

**A readback is not GUI-verified.** It proves what the swapchain held under a test run — fixed clock, display
scale 1, no OS input, no idle waiting — not what the user sees with real input, DPI and window chrome. Write
`golden-verified` or `shot-verified`, and still `NOT GUI-verified` next to it. A golden you approved in the same
change is `shot-verified`, not `golden-verified`: the approval is the thing being checked.

Bootstrap gives one check for free: an action name in `AuroraEngine/Data/XML/Documents/Bootstrap.bootstrap.axml`
or a host's `Data/XML/Documents/Inputs/InputMap.inputs.axml` that resolves to nothing throws at
`InputHandler.LoadInputs`, so a clean boot proves every declared action exists.

## Build

```bash
cd "$(git rev-parse --show-toplevel)" && _Build/build.sh
```

Exit code is the build's own; `BUILD OK` is the only pass.

Nothing in the build compiles or validates shaders — a `.spv` change is invisible to it. See `shader-pipeline`.

## Launching

Paths resolve against the exe's own folder (`AppContext.BaseDirectory`) since 2026-09-27, so the exe starts
from any working directory, `dotnet run` included.

Three hosts boot the engine, each with its own `Data/` beside its exe, so "the app" means naming one:
`Thorium/bin/Debug/net10.0-windows10.0.22621.0/Thorium.exe`, and the same path shape for `Carbon.exe` and
`AuroraEditor.exe`. `ArctisAurora.exe` sits in those folders too — it is the engine assembly, not the host.

## Capture

**Try the readback first.** If a test can set the scene up, `t.Golden("Window")` in a throwaway test gives the
presented frame as a PNG from inside the process, with none of the traps below (`aurora-test` § Looking at what the
app draws). `capture.ps1` is for what a test cannot show: a normally launched app, idle and wake behaviour, OS window
chrome, the real display scale, and secondary windows.

Three things that do not work, so they are not worth retrying:

- `CopyFromScreen` loses to whatever owns the foreground — usually the Claude window.
- `SetForegroundWindow` from a background shell is blocked by Windows' foreground lock.
- `PrintWindow` comes back blank, because the surface is Vulkan-rendered.

What works: `SetWindowPos(hwnd, HWND_TOPMOST, …, 0x43)` → capture → `SetWindowPos(hwnd, HWND_NOTOPMOST, …)`.
`capture.ps1` beside this file does exactly that, DPI-aware, client area only — do not retype it:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/aurora-verify/capture.ps1 -Process Thorium -Out <scratchpad>/shot.png -Region 740,56,210,40
```

**Crop by default.** `-Region x,y,w,h` is in client pixels. A crop costs a fraction of a full-window shot and is
not downscaled, so it shows more for less. Leave `-Region` off only when the whole layout is the question.

**A structural dump is not a visual check.** A control-tree dump of parents, arranged rects and glyph counts
verifies geometry and wiring and says nothing about what is drawn. This exact substitution was reported as
"verified" on 2026-08-07 while the window was mostly white — a `StackPanelControl` had inherited the default
`maskAsset` and was painting a solid quad under white text. If capture fails, say the visual check did not
happen. Do not reuse the word for a proxy.

**F10 writes that dump for the new stack** — `UI.UITreeDump` (`UI.DumpTree`) → `uitree.axml` beside the
exe: every window's tree with arranged `X Y W H`, desired `W H`, `Hidden`. Thorium's is ~17 KB, so grep it for
the control in question; never read it whole. With the root unscaled a rect is client pixels, i.e. directly a
`-Region` — grep the control, then crop the capture to it. Rects are design units: at a UI zoom or display
scale other than 100% (`WindowRoot.scale`), multiply by that scale to get client pixels. F10 needs the window focused: click an empty pane
first (a title-bar click enters the OS drag loop) — or skip the keypress: `Thorium.exe --send UI.DumpTree` against
the running app writes the same file.

## Synthetic input

Reach for it last. A dump or an action in a running app is a `--send`; behaviour is a test — both run inside the
engine, so none of the traps below apply (`aurora-test`). OS input is left for what only a real keypress or pointer
shows: a keybind's wiring, a hover, a drag.

`SetCursorPos` + `mouse_event` for the pointer, `[System.Windows.Forms.SendKeys]` for characters.

Five traps, each of which has already produced a wrong conclusion:

- **`UIEngine.Poll` returns immediately while `window.isInWindow` is false** (unless the window owns the drag). Input that leaves
  the pointer outside the window makes the *next* drag silently do nothing, which reads as a broken feature.
  Move the cursor inside and let a tick pass before driving anything.
- **`SetWindowPos` from another process is not a resize.** It produces a `WM_SIZE` whose 16-bit `HIWORD` is
  `0xFFFF`, so GLFW reports `900x65535`, Vulkan is honestly asked for a 900x65535 swapchain, and honestly
  returns `ErrorOutOfDeviceMemory`. `MoveWindow`, `ShowWindow(SW_MAXIMIZE)` and a real pointer drag all resize
  it correctly.
- **`Text.Write` is bound `<Continuous/>`.** `SendKeys` steals focus, the key's release lands on another
  window, and the bind keeps firing — pouring characters into whatever note has focus. Drive text with
  explicit key-up, and restore any note the probe wrote to.
- **`keybd_event` goes to the foreground window, and a capture between keystrokes can take it.** Every
  `capture.ps1` run is another process; once it has the foreground the next key lands nowhere and the app reads
  as ignoring the gesture. Ctrl+A then Ctrl+B did nothing after an intervening capture on 2026-09-12 and worked
  on the first try when sent in the same call as the click that focused the window. Drive and capture in
  separate calls, click into the window first, and assert `GetForegroundWindow()` against the process's
  `MainWindowHandle` before believing a negative result.
- **Extended keys do not arrive at all.** Arrow keys, Home/End, PageUp/PageDown and Delete produce nothing
  through `keybd_event` (with or without `KEYEVENTF_EXTENDEDKEY`) or `SendKeys`, while characters, Enter,
  Backspace, F10 and every Ctrl combo work through the same paths. Caret movement and `Text.Delete` can only be
  checked by hand — do not report them as broken from a script.

## Before calling something an engine defect

- **"The unmodified build does it too" proves only that your change is not the cause.** It is not evidence the
  engine is at fault — the harness is unmodified in both runs too.
- **Reproduce through a second, independent input path** before reporting it. `MoveWindow` vs `SetWindowPos`,
  or the app's own chrome.
- **Probe the values before naming a cause.** Two rounds of reasoning about `RecreateSwapchain` produced
  nothing; one print of the requested extent produced the answer immediately.
- CLAUDE.md fences off the Vulkan internals. Going in needs evidence that holds.

## If a claim turns out to be wrong

Retract in **every** place it was made — the WIP list and each decision note that repeated it, not just the
newest file. A false premise the user has already answered a question against is the expensive failure here.
