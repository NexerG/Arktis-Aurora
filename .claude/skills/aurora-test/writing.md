# Testing Aurora from inside the engine

Two tools replace driving the app from outside:

| Need | Tool |
|---|---|
| a pass/fail answer about behaviour | a test, run with `--test` |
| a pass/fail answer about what is drawn | a test ending in `t.Golden`, run with `--test` |
| a picture of what the app draws, for you to look at | a throwaway test ending in `t.Golden("Window")` |
| state from a running app, or one action in it | a console command, sent with `--send` |

All of them run inside the engine, so none of `aurora-verify`'s synthetic-input or capture traps apply. A passing
test earns **test-verified** on `aurora-verify`'s ladder, a passing golden **golden-verified**, and a readback PNG
you looked at yourself **shot-verified**.

## Running tests

```bash
Thorium/bin/Debug/net10.0-windows10.0.22621.0/Thorium.exe --test > <scratchpad>/run.txt 2>&1; echo "exit=$?"
```

- Any working directory works. `--test=<Suite>` runs one suite. The app exits by itself; the exit code is the number
  of failed entries. Add `--test-approve` to write missing or mismatched goldens (see Golden images).
- `grep -aE '\[Test\]' run.txt` — one `PASS`/`FAIL`/`SKIP`/`NEW` line per test (`APPROVED <path>` per golden
  written), then `N passed, M failed, K skipped, J new — <results.axml>`. `SKIP` and `NEW` are not in the exit code.
  Don't read the whole log.
- `results.axml` lands in `%APPDATA%\<Host>\Tests\<yyyyMMdd-HHmmss>\`: `<Test Suite Name Result Ticks>`, with
  `<Failure Message File Line>` and `<Shot Name Result Golden Actual Diff>` children. A shot's PNGs are in
  `<run>\<Action>\`.
- **Boot** is always first: the host's own UI runs 60 ticks, and Boot fails on any `Error`/`Fatal` logged since
  launch. It fails today on pre-existing errors (WIP list). A change caused a Boot failure only if the error *kinds*
  changed — group them and compare with a run from before the change:
  `grep -aE ' (ERROR|FATAL) ' run.txt | sed -E 's/^[0-9:.]+ //' | sort | uniq -c`
- The host's own history is `%APPDATA%\<Host>\Logs\engine.log`. Use `grep -a`: the file holds binary bytes and plain
  grep stops matching partway through.
- Engine suites run in every host; a host's own suites only in that host.

A run changes the app, on purpose: the clock is fixed at 1/60 s a tick, the display scale is 1 whatever the monitor,
no window gets OS mouse or keyboard callbacks, idle waiting is off, the swapchain is created readable
(`TransferSrc`), and settings come from an emptied `%APPDATA%\<Host>\TestSettings` — the user's settings, layout and
vault list are never touched.

## Writing a test

Engine tests live in `AuroraEngine/Tests/` (`ArctisAurora.Tests`); a host's own in `<Host>/Tests/`.

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

- `t.Show(control)` makes it the primary window's whole tree and destroys the previous one.
- `yield return n` waits n ticks; two after a `Show` covers layout. Rects are design units.
- `t.Check(bool, "…")` records its file:line on failure and carries on. Word the message as the expectation.
- A test also fails on an exception (throw site reported), a timeout, or any `Error`/`Fatal` logged while it ran.
  Console `error:` answers and the runner's own lines log at `Warn`, so they never count.
- Input: `yield return t.Click(control)` / `t.Drag(control, toDesignPoint)` / `t.Key(Keys.S, Keys.LeftControl)` /
  `t.Type("text")` / `t.MoveTo(control)`. Each returns the ticks its gesture takes; when the yield returns it has
  been handled. `Click`/`MoveTo`/`Drag` fail the test if the control's centre hits something else. Keybinds are
  host data — a test needing one (`AnySymbol → Text.Write` for typing) belongs to a host that binds it.
- **A test that edits a note clears the tree with `t.Show(new StackPanelControl())` before deleting its file.**
  Anything that marks a note dirty (typing, `SetLayout`, `SetReadOnly`, `ShiftListLevel`, …) is written by
  autosave on the next tick, and an editor still in the window at exit is written again by `Notes.SaveEdited`.
  Either one recreates a temp fixture the test already deleted, leaving a stray file in `%TEMP%`. Which test
  runs last changes with the suite and with `--test=<Suite>`, so every test that edits a note clears it.
- Perf tests (`StartMeasure`/`EndMeasure`, `<Budget>`) and everything about measuring: `aurora-perf`.
- List it in a suite — one file per suite, the file name is the suite name:

```xml
<!-- AuroraEngine/Data/XML/Documents/Tests/Layout.tests.axml -->
<TestSuite xmlns="http://arctisaurora/AuroraTestTypes">
	<Test Action="Layout.StarChildCrossSize" Timeout="5000"/>
