---
date: 2026-09-27
tags:
  - d_System
cssclasses:
  - Aurora.css
Status: Current
Linker:
  - "[[Arctis Aurora]]"
System:
  - "[[TESTING]]"
Dependencies:
  - "[[Bootstrapper]]"
  - "[[LOGGING]]"
  - "[[THREADING]]"
  - "[[SETTINGS]]"
Implementors:
  - "[[TESTING]]"
Namespace: ArctisAurora.Core.Testing
SourceFiles: AuroraEngine/Core/Testing/*.cs, AuroraEngine/Tests/*.cs
VerifiedAgainst: 2026-09-27
---
## Overview

Tests run inside the real application. Launching a host with `--test` boots it exactly as normal, then a runner takes over the primary window, runs the test suites one test at a time over real frames, writes down what passed and what failed, and exits with the number of failures as its exit code.

There is no unit-test framework. A test is a coroutine: it builds a fixture, lets frames pass, and checks what the engine did with it. A test that lets no frames pass is simply a function check.

## Running

```
Thorium.exe --test
Thorium.exe --test=Layout
```

Run it from the host's own `bin` folder, like any launch. `--test` runs every suite and `--test=<Suite>` runs one. Each test prints a `PASS` or `FAIL` line, and the run ends with a summary line and the path of its results file, `%APPDATA%\Arktis\<Host>\Tests\<yyyyMMdd-HHmmss>\results.axml`.

The first entry is always **Boot**: the host's own UI runs for 60 ticks, and Boot fails if anything logged an error since launch.

The suite also runs on Linux. `bash _Build/test.sh` there builds Thorium alone and runs it, without the `.exe`, under a virtual X display with Mesa's lavapipe software driver as the Vulkan device. Lavapipe offers no `R8G8B8A8_UNORM` swapchain, so the renderer falls back to `B8G8R8A8_UNORM`; before that fallback existed it landed on an SRGB swapchain, which encoded every colour twice and turned every golden lighter. Colours now match, but the golden images approved on Windows still fail on Linux (54 of them): most differ only where a software rasterizer anti-aliases edges differently from the Windows GPU, some also by an invisible one to three levels across flat areas, and six TeX pages draw faint dots under some spaces between words, which is a real rendering difference. Every other test passes except the pre-existing Boot sampler error. `LINUX.md` at the repository root lists what to install, how to build, test and export headless, which failures to expect, and how to turn a driver crash into a validation message with GPU-assisted validation.

## Writing a test

A test is a static method tagged as a `Test` action. It is handed a `TestContext`, and every `yield return n` lets n ticks pass.

```csharp
[A_XSDActionDependency("Layout.StarChildCrossSize", "Test")]
private static IEnumerator<int> StarChildCrossSize(TestContext t)
{
    StackPanelControl bar = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal, preferredHeight = 32f };
    bar.AddChild(new LabelControl { text = "Untitled", widthStar = 1f });
    t.Show(bar);
    yield return 2;
    t.Check(bar.arrangedRect.height == 32f, "the bar is arranged 32 px tall");
}
```

`Show` makes a control the primary window's whole tree, destroying whatever was there before. `Check` records a failure together with its file and line, and the test carries on.

A suite lists its tests in order, one file per suite, and the file's name is the suite's name.

```xml
<!-- Data/XML/Documents/Tests/Layout.tests.axml -->
<TestSuite xmlns="http://arctisaurora/AuroraTestTypes">
	<Test Action="Layout.StackPanelArranges"/>
	<Test Action="Layout.StarChildCrossSize" Timeout="5000"/>
</TestSuite>
```

`Timeout` is in milliseconds of test time and defaults to 10000. Engine tests live in the engine and run in any host; a host's own tests live in that host and its `Data` folder.

## Playing input

No window hears the OS during a run, so a test plays its own input. Each helper queues a gesture and returns how many ticks it takes, which is what the test yields:

```csharp
yield return t.Click(button);
yield return t.Type("Hi 5!");
yield return t.Key(Keys.GraveAccent, Keys.LeftControl);
yield return t.Drag(splitter, new Vector2(300f, 40f));
```

The runner plays one step a tick, from the test's own tick, so each step is handled by the next tick's input pass — the same tick an OS callback would have reached. When the helper's ticks are up, the whole gesture has been handled.

| Helper | Steps, one per tick |
|---|---|
| `MoveTo(control)` / `MoveTo(window, point)` | the pointer moves to the control's centre, or a design-space point |
| `Click(control, button)` | move, button down, button up |
| `Click(control, point, button)` | the same at a design-space point, with any mouse button; the test fails if the point does not hit the control or a descendant |
| `Drag(control, to, steps)` | move, left down, `steps` moves to `to`, left up |
| `Key(key, modifiers)` | modifiers down, key down, key up, modifiers up |
| `Type(text)` | per character: its key down with the character, then its key up |

```
Point(control)
	window = the window the control is drawn in
	centre = the middle of the control's arranged rect
	if the hit-test at centre is not the control or inside it
		record a failure naming what it hit
	every window's isInWindow = (it is this window)
	move the pointer to centre, converted to window pixels by ToWindowSpace
```

Pointer moves and characters go through the same handlers the OS callbacks call. Keys and mouse buttons go straight into the key tracker as engine `Keys`, because the keyboard handler only translates a GLFW key and enqueues it. `Type` presses the real key for letters, digits and space and `AnySymbol` for anything else, which is what makes an `AnySymbol` keybind fire. Keybinds are host data, so a test that needs one belongs to a host that binds it — typing into a text box is a Thorium test because only Thorium binds `AnySymbol` to `Text.Write`.

A window's `isInWindow` starts false under `--test` and only the helpers set it, so the user's own mouse over the window never reaches a run.

## What fails a test

- a `Check` that came out false
- an exception, reported with its type, message and the line that threw
- running past its `Timeout`
- any error or fatal logged while it ran
- a suite line naming a test that does not exist, or `--test=<Suite>` naming a suite that does not exist

## What a run changes

- **Time is fixed.** Every tick is exactly 1/60 s of engine time, so key repeat, double-click windows, caret blink, animations and shader effects advance identically on every run, however fast the machine is.
- **Desktop input does not reach it.** No window gets mouse or keyboard callbacks, so moving the mouse or typing while a run is on screen changes nothing. Open context menus are not closed for want of window focus (`ContextMenus.Tick` returns early during a run), so a popup test passes with another window on top.
- **Nothing appears on screen.** On Windows every window a run opens is cloaked by DWM: it is still visible to the OS, still renders and still presents, so golden images and visibility checks behave as normal, but nothing is drawn on the desktop. Windows show without activating and `Focus` does nothing during a run, so a run never takes the foreground from whatever you are working in.
- **Frames run flat out.** Idle waiting is switched off for the run.
- **Settings are the host's defaults.** A run reads and writes a `TestSettings` folder beside the host's own settings, emptied at the start of every run, so it never touches the user's settings, layout or vault list.

## Runner

```
Arm()
	read --test or --test=<Suite> from the command line, otherwise return
	fix the clock at 1/60 s
	empty TestSettings and make it the settings folder
	collect every method tagged as a Test action
	for each <Suite>.tests.axml across the data folders
		skip it unless it is the suite asked for
		queue each <Test> with its Timeout
	if a suite was asked for and none matched
		record a failure
	post the session to the first main tick

Session tick
	if Boot is still running
		count the tick
		on tick 60 record Boot from the errors logged since launch
	else if no test is running and the queue is empty
		write results.axml
		set the exit code to the number of failures
		request shutdown
	else
		if no test is running
			start the next queued test, recording any whose name resolves to nothing
		fail the test if it has run past its Timeout
		wait out the ticks it asked for
		advance it
		when it finishes, record its failed checks, its exception and the errors logged while it ran
```

## Results file

```xml
<TestRun Host="Thorium" Build="Debug" Started="2026-09-27T15:47:31.1758827+03:00">
  <Test Suite="" Name="Boot" Result="Fail" Ticks="60">
    <Failure Message="55 error(s) logged since launch" File="" Line="0" />
  </Test>
  <Test Suite="Layout" Name="Layout.StackPanelArranges" Result="Pass" Ticks="3" />
  <Test Suite="Layout" Name="Layout.StarChildCrossSize" Result="Pass" Ticks="3" />
  <Test Suite="Perf" Name="Perf.RelayoutLabels" Result="Skipped" Ticks="154" Reason="unoptimized JIT" />
</TestRun>
```

A measured test also carries `Capture="Perf.RelayoutLabels"`, the folder its frame capture was written to, relative to the run folder. `Skipped` and `New` do not count towards the exit code, and the summary line reads `N passed, M failed, K skipped, J new`. A test that took golden shots carries one `<Shot Name Result Golden Actual Diff/>` per shot: `Golden` is the full path of the PNG it was compared with, `Actual` and `Diff` are relative to the run folder and present only when written.

## Measuring performance

A test measures itself by bracketing the frames it cares about. Everything before `StartMeasure` is warm-up, and the test keeps driving its own work while the capture records:

```csharp
t.StartMeasure();
for (int i = 0; i < 120; i++)
{
    column.preferredWidth = 400f + i % 2;
    yield return 1;
}
yield return t.EndMeasure();
```

The suite gives the test its budgets, one per zone. A limit left out is not checked:

```xml
<Test Action="Perf.RelayoutLabels">
	<Budget Zone="Step.Main.Layout" Thread="Main" P95="3" Max="25" AllocKB="1"/>
</Test>
```

```
StartMeasure
	if this is a Debug build
		skip — unoptimized JIT
	else if the profiler is not compiled in
		skip — profiler not compiled in
	else
		capture every thread's frames into <run>\<test name>\

EndMeasure
	stop the capture
	hold the test until every thread has handed its frames and the files are closed

when the test ends
	if it measured
		for each budget
			find the budget's thread in the capture
			for each frame the zone ran in
				add up the zone's time and allocation in that frame
			fail on any of p50, p95, max over its limit, or the worst frame's allocation over AllocKB
			fail if the thread or the zone is not in the capture
	else if it has budgets and was not skipped
		fail — it measured nothing
```

Only a Release build with `PROFILE` defined measures anything, because the profiler compiles away in plain Release and a Debug build's numbers are not worth a budget:

```
_Build/PerfTests.cmd [Host]
```

`PerfTests.cmd` builds the solution in Release with `PROFILE` defined, runs `<Host>.exe --test=Perf` from its Release folder, and exits with the run's exit code. The host defaults to Thorium.

On Linux, `_Build/PerfTests.sh` does the same: it builds Thorium for `net10.0` in Release with `PROFILE`, then runs `--test=Perf` under `xvfb-run` with the lavapipe (CPU) Vulkan driver. Render and Draw run on the CPU there, so the other Perf budgets, which are per machine, fail on it; only the unbudgeted `Perf.ImageDecode` tests are meaningful.

The Perf suite's targets, each 30 warm-up ticks then 120 measured ticks:

| Test | Fixture | Work per tick | Budgeted zones |
|---|---|---|---|
| `Perf.RelayoutLabels` | 1,000 labels in a column | column width flips 400/401 | `Step.Main.Layout` |
| `Perf.TypeLargeNote` | 1,000 blocks of 1,000 chars in an editor | one character typed into the first block | `Step.Main.Layout`, `Step.Main.Logic`, `Document.MeasureBlocks` |
| `Perf.RewrapLargeNote` | the same note | the page is a custom size whose width narrows 1 mm a tick from 210 to 150 mm, then widens back, so every block rewraps | `Step.Main.Layout`, `Document.MeasureBlocks`, `Text.MeasureBlock` |
| `Perf.ResizeLargeNote` | the same note | the editor's width narrows 8 px a tick, then widens back; the text does not rewrap, because the paper sets its width | `Step.Main.Layout` |
| `Perf.AnimationBurst` | 5,000 buttons in rows of 100 | one 2 s tween on every button's `state`, started at the first measured tick | `Step.Animation.Step` |
| `Perf.AnimationLayoutClip` | 5,000 buttons in rows of 100 | `profile-margin` playing on every button, a relayout every tick | `Step.Animation.Step`, `Step.Main.Layout` |
| `Perf.Controls.<Name>.Static` | 1,000 of one control in rows of 25, each a star share of a 1,000-wide grid and 16 tall | nothing changes — the cost of having them on screen | `Step.Main.Input`, `Step.Main.Layout`, `Step.Main.DrawLists`, `Draw` on Render |
| `Perf.Controls.<Name>.Relayout` | the same grid | the grid's width flips 1000/1001, so every control re-lays out | the same four |
| `Perf.ImageDecode.Atlases.Arctis` / `.ImageSharp` | the 11 Thorium font atlases and the default icon atlas, cycled into 64 separate copies | one decode per tick into one reused RGBA8 buffer | none — no `<Budget>`, the zones `Image.Decode.<Arctis\|ImageSharp>.Atlas` are read from the capture |
| `Perf.ImageDecode.Formats.Arctis` / `.ImageSharp` | a 1024² crop of the arial atlas encoded as RGBA8, RGB8, Grey8 and RGBA16 under every PNG filter, RGBA8 Adam7, and for ImageSharp only JPEG and GIF | 64 decodes per row | none — zones `Image.Decode.<Arctis\|ImageSharp>.Png.<Colour>.<Filter>` |

The four `Perf.ImageDecode` tests carry no budget because one atlas decode takes 20 to 180 ms, over the 8 ms roof every budget has; they measure rather than gate. See `ClaudeMemory/Decisions/png-decode-fast-paths.md` for the numbers.

The controls measured are Panel, Button, CheckBox, Slider, Dropdown, Expander, KeyCapture, Icon, Label, TextBox, EditableLabel, a nested StackPanel, GridList, Scrollable, SplitView and TabView. `Perf.Controls.Table.*` shows 100 small tables in a note instead, and its Relayout flips the page width by 1 mm.

`Step.Animation.Step` is budgeted on Main because the scheduler runs it there while Main is free in its stage; if it moves to a worker the budget fails with the zone never having run.

Every budget's `Max` is 8 ms: no zone may take more than 8 ms in any one frame, and a spike is a failure rather than noise to allow for. `P95` is set at twice the worst p95 of three Release runs, never above 8, and `AllocKB` at twice the worst frame. The rewrap of the large note and the large note's typing spikes fail this today, as does any frame that lands on the renderer growing or shrinking its quad buffers.

The capture goes into the run's own folder, never the host's `Profiling` folder, so a test run never prunes the user's captures. `--profile-scenario` stays a separate tool for long exploratory captures.

## Viewing results in Carbon

Carbon's left column opens with a `Captures | Tests` switch. `Tests` swaps the capture list and charts for a list of run folders under `<Carbon><TestRoot Path>`, which defaults to `%APPDATA%\Arktis\Thorium\Tests`; point it at another host's `Tests` folder to see that host's runs. Carbon never starts a run — runs are launched by hand and Carbon reads the folders when it opens.

`TestResultsReader` is the reader, and it lives in the engine beside the runner that writes the file:

```
Enumerate(root)
	for each folder under root
		Load(folder) — skipped when it has no readable results.axml
	sort newest first

Load(folder)
	read results.axml
	for each <Test>
		collect its <Failure> children
		count it as passed, skipped or failed by Result
```

Clicking a run shows a heading with the counts, one `PASS`, `FAIL`, `SKIP` or `NEW` line per test with its tick count, and under a test that did not pass one indented line per failure, ending in the file and line that recorded it. A skipped test shows its reason. A failed test that measured gets an `Open capture` button, which switches to Captures and loads its capture there; pin another capture as the baseline to compare against it. A shot that failed or is new gets a strip of images under its test: the golden, the actual frame and the diff, side by side.

## Golden images

A test can check what is on screen. `yield return t.Golden("Default", control)` reads the primary window back from the swapchain - after the compositor, so it is exactly what would be presented - crops it to the control, and compares it with a PNG stored next to the suite file, in `Goldens/<Action>.<Shot>.png`. Leave the control out to take the whole window.

```
t.Golden(shot, region)
	hold the test clock
	ask the render thread for the next frame drawn after this tick
	the runner holds the test until the pixels arrive
	crop to the region's rect
	if there is no golden
		write <Shot>.actual.png into the test's folder; the shot is New
	else if every pixel matches
		the shot passes
	else
		write <Shot>.actual.png and <Shot>.diff.png; the test fails with how many pixels differ
	release the clock
```

The compare is exact. Goldens belong to the machine that approved them. A run also pins the display scale to 1, so the window and every golden are the same size whatever monitor the app opens on.

A golden nobody has looked at proves nothing, so a missing one does not pass: the test reports `New`, which is neither a pass nor a failure and stays out of the exit code. Look at the `actual.png` - in Carbon, or in the run folder - and when it is right, run again with `--test-approve`, which writes every missing or mismatched golden and marks the shot `Approved`. Approve from a Debug build: Debug reads `Data` from the source tree, and Release reads the copy in `bin`.

The clock is held while the frame is read back, because the render thread draws whatever time it finds when it gets there. With the clock still, any frame drawn after the request is the same frame. The readback itself is one extra command buffer that rides in the compositor's batch: it moves the presented image to a copy layout, copies it into a host-visible buffer, and moves it back before present. The swapchain can be read only because a test run creates it with `TransferSrc`, which a normal launch does not.
