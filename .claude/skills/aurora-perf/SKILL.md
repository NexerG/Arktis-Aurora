---
name: aurora-perf
description: Measure, profile and compare Aurora's performance — frame time, stutter, hitches, lag, slow layout or typing, allocation/GC per frame, before/after timings, perf budgets and regressions. Use whenever a change is about being faster or allocating less, when something feels slow, when asked to profile, capture, benchmark or optimize, when reading a *.frames.axml capture or a Carbon comparison, and before stating any millisecond or KB number.
---

# Measuring performance

The profiler is zones that time and increments that count, recorded per thread into `*.frames.axml` captures.
It is CPU only — GPU time is not captured. Every number you report comes from a capture of an **optimized**
build, read with `summarize.ps1` beside this file, and goes in a table labelled with its build.

## 1. Build for measuring — always first

```bash
dotnet build AuroraEngine/ArctisAurora.sln -c Release "-p:DefineConstants=TRACE%3BPROFILE"
```

Run the host from `Thorium/bin/Release/net10.0-windows10.0.22621.0/` (same shape for Carbon). Output goes to
`bin/Release`, so the Debug bin is untouched.

On Linux (cloud sessions included) build the host project for `net10.0` and run it under `xvfb-run` with lavapipe;
`profile.ps1` (run with `pwsh`) and `_Build/PerfTests.sh` do both, and captures land under `~/.config/Arktis/`:

```bash
dotnet build Thorium/Thorium.csproj -f net10.0 -c Release "-p:DefineConstants=TRACE%3BPROFILE" -p:EnableWindowsTargeting=true
```

- **A Debug number is not a measurement.** Debug never lets the JIT optimize — about 5× slow in the animation
  step. Every Debug table from 2026-09-19 to 09-24 was wrong for this reason (`Mistakes/profiling-unoptimized-jit.md`).
- Plain Release compiles the profiler **out**: no zones, no capture, and a perf test reports `SKIP — profiler not
  compiled in`. `PROFILE` is what keeps it.
- `-p:Optimize=true` on an up-to-date Debug tree changes nothing — the incremental build ignores properties.
- Release reads the `Data` copied into `bin`, not the source tree. Rebuild after editing data.

## Preset runs

`profile.ps1` beside this file runs a scenario `-Runs` times (default 3), copies each session to `<Out>\run<N>` so
pruning cannot take it, and summarizes each with the preset's filter. `-Build` does §1 first; `-HostApp Carbon`
switches host; `-From`/`-Top` pass through to `summarize.ps1`. An `-Out` that already holds a `runN` is refused.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/aurora-perf/profile.ps1 -Preset App -Out "<scratchpad>/app-before" -Build
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/aurora-perf/profile.ps1 -Preset Animation -Out "<scratchpad>/anim-before"
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/aurora-perf/profile.ps1 -Preset UI -Out "<scratchpad>/ui-before"
```

| Preset | Scenario | Thread | Zones |
|---|---|---|---|
| App | `--profile-scenario` | all | `Step.` — every frame-graph step, plus each thread's frame time |
| Animation | `--profile-scenario=animation` | all — `Animation.Step` is unpinned | `Anim.`, `Step.Animation.Step`, `Scenario.` phases, `#Anim.Request`, `#Anim.Stepped` |
| UI | `--profile-scenario` | Main | `Layout.`, `Text.`, `Document.`, `Editor.`, `HandleUI`, `ResolveLayout`, `Step.Main.Layout`, `Step.Main.DrawLists`, `Step.Edge.UIElements`, `#Root` |

- The typing and resize phases run tier-0 code (§5); `-From 1100` reads steady state only.
- Animation prints every worker's frame time with no zones under it — that is the wait between jobs, not work.
- Re-read a kept run with a different cut: `summarize.ps1 -Dir "<Out>\run2" -Zone Layout.,Text. -From 1100`.

## 2. Pick the tool

| Question | Tool |
|---|---|
| app / animation / UI cost over 3 runs, one command | `profile.ps1 -Preset App\|Animation\|UI` (Preset runs, above) |
| where does a scripted workload spend its time | `--profile-scenario` (typing + resize on a 1M-char note) or `--profile-scenario=animation`; quits by itself |
| what does a launch cost, bootstrap included | `--profile` / `--profile=N` — N frames **per thread**, the boot frame is one of Main's |
| what does the app cost doing something you set up by hand | `Profiling.Capture` — the next `BurstFrames` (default 300): F9 in Thorium, or on the launch line `--exec "Wait 3000; Profiling.Capture; Wait 8000; Quit"`. **Release has no `--send` pipe** (Debug only) |
| data-pool sizes per frame | add `--profile-pools` to any capture |
| a pass/fail guard that the cost stays under a limit | a perf test with `<Budget>` (§5) |
| a rough per-second reading without a capture | `<Profiling><ProfilingReport Enabled="true"/>` — one `[Profiling]` log line per zone per second |
| look at it, compare two captures by eye | Carbon — Load, **Pin as baseline**, load the second, zone table shows ms/frame and the change |

Scenarios point the settings root at a `ProfileScenario` folder, so the user's layout is safe. `--profile` and
`--exec` run a normally launched app — that loads and saves the user's vault; copy `%APPDATA%\Arktis\<Host>\Settings`
aside first. **An idle host draws no frames** (Thorium and Carbon wait on OS events), so a burst over an idle
window records almost nothing — capture while something is happening, or use a scenario or a test.
`--dump-tree` on a scenario writes `uitree-<label>.axml` at still points, for comparing two builds' layouts.

## 3. Where captures land

- `%APPDATA%\Arktis\<Host>\Profiling\<yyyyMMdd-HHmmss>\<Thread>.frames.axml` — one file per thread (Main, Render,
  `Worker N`, Physics, and `Bootstrap` for `--profile`).
