# Pattern — building without spending context

- Build through `_Build/build.sh [Project.csproj|Solution.sln] [--rebuild]`; default target `AuroraEngine/ArctisAurora.sln`
- Clean → `BUILD OK (Ns)`, exit 0. Failure → deduplicated `error` lines (max 20), `BUILD FAILED (log: …)`, build's exit code
- MSB3027/MSB3021 (running app holds a DLL) → one `LOCKED: <process (pid)>` line instead of the copy errors
- Full log stays at `$TEMP/aurora-build/build.txt`; grep it, don't read it
- Raw output sizes (Thorium, 2026-10-30): incremental 12.6 KB, `--no-incremental` 926 KB / 1,427 warnings — past the 30 KB tool cut-off
- `test.sh` and `aurora-ship` keep their own build lines
