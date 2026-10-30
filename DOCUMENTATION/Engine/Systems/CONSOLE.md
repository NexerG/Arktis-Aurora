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
  - "[[CONSOLE]]"
Dependencies:
  - "[[INPUT]]"
  - "[[THREADING]]"
  - "[[TESTING]]"
  - "[[UI-ENGINE]]"
Implementors:
  - "[[CONSOLE]]"
Namespace: ArctisAurora.Core.Commands
SourceFiles: AuroraEngine/Core/Commands/*.cs, AuroraEngine/Core/UI/Controls/ConsoleControl.cs
VerifiedAgainst: 2026-09-27
---
## Overview

The console runs the engine's own actions by name. Every keybind and UI action is already a named method, so each one is also a command: typing `UI.DumpTree` does what F10 does. A handful of console-only commands take arguments and answer with text.

A line holds one or more commands separated by `;`. Each command answers `ok`, its own text, or `error: …`, and every answer is written to the log as well.

## Four ways in

- **The app's console window.** Every host is a console app, so typing a line into its window and pressing Enter runs it.
- **`--exec` on the launch line.** `Thorium.exe --exec "UI.DumpTree; Quit"` runs the line once the first frame has been laid out.
- **`--send` to a running app.** `Thorium.exe --send "UI.DumpTree"` hands the line to the Thorium that is already running, prints its answers, and exits: 0 when every command succeeded, 1 when one answered with an error, 2 when no Thorium is running. It works in Debug builds, through a named pipe only the current Windows user can open.
- **Ctrl+` inside the app.** A panel drops over the top of the window with the answers above an input line. Enter runs the line, Escape or Ctrl+` again closes it.

None of these run during `--test`, because a test run only stays repeatable if nothing from outside reaches it.

## Commands

| Command | Does |
|---|---|
| `Help` | lists every command |
| `Quit` | closes the app through the normal shutdown |
| `Wait <ms>` | pauses the rest of the line |
| `Focus` | gives the focused pane's editor the caret, so a headless `--exec` can run an action that needs a focused editor, such as `Tex.ExportPdf` |
| any keybind or UI action | runs it — `UI.DumpTree`, `Profiling.Capture`, `Settings.Open`, … |

A console-only command is a static method tagged with the `Console` category. It may take numbers, true/false, text or an enum value, and whatever string it returns becomes its answer.

```csharp
[A_XSDActionDependency("Help", "Console")]
private static string Help() => string.Join(", ", _commands.Keys.Append(waitCommand).Order(StringComparer.OrdinalIgnoreCase));
```

`Wait` works whether the app is busy or idle: it asks the frame scheduler for a frame at the moment the wait runs out, so an idle app wakes for it.

## Running a line

```
Execute(line, reply)
	for each command in the line, in order
		if it is Wait
			park the rest of the line until that much engine time has passed, and ask for a frame then
			return
		find the method by name, ignoring case
		convert each argument to the parameter's type
		run it and keep its answer: its returned text, ok, or error: …
	hand every answer to reply, one per line

Pump tick
	for each parked line whose time has come
		continue it from the command after its Wait
	stop ticking once nothing is parked
```

## The overlay

Ctrl+` attaches the console as the last child of the primary window's root, which is how an in-window context menu is hosted: the last child is drawn over everything and receives the mouse first. The input line takes the keyboard focus while the console is open and gives it up when it closes. The console keeps its last 200 lines, and the newest one is scrolled into view once it has been laid out.
