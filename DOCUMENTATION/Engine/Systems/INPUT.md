---
date:
Status: Current
tags:
  - Engine
Linker:
  - "[[Arctis Aurora]]"
System:
  - "[[INPUT]]"
Dependencies:
  - "[[GLFW]]"
  - "[[XSD / Data Layer]]"
  - "[[SETTINGS]]"
Implementors:
  - "[[InputHandler]]"
  - "[[KeyStateTracker]]"
  - "[[GestureMatcher]]"
  - "[[TextInputActions]]"
---
## System
The input system is responsible for catching hardware events from the OS (keyboard, mouse, scroll), converting them into engine-native key states, and evaluating gesture-based keybinds every engine tick. It is a passive exposure system — it catches and exposes input state, it does not route events outward to controls or systems. Other systems read from it or have their actions invoked by it.

The general hierarchy is:
`InputHandler`
	`KeyStateTracker`
	`GestureMatcher`
		`KeybindDefinitions`
			`Modifiers`
			`Conditions`

### Input Handler
The `InputHandler` is the top-level singleton that owns the entire input pipeline. It holds the `KeyStateTracker`, the `GestureMatcher`, the character input queue, the pointer table and the scroll state. Every OS input reaches it through one ordered queue, `InputHandler.Post(in RawInputEvent)`, which any platform backend calls — it is the only place raw input enters the engine. GLFW is one such backend, `GlfwInput`; there is no backend interface, because a backend is just code that calls `Post`.

