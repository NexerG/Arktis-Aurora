---
name: aurora-test
description: Run Aurora's in-engine test suite and get pass/fail back with one command, `_Build/test.sh [Suite]`. Also the entry point for writing a test or suite, golden-image checks, looking at what the app draws, and driving a running app through `--send` instead of SetCursorPos, keybd_event, SendKeys or capture.ps1 — those are in this skill's writing.md.
---

# Running Aurora's tests

```bash
bash _Build/test.sh [Suite]
```

- Builds Debug, runs Thorium with `--test` (or `--test=<Suite>`) and prints only `FAIL`/`NEW`/`APPROVED` lines,
  `new error:` lines and the `N passed, M failed, K skipped, J new — <results.axml>` line.
- Runs once, at the end — not per step, not at a midpoint (CLAUDE.md §4).
- Feature work checks functionality and errors only. A bare run leaves the `Perf` suite out; perf runs only when
  measuring performance itself, through `aurora-perf` (`--test=Perf`, `_Build/PerfTests.cmd`).
- Exit code = failed entries; 255 = the build failed, its errors printed above.
- That output is the whole answer. Open the log (`%TEMP%\aurora-test\run.txt`) only when the results line is missing
  — the run died, see `aurora-debug`.
- **Boot** fails today on pre-existing errors. `new error:` is an `ERROR`/`FATAL` kind absent from
  `_Build/test-baseline.txt`; a change broke Boot only if one appears. After fixing a baseline error, or when a new
  one is intended, rerun with `--baseline`.
- A pass earns **test-verified** on `aurora-verify`'s ladder; a passing golden **golden-verified**.

## Linux (cloud container)

The same command works; `test.sh` builds `Thorium.csproj` alone and runs it under Xvfb on Mesa lavapipe. A fresh
container needs, once:

```bash
apt-get install -y dotnet-sdk-10.0 mesa-vulkan-drivers libvulkan1 libglfw3 xvfb vulkan-validationlayers libminizip1t64
```

- The log is `/tmp/aurora-test/run.txt`.
- Goldens fail on lavapipe (51 today: edges, 1–3 level flat deltas, dots at TeX spaces) — they were approved on a Windows GPU. Read only
  non-golden `FAIL` lines. A golden that is lighter all over is a colour-space bug, not lavapipe — check the
  swapchain format.
- Linux runs are test-verified for Linux only; Windows stays builds-clean until run there.

## Everything else

Read `writing.md` in this folder before writing a test or suite, reading or approving a golden (`--test-approve`),
taking a throwaway screenshot, running a host other than Thorium, or sending console commands to a running app.