- A measured test: `%APPDATA%\Arktis\<Host>\Tests\<run>\<Action>\`.
- **Only the newest 5 sessions survive** (`<ProfilingCapture Keep>`): every new session prunes the rest. Copy a
  baseline you still need into the scratchpad before capturing again.
- Format: `<Names><N I V/>` (declared as they first appear), `<Batch Seq Dropped>`, `<F I T D A>` (frame index,
  start, duration, bytes), nested `<Z N B E A>` spans (ticks relative to the frame; `Frequency` on the root),
  `<C N V>` counters, `<P N C K M>` pools. A file without `</FrameCapture>` is truncated — the process died.

## 4. Read it

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/aurora-perf/summarize.ps1 -Dir "<session>" -Thread Main -Zone Step. -Top 20
```

- Per thread: frame time p50/p95/max/mean and KB per frame; per zone: ms and calls per frame (over every frame in
  range), then p50/p95/max over the frames it ran in and the worst frame's KB — the same rules as
  `TestRunner.CheckBudgets`, so its numbers match a test's `[Test]` stats line. Counters print as `#name /frame`.
- `-Zone` takes several substrings, comma-separated (`-Zone Layout.,Text.`); a zone or counter matching any is kept.
- `-From`/`-To` take the frame index (`F I`) — cut off warm-up and tiering, or isolate a phase of a scenario.
- Header flags `dropped` (the spool fell behind, frames missing) and `TRUNCATED`.
- Never read a `.frames.axml` whole — they are megabytes. Never `grep` them for numbers either; names are ids.

Reading traps:
- **A worker's frame time is not work.** It spans the wait between jobs (p95 of 100–350 ms is normal). Read a
  worker's zones.
- **Physics has frames and no spans** — its tick is a stub.
- A zone re-entered in one frame sums, like the budget check. A typo'd zone name is a *new* zone, not an error
  in Release.
- Allocation is bytes allocated inside a zone; bytes collected and GC pauses are not recorded.
- Nothing before `Engine.Init` is captured — `XSDGenerator` never shows.

## 5. Compare honestly

- Same build configuration, same machine, same scenario, **at least 3 runs each side** — one run's `max` swings
  6 → 10 ms on an unchanged build. Report the spread, not the best run.
- Per frame, never totals: two captures rarely hold the same frame count.
- Early frames of a run are still tiering up; compare within the same frame range.
- **`--profile-scenario`'s typing and resize phases run tier-0 code** — frame p50 2.6–3.0 ms there, 0.4–0.7 ms with
  `DOTNET_TieredCompilation=0`, and every frame past ~1100 matches. They measure cold-session cost, not steady state.
  To check whether a cost is the JIT, rerun with `DOTNET_TC_QuickJitForLoops=0` or `DOTNET_TieredCompilation=0` set
  in the environment — nothing in the repo changes.
- Before/after goes in a table, labelled with the build (`Release+PROFILE`, 3 runs):

| zone | before p50 / p95 / max | after p50 / p95 / max | Δ p95 |
|---|---|---|---|

- A change "is faster" when every run's p95 moved, not when the mean of one did.

## 6. Instrument, then lock it in

A zone wraps the span; `End` takes the name so a mismatch is caught in Debug:

```csharp
Profiling.Zone.Start("Document.MeasureBlocks");
    Profiling.Zone.Increment("Block");   // counts under the innermost open zone
Profiling.Zone.End("Document.MeasureBlocks");
```

Every frame-graph step is already `Step.<Action>` (`Step.Main.Layout`, `Step.Animation.Step`); read existing names
from a capture before adding one. **Never log per frame** — that is what zones and `LastTickMs` are for.

To keep a win from regressing, write a perf test (conventions for tests in general: `aurora-test`):

```csharp
[A_XSDActionDependency("Perf.RelayoutLabels", "Test")]
private static IEnumerator<int> RelayoutLabels(TestContext t)
{
    // build the fixture, t.Show(...)
    for (int i = 0; i < 30; i++) { /* the work */ yield return 1; }   // warm-up
    t.StartMeasure();
    for (int i = 0; i < 120; i++) { /* the work */ yield return 1; }
    yield return t.EndMeasure();
}
```

```xml
<Test Action="Perf.RelayoutLabels">
	<Budget Zone="Step.Main.Layout" Thread="Main" P95="3" Max="25" AllocKB="1"/>
</Test>
```

- Budget limits are ms (`AllocKB` is the worst frame); an absent limit is unchecked. A zone that never ran fails.
- `Max` is 8 ms on every budget (user) — a spike is a failure, not headroom. `P95` is 2× the worst p95 of 3
  Release+`PROFILE` runs, capped at 8; `AllocKB` 2× the worst frame. Budgets are per machine.
- Debug and plain Release report `SKIP` and stay out of the exit code; only Release+`PROFILE` measures.
- The capture lands in `<run>\<Action>\`; summarize it like any other, or open it in Carbon from the failed test.
- `cmd //c "_Build\\PerfTests.cmd [Host]"` builds Release+`PROFILE` and runs `--test=Perf`, exit code = failures.
  The existing targets and their measured baselines: [[engine-testing]] § Perf targets.
- `CheckBudgets` reads one thread per budget — an unpinned step (`Step.Animation.Step`) is budgeted where it ran;
  check the capture's threads before choosing `Thread`.

## Claims

A performance claim names the build, the run count and the tool: "`Step.Main.Layout` p95 1.06 → 0.71 ms,
Release+PROFILE, 3 runs each, `--test=Perf`". Anything measured on Debug, on one run, or read off Carbon by eye
without the numbers is an observation, not a result — say so. `aurora-verify` holds the rest of the ladder.