Events are applied in the order they were posted, so the order between kinds is kept. A `RawInputEvent`'s `kind` (`InputEventKind`) says which of the other fields it uses:
- **Key** and **Button** → `key` (Aurora's `Keys`; mouse buttons live in the same enum as keyboard keys), `action` (`RawAction`), `timestamp` (`Engine.totalTime`) and the `window`. The `KeyStateTracker` reads only these two kinds
- **PointerMove** → `pointerId`, `pointerKind` (`PointerKind`: Mouse, Touch, Pen) and `position` in window pixels
- **Scroll** → `delta`. **ScrollContact** → `action` Down or Up, a finger touching or leaving the touchpad
- **Text** → `codepoint`, a raw Unicode codepoint — a separate kind from Key because character input is layout-aware (gives you `ä` instead of just `A`), used by text editing controls

`GlfwInput.Wire(RenderWindow)` sets the GLFW cursor-position, mouse-button, key, character, cursor-enter and scroll callbacks, and `Engine.WireInput` calls it for each window. The mapping from `Silk.NET.GLFW.Keys` and `MouseButton` to Aurora's `Keys` (`MapKey` and `MouseKey`) lives there, private, so nothing GLFW-typed reaches `InputHandler`. GLFW's Super keys map to `Keys.LeftSuper` and `RightSuper`, which the tracker counts as modifiers.

Bootstrap happens via `[A_XSDActionDependency("InputHandler.LoadInputs", "Bootstrap")]`. This calls `ParseXML` which loads all `*.inputs.axml` files from the inputs directory, constructs keybind definitions, and hands them to the `GestureMatcher`. After parsing, the default keybind group is activated.

#### Per-Tick Flow
Every engine tick, `ActivateKeybinds()` runs on the main thread. The sequence is:
1. `Drain()` — swap the posted queue under lock and apply the events in order
2. `SettleScroll()` — see Scroll gestures below
3. Swap the character queues, and copy each window's summed `scrollDeltaWrite` into `scrollDelta`
4. `keyTracker.Update()` — process the drained Key and Button events into key states, update hold durations; the drained list is then cleared
5. A pending rebind capture takes the first pressed key and ends the tick here
6. `gestureMatcher.Update()` — evaluate all active keybind definitions against current key states
7. If a modified keybind fired this frame, suppress the character input queue (prevents `Ctrl+S` from also producing an `s` character)

#### Double Buffering
The posted queue is double-buffered. A backend calls `Post` and the event lands in the write buffer under a lock. Once per tick on the main thread, `Drain` swaps the write and drain buffers under that lock, then applies the drain buffer. This means input is never lost between frames and the backend never blocks on the main thread for longer than a pointer swap. GLFW callbacks run inside `PollEvents` right before `ActivateKeybinds`, so draining first matches the old callbacks-then-settle order.

A Scroll event is applied by `ApplyScroll(RenderWindow, dx, dy)`, which holds what the wheel handler used to do.

#### Pointers
Pointer state lives in `InputHandler.PointerState`, one per pointer id: `id`, `kind`, `window`, `position`, the bit masks `down`, `pressed` and `released` indexed like `PointerEvent.leftButton` (0) and `rightButton` (1), the tap counts `taps` and `lastTapAt`, and `JustPressed(int)` and `JustReleased(int)`. `primaryPointer` is the first pointer seen and is null until the first pointer event. `UIEngine.Poll` drives the UI from it and skips buttons until it exists. A move on the primary pointer still writes `RenderWindow.mousePos`, so its readers are unchanged. Mouse buttons still also go to the key tracker, so `IsKeyDown(Keys.MouseLeft)`, a `Trigger="MouseLeft"` keybind and the title bar's tap count still work. `PointerEvent` carries `pointerId` and `kind`. Touch is not built: the agreed rule for later is that the primary touch contact drives the same press, release, tap and move, a long press arrives as a secondary-button press and release, and a one-finger pan over a scroller becomes a scroll with an end. See ClaudeMemory note input-platform-layer.

#### Wheel zoom
Holding the `Zoom` role (Control, Cmd on a Mac) turns the wheel into zoom and the scroll is not delivered. Control plus the `AppZoom` role (Shift) zooms the whole UI. Control alone zooms the content of whichever editor is under the pointer, a note, a sheet or the planner (Board, Gantt and Calendar), through the one shared document zoom setting; over a Tex preview that has its own zoom picked it steps that preview zoom instead. Off any editor, Control alone does nothing. A touchpad pinch arrives as Control plus wheel, so it zooms the same way.

#### Scroll gestures
GLFW reports scroll deltas but not where a gesture starts or ends, so `InputHandler` tracks the gesture itself. A mouse wheel gesture ends after 0.15 seconds without a delta. On a precision touchpad on Windows, `TouchpadInput` registers for Raw Input from the HID touchpad device and reads the tip switch of each finger from `WM_INPUT`, which the window procedure hands over before forwarding it to GLFW. `TouchpadInput` posts a `ScrollContact` event (Down or Up) whenever the contact changes, and `InputHandler` takes the contact state from those events when it drains them instead of reading statics from the Win32 source. A gesture with a finger down is marked touched. After the last finger lifts, Windows keeps sending momentum deltas that fade out. The gesture ends when a delta falls below a small floor or 50 milliseconds pass with none, and the rest of the momentum tail is swallowed. Touching the pad again stops the momentum, and the next lift ends the gesture.

`SettleScroll()` runs first in `ActivateKeybinds` and sets `scrollEnded` for the one tick in which a gesture finished. `UIEngine` remembers the control that consumed the scroll and calls its `OnPointerScrollEnd()` once then. The planner calendar uses it to settle onto a whole day or week. Other platforms and non-precision touchpads fall back to the wheel's quiet period. See ClaudeMemory note scroll-gesture-end for the measurements and rejected alternatives.

#### Window Focus
GLFW's window focus callback is wired per window in `Engine.WireInput` and lands in `RenderWindow.FocusChanged`, which records the window in `UICollisionHandling.pendingActiveGlfwWindow` and does nothing else. `Engine.MainTick` publishes it into the `ActiveGLFWWindow` context after the window reap and before the UI loop, so no control callback ever runs inside `glfwPollEvents` — the same defer-to-the-tick pattern as the input queues above.

The context is a **latch**: it moves on focus gained and ignores focus lost, because alt-tabbing to another application reports lost focus on every window and would otherwise clear it. A window only enters the context if `RenderWindow.isActivable` is set — `Engine.InitWindowing` and `Engine.OpenWindow` set it, so the primary window and torn-off tab windows qualify, while context menus and confirmation prompts opened through `OpenMenuWindow` deliberately do not and leave the context on the window they act on. The drag preview window is excluded outright, since `OpenGhostWindow` wires no callbacks at all.

Focus does not move `activeControl`. The two are independent: a menu window taking OS focus must not take the active control with it.

### KeyStateTracker
The `KeyStateTracker` maintains per-key state for every key the engine has seen. It stores a `Dictionary<Keys, KeyStateEntry>` where each entry tracks:
- `isDown` — whether the key is currently held
- `justPressed` / `justReleased` — per-frame edge flags, reset every tick before processing new events
- `downTimestamp` / `upTimestamp` — exact time of the last press/release (using `Engine.totalTime`)
- `holdDuration` — how long the key has been continuously held, incremented by `deltaTime` each tick
- `tapCount` — how many consecutive presses have happened within the `tapWindow`. Decays to 0 when the window expires. The window is not a field on the tracker — it is `Input.DoubleClick.Timeout` from [[SETTINGS]], read at the point of use
- `consumed` — set by the `GestureMatcher` when a keybind fires for this trigger, used to prevent less-specific binds from also firing and to suppress character input

The tracker is purely a state machine. It doesn't know about keybinds. It converts raw `Down`/`Up` events into rich per-key state that conditions and matchers can query.

#### Update Sequence
`Update(events, currentTime, deltaTime)`, where `events` is the list `InputHandler.Drain` produced:
1. Take the event list (the tracker has no queue or lock of its own)
2. Reset per-frame flags (`justPressed`, `justReleased`, `consumed`) on all existing entries
3. Process each `RawInputEvent` in order:
   - **Down**: if not already down → set `justPressed`, `isDown`, record `downTimestamp`, update tap count (increment if within `tapWindow`, otherwise reset to 1)
   - **Up**: if currently down → set `justReleased`, clear `isDown`, record `upTimestamp`
4. Move on once every event is processed
5. Update hold durations for all keys that are currently down
6. Decay tap counts — if a key is up and `tapWindow` has elapsed since last tap, reset `tapCount` to 0

### Keybind Definitions
A `KeybindDefinition` represents a single input binding. It has:
- **Trigger** — the primary `Keys` value that initiates evaluation (e.g. `S`, `MouseLeft`, `F5`)
- **Action** — the `Action` delegate to invoke when the keybind fires. Resolved at XML parse time via `[A_XSDActionDependency]` methods from `EngineTypes.Actions`
- **Modifiers** — a list of `KeybindModifier`, each specifying a `Keys` value that must be held simultaneously (e.g. `LeftControl`). All modifiers must be down for the keybind to be considered
- **Conditions** — a list of `InputCondition` objects that define *when* relative to the trigger key's state the action should fire. If no conditions are specified, the default behavior is `Press` (fire on key down)

Mouse and keyboard share the `Keys` enum. Mouse buttons are `MouseLeft`, `MouseRight`, `MouseMiddle`, `MouseButton4`–`MouseButton8`. This means keybinds can freely mix mouse and keyboard — a `Trigger="MouseLeft"` with `Modifier Key="LeftShift"` works exactly like any keyboard combo.

Mouse *gestures* against controls are a separate path and do not go through keybinds at all: `UIEngine.Poll` reads the primary pointer's press, release and tap counts each tick instead of the tracker's mouse-button states, and hands them on to `UICollisionHandling`, which resolves hover, press, release, drag and scroll against the control tree. A control claims the held-button drag by calling `StartDrag()` from its click handler, and `ResolveDrag` then reaches it every tick until the button comes up. It is opt-in from a click rather than automatic on press because the control the mouse actually hits is the deepest one — a glyph, inside a document — and what wants the drag is whatever above it knows what dragging means.

The GLFW-to-Aurora key mapping is not part of `KeybindDefinition` any more: `MapKey()` for keyboard and `MouseKey()` for mouse buttons are private to `GlfwInput`. They are exhaustive switch expressions that translate from `Silk.NET.GLFW.Keys` / `Silk.NET.GLFW.MouseButton` to the engine's `Keys` enum. Unknown keys map to `Keys.unknown`.

### Conditions
Conditions are the composable building blocks that define gesture behavior. They are `InputCondition` subclasses, each with an `Evaluate()` method that returns one of four states:
- `Idle` — not relevant, nothing happening
- `Ongoing` — in progress but not ready to fire (e.g. hold timer counting up)
- `Triggered` — fire now
- `Canceled` — was ongoing but failed (e.g. released too early for a hold)

A keybind can have multiple conditions. They are AND-combined: all must return `Triggered` in the same tick for the action to fire. If any returns `Canceled`, all conditions reset.

Current condition types:

| Condition | Fires when | XML Attributes |
|-----------|-----------|----------------|
| `Press` | The frame the key goes down | — |
| `Release` | The frame the key goes up | — |
| `Hold` | Once after holding for threshold | `Threshold` (default 0.3s) |
| `HoldRelease` | On release, only if held longer than threshold | `Threshold` (default 0.3s) |
| `MaxHoldTime` | On release, only if held *less* than threshold | `Threshold` (default 0.5s) |
| `MultiTap` | On Nth consecutive press within tap window | `Count` (default 2) |
| `Continuous` | Every tick while held | — |
| `Repeat` | On press, then repeats after the global delay at the global rate | — (global, see below) |
| `Toggle` | Alternates active/inactive state on each press | — |
| `HoldContinuous` | Every tick, but only after holding for threshold | `Threshold` (default 0.3s) |
| `Chord` | Requires another specific key to be held | `Key` |

Conditions are stateful — `Repeat` tracks its accumulator internally, `Hold` tracks whether it has already fired, `Toggle` tracks its on/off state. They get reset when the keybind fires or when evaluation is canceled, and `Repeat` is the one that does not fully comply: a fire is not the end of a hold, so it keeps the flag saying the initial delay is already served and clears only its accumulator. Resetting that flag on every fire would make each repeat wait the *delay* rather than the *rate*, and the press and release branches of its own evaluation already own the hold's real boundaries.

The `deltaTime` all of this counts in is wall time between main-thread ticks, measured tick-start to tick-start in `MainSystem`. It is deliberately **not** `ThreadedSystem.LastTickMs`, which is sampled before the pacing sleep and therefore measures the work a tick did rather than the time that passed — at the main thread's 120Hz target with a cheap tick, an order of magnitude short, which is enough to push the repeat delay out past five real seconds.

All condition types are tagged with `[A_XSDType]` and their parameters with `[A_XSDElementProperty]`, so they appear in the generated XSD schemas and are fully declarable from XML.

#### Global timings
Two numbers are deliberately **not** per-condition: the tap window `MultiTap` counts within, and the delay and rate `Repeat` waits and fires at. They come from the `Input` settings category in [[SETTINGS]] — `<DoubleClick Timeout>` and `<KeyRepeat Delay Rate>` — and a keybind has no way to override them. These describe the person at the keyboard rather than the gesture, so a settings screen owns them, and letting a keybind name its own left a settings screen with no answer for what it should do to that keybind. Everything a condition still carries — `Threshold`, `Count`, `Key` — describes what the gesture *is* and stays where it is.

They are read where they are used rather than copied into the tracker and the conditions when the map is parsed, so moving one takes effect on the next tick with nothing to re-seed.

### GestureMatcher
The `GestureMatcher` owns the evaluation loop. It holds all keybind definitions organized by named groups and references the `KeyStateTracker` for key state queries.

#### Groups
Keybind definitions are organized into groups. Each XML file in the inputs directory becomes a group named after the file's name half (e.g. `default.inputs.axml` → group `"default"`), read through `Paths.DocName` — see [[Paths]] on why that is not `Path.GetFileNameWithoutExtension`. Only one group is active at a time. Swap with `SetActiveGroup(string)` — this is how you switch keybind contexts for different game modes or editor states. When a group is set, the matcher's `_activeBinds` list is pointed at that group's keybind list directly.

#### Evaluation Loop
`Update(deltaTime)` runs once per tick after `KeyStateTracker.Update()`:

The loop runs in two passes to handle priority:
- **Pass 0** — only keybinds that have modifiers (Ctrl+S, Shift+Click, etc.)
- **Pass 1** — only keybinds without modifiers (bare key presses)

This ensures modified binds always evaluate first. Within each pass:

1. Skip if this trigger key was already consumed by a more specific (more modifiers) bind — checked via `IsShadowed()` which verifies whether a superset bind exists and all its modifiers are currently held
2. Look up the trigger key's state from the tracker. Skip if it doesn't exist (key was never pressed)
3. Check all modifiers are held. Skip if any are not
4. Evaluate all conditions via `EvaluateConditions()` — AND-combination logic as described above
5. If `Triggered`: invoke the action delegate, mark the trigger key as `consumed`, track it in the consumed set

The shadowing system prevents `S` from firing when `Ctrl+S` fires in the same frame. A bind is shadowed if there exists another bind for the same trigger key that has strictly more modifiers, all of the simpler bind's modifiers are a subset of the complex bind's modifiers, and all of the complex bind's modifiers are currently held.

#### Character Input Suppression
After the evaluation loop, `ShouldSuppressCharInput()` checks whether any modified keybind had its trigger consumed this frame. If so, the character input queue is cleared — this prevents `Ctrl+S` from also producing an `s` character in any listening text control.

### Text editing actions
The actions a keybind can name are ordinary `[A_XSDActionDependency]` statics and can live anywhere, but the ones that drive text editing live in the engine, in `TextInputActions` — an application declares which keys reach them in its `KeybindMap` and writes no input code of its own. They were briefly the host's, in `Thorium`, and the move is what makes the character drain and caret movement the same code for every application rather than something each one reimplements against `UICollisionHandling`.

| Action | Does |
|--------|------|
| `Text.Write` | Drains the whole character queue into the focused `TextControl`, replacing the selection if there is one. Drains rather than takes one, because the OS repeat rate can outpace the frame rate |
| `Text.Backspace` / `Text.Delete` | The selection, or the character the caret would step over |
| `Text.NewBlock` | Splits the caret's block in two |
| `Text.Save` | Writes the focused note back to the file it was loaded from |
| `Text.CaretLeft` / `Text.CaretRight` | One character, crossing run and block boundaries |
| `Text.CaretUp` / `Text.CaretDown` | One visual line, keeping the caret's x |
| `Text.CaretLineStart` / `Text.CaretLineEnd` | The visual line's ends, not the paragraph's |
| `Text.CaretPageUp` / `Text.CaretPageDown` | One viewport height |

Every one of them starts from `UICollisionHandling.activeControl` — a keybind action is an `Action` with no arguments, so *what* it acts on can only come from engine state, not from the binding. `Text.Write` casts it to a `TextControl` and checks `isEditing`; the caret, edit and save actions walk up from it to the nearest `DocumentEditorControl` and do nothing when there isn't one. The caret moves and the edits themselves are resolved on the block layouts and are described in [[Rich Text Document]].

A plain `Backspace` bind coexists with any modified one: the modified bind is evaluated in the first pass and consumes the trigger, and the shadowing check then skips the unmodified one. That is also a trap — a `Ctrl+Backspace` bind would swallow the word delete, which reaches `Text.Backspace` through the plain bind with the `Word` role held, and it is why Thorium no longer binds `Ctrl+Backspace` to `ExitApplication`. Backspace, Delete and Enter are all bound with `Repeat`, so holding one makes deletions or paragraphs the way holding a letter makes letters. `Enter` also counts as a character key and therefore mirrors to `AnySymbol`, but GLFW's character callback emits nothing for it, so the `Text.Write` that fires alongside finds an empty queue and returns.

A keybind's `<Modifier>` names one key but is satisfied by either side of it: `<Modifier Key="LeftControl"/>` fires with the right Ctrl too, and the same holds for Shift, Alt, Super and Win. The matcher asks whether the key *or its twin* is down, and the shadowing check treats the two sides as the same modifier, so a bind someone rebound with the right-hand key still overrides a looser left-hand one. The price is that no bind can demand one particular side.

### Named modifiers
A `Keybind` names its modifiers per bind, which works when the modifier belongs to one action. It does not work for something like extend-a-selection, which is a *state* several unrelated paths ask about — the arrow-key actions ask, and so does the click handler, which is not a keybind at all: caret placement comes through the hit-test in `UICollisionHandling`, so there is no action for a `Modifier` element to hang off.

A `NamedModifier` binds a key to a role in the `InputModifier` enum, and engine code asks for the role rather than the key.

```xml
<NamedModifier Modifier="Extend" Key="LeftShift" />
<NamedModifier Modifier="Extend" Key="RightShift" />
```

`InputHandler.IsModifierDown(InputModifier.Extend)` is true while any key declared for the role is down, which is why left and right shift are two declarations rather than a special case — a `Keybind`'s own modifier list is AND-ed and could not express the OR. The role is an enum and not a free string so the query is typed end to end and the schema enumerates what is legal, the same reasoning the [[Settings Registry]] rework used.

Three roles exist: `Extend` (a caret move or click extends the selection), `Word` (arrows, Home/End, Backspace and Delete work a word or the whole note at a time — see [[Rich Text Document#Word by word]]) and `Copy` (a text drop copies rather than moves — see [[Rich Text Document#Dragging text]]). Thorium binds `Word` and `Copy` to both Ctrl keys; they share keys but are separate roles, so an application can move either without the other.

Roles are grouped exactly as binds are, one group per file, and `SetActiveGroup` swaps both — a keybind group is a mode, so switching to a game group rebinds `Extend` along with everything else instead of leaving an editor binding live. A role no file declares simply reads as never down: there is no engine-side fallback to shift, because a fallback is the hardcoding this exists to remove.

#### Command as Control on a Mac
One keybind serves every platform, so the maps never name one: `<Modifier Key="LeftControl"/>` is the only form. On a Mac the matcher lets Cmd stand in for Ctrl. `GestureMatcher.commandIsControl` is a public static flag that defaults to `OperatingSystem.IsMacOS()` and that tests flip. While it is set, `Held(Keys)` accepts LeftSuper for LeftControl and RightSuper for RightControl, through the private `Down(Keys)` and `internal static Command(Keys)`. `IsDown(InputModifier)` checks through `Held` too, so Cmd also fills `Word`, `Copy` and `Zoom`. The shadowing check counts the Ctrl and Cmd pair as one modifier.

It is on for Mac only because on Windows Super is the Win key, and Win+V or Win+Z would open the OS feature and fire paste or undo as well. Mapping Cmd to Control in the backend was rejected: holding Ctrl and Cmd and releasing one would make the tracker think Ctrl was released while the other is held. The cost is that on a Mac word motion is Cmd+Arrow rather than Option+Arrow, and Ctrl+key also fires Ctrl binds there.

Rebind capture (`KeyStateTracker.HeldModifiers`) records Super as Control while the flag is set and drops duplicates, so a rebind saved on a Mac works on Windows. Key labels show the Control name on a Mac, not Cmd. See ClaudeMemory note input-platform-layer for the rejected alternatives.

### XML Format
Keybinds are defined in `*.inputs.axml` files placed in the inputs directory (`Paths.XMLDOCUMENTS_INPUTS`). Each file is one group. The root element is a `KeybindMap` containing `Keybind` elements.

Structure:
```xml
<KeybindMap>
  <Keybind Trigger="S" Action="SaveDocument">
    <Modifier Key="LeftControl" />
    <Press />
  </Keybind>

  <Keybind Trigger="MouseLeft" Action="SelectItem">
    <MultiTap Count="2" />
  </Keybind>

  <Keybind Trigger="A" Action="MoveLeft">
    <Repeat />
  </Keybind>

  <Keybind Trigger="G" Action="GrabMode">
    <Hold Threshold="0.5" />
  </Keybind>
</KeybindMap>
```

- `Trigger` — the `Keys` enum value string
- `Action` — resolved by name from the `[A_XSDActionDependency]` methods in `EngineTypes.Actions`
- Child `Modifier` elements specify required held keys
- Child condition elements (`Press`, `Release`, `Hold`, `Repeat`, etc.) are parsed by type name and their XML attributes are reflected onto the condition object's properties/fields
- If no condition elements are present, `Press` is the implicit default

All keybind types, conditions, and modifiers are part of the XSD system via their `[A_XSDType]` and `[A_XSDElementProperty]` attributes. The schemas are auto-generated — do not hand-edit them.

### Keys Enum
Aurora defines its own `Keys` enum independent of GLFW. It covers:
- A–Z letters
- Num0–Num9 (number row)
- Numpad0–Numpad9 + numpad operators
- F1–F25
- Navigation (arrows, Home, End, PageUp, PageDown, Insert, Delete)
- Modifiers (LeftControl, RightControl, LeftShift, RightShift, LeftAlt, RightAlt, LeftWin, RightWin)
- Special (Space, Enter, Tab, Backspace, Escape, CapsLock, etc.)
- Symbols (Apostrophe, Comma, Minus, Period, Slash, Semicolon, etc.)
- Mouse buttons (MouseLeft, MouseRight, MouseMiddle, MouseButton4–MouseButton8)
- `AnySymbol` — a special wildcard value; intended for text input listeners
- `unknown` — fallback for unmapped keys