</TestSuite>
```

`Timeout` is milliseconds of test-clock time, default 10000. A suite line naming no test fails, and so does
`--test=<Suite>` naming no suite. New file → `_Build/GenerateNamespaces.cmd`.

## Golden images

```csharp
t.Show(panel);
yield return 2;
yield return t.Golden("Default", panel);   // leave the control out for the whole window
```

- `t.Golden(shot, control)` holds the test clock, reads the primary window back from the swapchain (after the
  compositor, before present), crops to the control's rect and compares **exactly** with
  `<suite file's folder>/Goldens/<Action>.<Shot>.png` — engine suites' goldens sit in `AuroraEngine/Data/…/Tests/Goldens/`.
- No golden → the test reports `NEW` and writes `<run>\<Action>\<Shot>.actual.png` only. **Read that PNG** (the Read
  tool shows images) and check it is what the fixture should draw, then run again with `--test --test-approve` from a
  **Debug** build — Debug resolves `Data` to the source tree, Release to its `bin` copy. Commit the golden PNG.
- A mismatch fails with `N px differ, max channel delta d` (or `size WxH, golden WxH`) and writes `actual.png` and
  `diff.png` — the golden greyed, differing pixels red. Read the diff before deciding the golden is stale; approve only
  a change you meant.
- Goldens belong to the machine and GPU that approved them; the scale is pinned to 1, so monitor DPI does not matter.
- Several shots in one test need distinct names. Settle first: two ticks after `Show`, and let any animation finish —
  the clock is held only while the readback is pending.
- Carbon's Tests view shows a failed or new shot's golden, actual and diff side by side.

## Looking at what the app draws

For anything a test can set up, a throwaway test is the camera — in-process, so no foreground lock, focus theft or
DPI crop to get wrong. Show the fixture (or leave the host's own tree and drive it — `CarbonActions.ShowTests()`,
a `Load(folder)`, clicks), then end with `yield return t.Golden("Window");`. The run reports `NEW`; Read
`<run>\<Action>\Window.actual.png`. Delete the throwaway test and suite afterwards. What a test cannot show — a normally
launched app, idle/wake behaviour, OS window chrome, the real display scale, secondary windows — is still
`aurora-verify`'s `capture.ps1`.

Traps:
- **Build fixtures in code.** A `*.ui.axml` loads by name through the asset registry; there is no fixture loader.
- **Idle behaviour cannot be tested.** Idle waiting is off under `--test`, so wake and idle bugs only show against a
  normally launched app.
- **A test that hangs inside one tick hangs the process** — the timeout counts ticks, not wall time.
- **A readback costs ticks against `Timeout`** — main runs unpaced while it waits: 4 ticks in Thorium.
- **A host needs a `<FrameCap>`.** Uncapped, main outruns the renderer under `--test`: readbacks and `Stopwatch`
  waits run out of ticks and time out. Thorium and Carbon set `MaxFps="120"`.
- **A throwaway golden lives twice.** The build copies `Data` into `bin/…/Data`, and a rebuild does not delete the
  copy; remove a throwaway PNG (or suite) from both, or the next run still finds it.
- **Only the primary window is read back.** A shot of a menu or torn-off tab window is not possible yet.
- **Glyphs the UI font lacks draw as blanks** (`Δ`, `—`) — keep test and failure text ASCII if Carbon should show it.
- **Changing the runner itself?** Write throwaway tests that fail each way — false check, exception, timeout, logged
  error, unknown name — run them, then delete them. A runner that passes everything is the failure to rule out.

## Poking a running app

Debug builds serve `\\.\pipe\Aurora.<Host>`. Send it a line from any shell:

```bash
Thorium/bin/Debug/net10.0-windows10.0.22621.0/Thorium.exe --send "UI.DumpTree"
```

- It prints the answers. Exit 0 all ok, 1 an `error:` came back, 2 no host answered.
- Commands are every parameterless `Input`/`UI`/`Any` action (`UI.DumpTree`, `Profiling.Capture`, `Settings.Open`,
  …) plus `Help`, `Quit` and `Wait <ms>`; `;` chains them. `Help` lists them all.
- `--exec "UI.DumpTree; Quit"` on the launch line runs after the first layout; the app then exits by itself.
- To check something live: start the app in the background, poll `--send Help` until it stops returning 2 (each try
  waits 2 s for the pipe), and end it with `--send Quit` — never kill it (`never-kill-thorium-mid-import`).
- A normal launch loads the user's vault and session and saves them back on `Quit`. Copy
  `%APPDATA%\<Host>\Settings` aside first and compare afterwards.
- **The pipe serves one line at a time.** While a line is waiting (`Wait`, or a command that hangs — the server gives
  up after 60 s), every other `--send` returns 2 as if nothing were running.
- The Ctrl+` overlay is the same console inside the window, for the user.
