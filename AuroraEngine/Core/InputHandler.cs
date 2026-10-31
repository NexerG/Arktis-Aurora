using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Threading;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;
using Silk.NET.Vulkan;
using System.Reflection;
using System.Xml.Linq;
using static ArctisAurora.EngineWork.KeyStateTracker;

namespace ArctisAurora.EngineWork
{
    public enum RawAction
    {
        Down,
        Up
    }

    public enum InputEventKind
    {
        Key,
        Button,
        PointerMove,
        Scroll,
        ScrollContact,
        Text
    }

    public enum PointerKind
    {
        Mouse,
        Touch,
        Pen
    }

    // One OS input, as a platform backend posts it to InputHandler.Post.
    public struct RawInputEvent
    {
        public InputEventKind kind;
        public Keys key;
        public RawAction action;
        public double timestamp;
        public RenderWindow window;

        // pointer identity, and its position in window pixels
        public int pointerId;
        public PointerKind pointerKind;
        public Vector2 position;

        // scroll offset, and a character's codepoint
        public Vector2 delta;
        public uint codepoint;
    }

    public class KeyStateEntry
    {
        public Keys key;
        public bool isDown;
        public double downTimestamp;
        public double upTimestamp;
        public double holdDuration;
        public int tapCount;
        public double lastTapTimestamp;
        public bool consumed;

        // Per-frame flags
        public bool justPressed;
        public bool justReleased;

        public void ResetFrame()
        {
            justPressed = false;
            justReleased = false;
            consumed = false;
        }
    }

    public class KeyStateTracker
    {
        private Dictionary<Keys, KeyStateEntry> _states = new Dictionary<Keys, KeyStateEntry>();

        // Global keybind timings, from the Input settings category. A keybind cannot override them.
        private static double tapWindow => SettingsRegistry.Get<InputSettings>().doubleClick.timeout;
        private static KeyRepeatSetting keyRepeat => SettingsRegistry.Get<InputSettings>().keyRepeat;

        public KeyStateEntry GetState(Keys key)
        {
            _states.TryGetValue(key, out KeyStateEntry entry);
            return entry;
        }

        public bool IsDown(Keys key)
        {
            if (_states.TryGetValue(key, out KeyStateEntry entry))
                return entry.isDown;
            return false;
        }

        public bool AnyDown
        {
            get
            {
                foreach (KeyStateEntry entry in _states.Values)
                    if (entry.isDown) return true;
                return false;
            }
        }

        public double HoldDuration(Keys key)
        {
            if (_states.TryGetValue(key, out KeyStateEntry entry) && entry.isDown)
                return entry.holdDuration;
            return 0;
        }

        // Called once per tick on main thread, with the tick's drained events.
        public void Update(List<RawInputEvent> events, double currentTime, double deltaTime)
        {
            foreach (KeyStateEntry entry in _states.Values)
                entry.ResetFrame();

            for (int i = 0; i < events.Count; i++)
            {
                RawInputEvent evt = events[i];
                if (evt.kind != InputEventKind.Key && evt.kind != InputEventKind.Button) continue;
                KeyStateEntry entry = GetOrCreate(evt.key);

                if (evt.action == RawAction.Down)
                {
                    if (!entry.isDown)
                    {
                        entry.justPressed = true;
                        entry.isDown = true;
                        entry.downTimestamp = evt.timestamp;
                        entry.holdDuration = 0;

                        if (evt.timestamp - entry.lastTapTimestamp < tapWindow)
                            entry.tapCount++;
                        else
                            entry.tapCount = 1;

                        entry.lastTapTimestamp = evt.timestamp;

                        // Mirror to AnySymbol
                        if (IsCharacterKey(evt.key))
                            MirrorToAnySymbol(evt.timestamp);
                    }
                }
                else
                {
                    if (entry.isDown)
                    {
                        entry.justReleased = true;
                        entry.isDown = false;
                        entry.upTimestamp = evt.timestamp;

                        // Release AnySymbol only if no other character keys are held
                        if (IsCharacterKey(evt.key) && !AnyCharacterKeyHeld())
                            ReleaseAnySymbol(evt.timestamp);
                    }
                }
            }

            foreach (KeyStateEntry entry in _states.Values)
            {
                if (entry.isDown)
                    entry.holdDuration += deltaTime;

                if (!entry.isDown && currentTime - entry.lastTapTimestamp > tapWindow)
                    entry.tapCount = 0;
            }
        }

        // The first non-modifier key that went down this tick, for a rebind listening for one.
        public Keys? FirstPressed()
        {
            foreach (KeyStateEntry entry in _states.Values)
                if (entry.justPressed && entry.key != Keys.AnySymbol && !IsModifier(entry.key))
                    return entry.key;
            return null;
        }

        public List<Keys> HeldModifiers()
        {
            List<Keys> held = new List<Keys>();
            foreach (KeyStateEntry entry in _states.Values)
            {
                if (!entry.isDown || !IsModifier(entry.key)) continue;
                Keys key = GestureMatcher.commandIsControl && entry.key is Keys.LeftSuper or Keys.RightSuper
                    ? GestureMatcher.Command(entry.key).Value : entry.key;
                if (!held.Contains(key)) held.Add(key);
            }
            return held;
        }

        public static bool IsModifier(Keys key) =>
            key == Keys.LeftControl || key == Keys.RightControl ||
            key == Keys.LeftShift || key == Keys.RightShift ||
            key == Keys.LeftAlt || key == Keys.RightAlt ||
            key == Keys.LeftSuper || key == Keys.RightSuper;

        private void MirrorToAnySymbol(double timestamp)
        {
            KeyStateEntry any = GetOrCreate(Keys.AnySymbol);
            any.justPressed = true;
            any.isDown = true;
            any.downTimestamp = timestamp;
            any.holdDuration = 0;

            if (timestamp - any.lastTapTimestamp < tapWindow)
                any.tapCount++;
            else
                any.tapCount = 1;

            any.lastTapTimestamp = timestamp;
        }

        private void ReleaseAnySymbol(double timestamp)
        {
            KeyStateEntry any = GetOrCreate(Keys.AnySymbol);
            any.justReleased = true;
            any.isDown = false;
            any.upTimestamp = timestamp;
        }

        private bool AnyCharacterKeyHeld()
        {
            foreach (KeyStateEntry entry in _states.Values)
            {
                if (entry.key != Keys.AnySymbol && entry.isDown && IsCharacterKey(entry.key))
                    return true;
            }
            return false;
        }

        private static bool IsCharacterKey(Keys key)
        {
            return key >= Keys.A && key <= Keys.Z ||
                   key >= Keys.Num0 && key <= Keys.Num9 ||
                   key >= Keys.Numpad0 && key <= Keys.NumpadEqual ||
                   key == Keys.Space || key == Keys.Enter || key == Keys.Tab ||
                   key == Keys.Apostrophe || key == Keys.Comma || key == Keys.Minus ||
                   key == Keys.Period || key == Keys.Slash || key == Keys.Semicolon ||
                   key == Keys.Equal || key == Keys.LeftBracket || key == Keys.Backslash ||
                   key == Keys.RightBracket || key == Keys.GraveAccent;
        }

        public enum ConditionResult
        {
            Idle,       // not relevant yet
            Ongoing,    // in progress, don't fire yet
            Triggered,  // fire now
            Canceled    // was ongoing, now failed
        }

        [A_XSDType("Condition", "Input", description: "Base input condition type")]
        public abstract class InputCondition : IKeybindChild
        {
            // Called every tick. Returns the current state of this condition.
            public abstract ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime);

            // Called when the keybind fires or resets
            public abstract void Reset();
        }

        // ──────────────────────────────────────────────
        // PRESS — fires the frame the key goes down
        // ──────────────────────────────────────────────
        [A_XSDType("Press", "Input", description: "Fires the frame the key goes down")]
        public class PressCondition : InputCondition
        {
            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (trigger.justPressed)
                    return ConditionResult.Triggered;
                return ConditionResult.Idle;
            }

            public override void Reset() { }
        }

        // ──────────────────────────────────────────────
        // RELEASE — fires the frame the key goes up
        // ──────────────────────────────────────────────
        [A_XSDType("Release", "Input", description: "Fires the frame the key goes up")]
        public class ReleaseCondition : InputCondition
        {
            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (trigger.justReleased)
                    return ConditionResult.Triggered;
                return ConditionResult.Idle;
            }

            public override void Reset() { }
        }

        // ──────────────────────────────────────────────
        // HOLD — fires once after holding for threshold
        // ──────────────────────────────────────────────
        [A_XSDType("Hold", "Input", description: "Fires once after holding for threshold seconds")]
        public class HoldCondition : InputCondition
        {
            [A_XSDElementProperty("Threshold", "Input")]
            public float threshold { get; set; } = 0.3f;

            private bool _fired;

            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (!trigger.isDown)
                {
                    if (_fired)
                    {
                        _fired = false;
                        return ConditionResult.Idle;
                    }
                    return ConditionResult.Idle;
                }

                if (_fired)
                    return ConditionResult.Idle;

                if (trigger.holdDuration >= threshold)
                {
                    _fired = true;
                    return ConditionResult.Triggered;
                }

                return ConditionResult.Ongoing;
            }

            public override void Reset() { _fired = false; }
        }

        // ──────────────────────────────────────────────
        // HOLD RELEASE — fires on release, only if held
        // longer than threshold
        // ──────────────────────────────────────────────
        [A_XSDType("HoldRelease", "Input", description: "Fires on release only if held longer than threshold")]
        public class HoldReleaseCondition : InputCondition
        {
            [A_XSDElementProperty("Threshold", "Input")]
            public float threshold { get; set; } = 0.3f;

            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (trigger.justReleased)
                {
                    double heldFor = trigger.upTimestamp - trigger.downTimestamp;
                    if (heldFor >= threshold)
                        return ConditionResult.Triggered;
                    return ConditionResult.Canceled;
                }

                if (trigger.isDown)
                    return ConditionResult.Ongoing;

                return ConditionResult.Idle;
            }

            public override void Reset() { }
        }

        // ──────────────────────────────────────────────
        // MAX HOLD TIME — passes only if key was held
        // LESS than threshold (combine with Release)
        // ──────────────────────────────────────────────
        [A_XSDType("MaxHoldTime", "Input", description: "Passes only if key was held less than threshold. Combine with Release")]
        public class MaxHoldTimeCondition : InputCondition
        {
            [A_XSDElementProperty("Threshold", "Input")]
            public float threshold { get; set; } = 0.5f;

            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (trigger.justReleased)
                {
                    double heldFor = trigger.upTimestamp - trigger.downTimestamp;
                    if (heldFor < threshold)
                        return ConditionResult.Triggered;
                    return ConditionResult.Canceled;
                }

                if (trigger.isDown)
                {
                    if (trigger.holdDuration >= threshold)
                        return ConditionResult.Canceled;
                    return ConditionResult.Ongoing;
                }

                return ConditionResult.Idle;
            }

            public override void Reset() { }
        }

        // ──────────────────────────────────────────────
        // MULTI TAP — fires on Nth consecutive press
        // within tap window
        // ──────────────────────────────────────────────
        [A_XSDType("MultiTap", "Input", description: "Fires on Nth consecutive press within tap window")]
        public class MultiTapCondition : InputCondition
        {
            [A_XSDElementProperty("Count", "Input")]
            public int count { get; set; } = 2;

            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (trigger.justPressed)
                {
                    if (trigger.tapCount >= count)
                    {
                        trigger.tapCount = 0;
                        return ConditionResult.Triggered;
                    }
                    return ConditionResult.Ongoing;
                }

                if (trigger.tapCount > 0 && trigger.tapCount < count)
                    return ConditionResult.Ongoing;

                return ConditionResult.Idle;
            }

            public override void Reset() { }
        }

        // ──────────────────────────────────────────────
        // CONTINUOUS — fires every tick while held
        // ──────────────────────────────────────────────
        [A_XSDType("Continuous", "Input", description: "Fires every tick while held")]
        public class ContinuousCondition : InputCondition
        {
            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (trigger.isDown)
                    return ConditionResult.Triggered;
                return ConditionResult.Idle;
            }

            public override void Reset() { }
        }

        // ──────────────────────────────────────────────
        // REPEAT — fires on press, then repeats after
        // delay at rate
        // ──────────────────────────────────────────────
        [A_XSDType("Repeat", "Input", description: "Fires on press then repeats after the global delay at the global rate")]
        public class RepeatCondition : InputCondition
        {
            private double _accumulator;
            private bool _delayPassed;

            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (trigger.justPressed)
                {
                    _accumulator = 0;
                    _delayPassed = false;
                    return ConditionResult.Triggered;
                }

                if (!trigger.isDown)
                {
                    _accumulator = 0;
                    _delayPassed = false;
                    return ConditionResult.Idle;
                }

                _accumulator += deltaTime;

                if (!_delayPassed)
                {
                    if (_accumulator >= keyRepeat.delay)
                    {
                        _delayPassed = true;
                        _accumulator = 0;
                        return ConditionResult.Triggered;
                    }
                    return ConditionResult.Ongoing;
                }
                else
                {
                    if (_accumulator >= keyRepeat.rate)
                    {
                        _accumulator = 0;
                        return ConditionResult.Triggered;
                    }
                    return ConditionResult.Ongoing;
                }
            }

            // Not _delayPassed: EvaluateConditions resets after every fire, and a fire is not the
            // end of the hold — clearing it makes every repeat serve the initial delay again. The
            // press and release branches above own the hold's real boundaries.
            public override void Reset()
            {
                _accumulator = 0;
            }
        }

        // ──────────────────────────────────────────────
        // TOGGLE — alternates active/inactive on press
        // ──────────────────────────────────────────────
        [A_XSDType("Toggle", "Input", description: "Alternates active/inactive on each press")]
        public class ToggleCondition : InputCondition
        {
            public bool isActive;

            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (trigger.justPressed)
                {
                    isActive = !isActive;
                    return ConditionResult.Triggered;
                }
                return ConditionResult.Idle;
            }

            public override void Reset() { isActive = false; }
        }

        // ──────────────────────────────────────────────
        // HOLD CONTINUOUS — fires every tick, but only
        // after holding for threshold
        // ──────────────────────────────────────────────
        [A_XSDType("HoldContinuous", "Input", description: "Fires every tick but only after holding for threshold")]
        public class HoldContinuousCondition : InputCondition
        {
            [A_XSDElementProperty("Threshold", "Input")]
            public float threshold { get; set; } = 0.3f;

            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (!trigger.isDown)
                    return ConditionResult.Idle;

                if (trigger.holdDuration >= threshold)
                    return ConditionResult.Triggered;

                return ConditionResult.Ongoing;
            }

            public override void Reset() { }
        }

        // ──────────────────────────────────────────────
        // CHORD — requires another key to be held
        // (alternative to modifier list, usable as condition)
        // ──────────────────────────────────────────────
        [A_XSDType("Chord", "Input", description: "Requires another key to be held simultaneously")]
        public class ChordCondition : InputCondition
        {
            [A_XSDElementProperty("Key", "Input")]
            public Keys key { get; set; }

            public override ConditionResult Evaluate(KeyStateEntry trigger, KeyStateTracker tracker, double deltaTime)
            {
                if (tracker.IsDown(key))
                    return ConditionResult.Triggered;
                return ConditionResult.Canceled;
            }

            public override void Reset() { }
        }

        private KeyStateEntry GetOrCreate(Keys key)
        {
            if (!_states.TryGetValue(key, out KeyStateEntry entry))
            {
                entry = new KeyStateEntry();
                entry.key = key;
                _states.Add(key, entry);
            }
            return entry;
        }
    }

    [A_XSDType("KeybindModifier", "Input", description: "Key that must be held for the keybind to activate")]
    public class KeybindModifier : IKeybindChild
    {
        [A_XSDElementProperty("Key", "Input")]
        public Keys key { get; set; }
    }

    // A role the engine asks about by name, so code never names the key that fills it.
    [A_XSDType("InputModifier", "Input")]
    public enum InputModifier
    {
        Extend,
        Word,
        Copy,
        Zoom,
        AppZoom,
        Follow
    }

    [A_XSDType("NamedModifier", "Input", description: "Binds a key to a modifier role the engine queries by name")]
    public class NamedModifier : IKeybindMapChild
    {
        [A_XSDElementProperty("Modifier", "Input")]
        public InputModifier modifier { get; set; }

        [A_XSDElementProperty("Key", "Input")]
        public Keys key { get; set; }
    }

    [A_XSDType("KeybindAccess", "Input", description: "Whether a bind may be rebound at runtime")]
    public enum KeybindAccess
    {
        Open, Locked
    }

    [A_XSDType("Keybind", "Input", AllowedChildren = typeof(IKeybindChild), Description = "Maps a trigger key with optional modifiers and conditions to an action")]
    public class KeybindDefinition : IKeybindMapChild
    {
        [A_XSDElementProperty("Trigger", "Input")]
        public Keys trigger { get; set; }

        [A_XSDElementProperty("Action", "Input")]
        public Action? action { get; set; }

        [A_XSDElementProperty("Access", "Input")]
        public KeybindAccess access { get; set; } = KeybindAccess.Open;

        // what Action named, kept so a rebind can find the bind the delegate came from
        public string actionName = "";

        [A_XSDElementProperty("Modifier", "Input")]
        public List<KeybindModifier> modifiers = new List<KeybindModifier>();

        // Conditions are child elements — parsed by RecursiveParse
        public List<InputCondition> conditions = new List<InputCondition>();

        // If no conditions specified, default to Press
        public bool hasConditions => conditions.Count > 0;
    }

    public class GestureMatcher
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("Input");

        private KeyStateTracker _tracker;
        private string _activeGroup = "default";
        private Dictionary<string, List<KeybindDefinition>> _groups = new Dictionary<string, List<KeybindDefinition>>();
        private List<KeybindDefinition> _activeBinds = new List<KeybindDefinition>();

        // modifier roles, grouped the same way the binds are
        private Dictionary<string, List<NamedModifier>> _modifierGroups = new Dictionary<string, List<NamedModifier>>();
        private List<NamedModifier> _activeModifiers = new List<NamedModifier>();

        // Default condition when none specified
        private static PressCondition _defaultPress = new PressCondition();

        public GestureMatcher(KeyStateTracker tracker)
        {
            _tracker = tracker;
        }

        public void SetActiveGroup(string group)
        {
            _activeGroup = group;
            if (_groups.TryGetValue(group, out List<KeybindDefinition> binds))
                _activeBinds = binds;
            else
                _activeBinds = new List<KeybindDefinition>();

            if (_modifierGroups.TryGetValue(group, out List<NamedModifier> modifiers))
                _activeModifiers = modifiers;
            else
                _activeModifiers = new List<NamedModifier>();
        }

        public void AddKeybind(string group, KeybindDefinition def)
        {
            if (!_groups.TryGetValue(group, out List<KeybindDefinition> list))
            {
                list = new List<KeybindDefinition>();
                _groups.Add(group, list);
            }
            list.Add(def);

            if (group == _activeGroup)
                _activeBinds = list;
        }

        public IReadOnlyList<KeybindDefinition> ActiveBinds => _activeBinds;

        // Moves an action's bind onto another trigger, in whatever group holds it — a stored override
        // is applied before the host has chosen its group. A Locked bind is the build's, so it refuses.
        public bool Rebind(string actionName, Keys trigger, List<KeybindModifier> modifiers)
        {
            bool moved = false;

            foreach (List<KeybindDefinition> binds in _groups.Values)
                for (int i = 0; i < binds.Count; i++)
                {
                    KeybindDefinition def = binds[i];
                    if (!string.Equals(def.actionName, actionName, StringComparison.OrdinalIgnoreCase)) continue;

                    if (def.access == KeybindAccess.Locked)
                    {
                        Log.Warn($"'{actionName}' is locked — not rebinding.");
                        return false;
                    }

                    def.trigger = trigger;
                    def.modifiers = modifiers;
                    moved = true;
                }

            if (!moved) Log.Warn($"no bind for '{actionName}' — not rebinding.");
            return moved;
        }

        public void AddModifier(string group, NamedModifier modifier)
        {
            if (!_modifierGroups.TryGetValue(group, out List<NamedModifier> list))
            {
                list = new List<NamedModifier>();
                _modifierGroups.Add(group, list);
            }
            list.Add(modifier);

            if (group == _activeGroup)
                _activeModifiers = list;
        }

        // Any one key bound to the role satisfies it, so left and right shift are two declarations.
        public bool IsDown(InputModifier modifier)
        {
            for (int i = 0; i < _activeModifiers.Count; i++)
                if (_activeModifiers[i].modifier == modifier && Held(_activeModifiers[i].key))
                    return true;

            return false;
        }

        public void Update(double deltaTime)
        {
            // Track which trigger keys have been consumed this frame
            // to prevent less-specific binds from also firing
            HashSet<Keys> consumedTriggers = null;

            // Process in order of specificity (most modifiers first)
            // Sort once when group changes, not every frame — but for correctness here:
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < _activeBinds.Count; i++)
                {
                    KeybindDefinition def = _activeBinds[i];

                    // Pass 0: only process binds with modifiers
                    // Pass 1: only process binds without modifiers
                    bool hasModifiers = def.modifiers.Count > 0;
                    if (pass == 0 && !hasModifiers) continue;
                    if (pass == 1 && hasModifiers) continue;

                    // Skip if a more specific bind already consumed this trigger
                    if (consumedTriggers != null && consumedTriggers.Contains(def.trigger))
                    {
                        // But only skip if this bind has fewer modifiers
                        // (same modifier count = different combo, allow it)
                        if (IsShadowed(def))
                            continue;
                    }

                    // Check trigger key exists in tracker
                    KeyStateEntry triggerState = _tracker.GetState(def.trigger);
                    if (triggerState == null) continue;

                    // Check all modifiers are held
                    bool modsHeld = true;
                    for (int m = 0; m < def.modifiers.Count; m++)
                    {
                        if (!Held(def.modifiers[m].key))
                        {
                            modsHeld = false;
                            break;
                        }
                    }
                    if (!modsHeld) continue;

                    // Evaluate conditions
                    ConditionResult result = EvaluateConditions(def, triggerState, deltaTime);

                    if (result == ConditionResult.Triggered)
                    {
                        InputHandler.firingRepeat = !triggerState.justPressed;
                        def.action?.Invoke();
                        InputHandler.firingRepeat = false;
                        triggerState.consumed = true;

                        if (consumedTriggers == null)
                            consumedTriggers = new HashSet<Keys>();
                        consumedTriggers.Add(def.trigger);
                    }
                }
            }
        }

        private ConditionResult EvaluateConditions(KeybindDefinition def, KeyStateEntry trigger, double deltaTime)
        {
            // No conditions = default Press behavior
            if (!def.hasConditions)
            {
                return _defaultPress.Evaluate(trigger, _tracker, deltaTime);
            }

            // ALL conditions must agree.
            // Triggered requires ALL to be Triggered.
            // If ANY is Canceled, result is Canceled.
            // If ANY is Ongoing (and none Canceled), result is Ongoing.
            // Otherwise Idle.

            bool allTriggered = true;
            bool anyCanceled = false;
            bool anyOngoing = false;

            for (int c = 0; c < def.conditions.Count; c++)
            {
                ConditionResult cr = def.conditions[c].Evaluate(trigger, _tracker, deltaTime);

                if (cr == ConditionResult.Canceled)
                {
                    anyCanceled = true;
                    allTriggered = false;
                }
                else if (cr == ConditionResult.Ongoing)
                {
                    anyOngoing = true;
                    allTriggered = false;
                }
                else if (cr == ConditionResult.Idle)
                {
                    allTriggered = false;
                }
            }

            if (anyCanceled)
            {
                // Reset all conditions when canceled
                for (int c = 0; c < def.conditions.Count; c++)
                    def.conditions[c].Reset();
                return ConditionResult.Canceled;
            }

            if (allTriggered)
            {
                // Reset after firing
                for (int c = 0; c < def.conditions.Count; c++)
                    def.conditions[c].Reset();
                return ConditionResult.Triggered;
            }

            if (anyOngoing)
                return ConditionResult.Ongoing;

            return ConditionResult.Idle;
        }

        private bool IsShadowed(KeybindDefinition def)
        {
            for (int i = 0; i < _activeBinds.Count; i++)
            {
                KeybindDefinition other = _activeBinds[i];
                if (other == def) continue;
                if (other.trigger != def.trigger) continue;
                if (other.modifiers.Count <= def.modifiers.Count) continue;

                // Check other's modifiers are a superset
                bool isSuperset = true;
                for (int m = 0; m < def.modifiers.Count; m++)
                {
                    bool found = false;
                    for (int n = 0; n < other.modifiers.Count; n++)
                    {
                        if (SameModifier(other.modifiers[n].key, def.modifiers[m].key))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found) { isSuperset = false; break; }
                }
                if (!isSuperset) continue;

                // Check all of other's modifiers are currently held
                bool allHeld = true;
                for (int m = 0; m < other.modifiers.Count; m++)
                {
                    if (!Held(other.modifiers[m].key))
                    {
                        allHeld = false;
                        break;
                    }
                }
                if (allHeld) return true;
            }
            return false;
        }

        // Set on a Mac, where Cmd fills a Ctrl modifier.
        public static bool commandIsControl = OperatingSystem.IsMacOS();

        // A keybind modifier is held when either side of it is, or on a Mac its Cmd counterpart.
        private bool Held(Keys key) => Down(key) || (commandIsControl && Command(key) is Keys cmd && Down(cmd));

        private bool Down(Keys key) => _tracker.IsDown(key) || (Twin(key) is Keys twin && _tracker.IsDown(twin));

        private static bool SameModifier(Keys a, Keys b) =>
            a == b || Twin(a) == b || (commandIsControl && Command(a) is Keys cmd && (cmd == b || Twin(cmd) == b));

        // The Cmd key for a Ctrl key, and the reverse.
        internal static Keys? Command(Keys key) => key switch
        {
            Keys.LeftControl => Keys.LeftSuper,
            Keys.RightControl => Keys.RightSuper,
            Keys.LeftSuper => Keys.LeftControl,
            Keys.RightSuper => Keys.RightControl,
            _ => null
        };

        private static Keys? Twin(Keys key) => key switch
        {
            Keys.LeftControl => Keys.RightControl,
            Keys.RightControl => Keys.LeftControl,
            Keys.LeftShift => Keys.RightShift,
            Keys.RightShift => Keys.LeftShift,
            Keys.LeftAlt => Keys.RightAlt,
            Keys.RightAlt => Keys.LeftAlt,
            Keys.LeftSuper => Keys.RightSuper,
            Keys.RightSuper => Keys.LeftSuper,
            Keys.LeftWin => Keys.RightWin,
            Keys.RightWin => Keys.LeftWin,
            _ => null
        };

        // Suppress char input when a modified keybind fired this frame
        public bool ShouldSuppressCharInput()
        {
            for (int i = 0; i < _activeBinds.Count; i++)
            {
                KeybindDefinition def = _activeBinds[i];
                if (def.modifiers.Count == 0) continue;

                KeyStateEntry state = _tracker.GetState(def.trigger);
                if (state != null && state.consumed)
                    return true;
            }
            return false;
        }
    }
    // -------------------------------

    [A_XSDType("Keys", "Input")]
    public enum Keys
    {
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        //a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s, t, u, v, w, x, y, z,
        Apostrophe, Comma, Minus, Period, Slash, Semicolon, Equal, LeftBracket, Backslash, RightBracket, GraveAccent,
        Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9, NumpadDecimal, NumpadDivide, NumpadMultiply, NumpadSubtract, NumpadAdd, NumpadEnter, NumpadEqual,
        Num0, Num1, Num2, Num3, Num4, Num5, Num6, Num7, Num8, Num9,
        Space, Enter, Tab,
        AnySymbol,
        Backspace, Escape, LeftControl, RightControl, LeftShift, RightShift, LeftWin, RightWin, Menu, CapsLock, ScrollLock, NumLock, PrintScreen, Pause,
        LeftAlt, RightAlt, LeftSuper, RightSuper,
        Up, Down, Left, Right, Home, End, PageUp, PageDown, Insert, Delete,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24, F25,
        MouseLeft, MouseRight, MouseMiddle, MouseButton4, MouseButton5, MouseButton6, MouseButton7, MouseButton8,
        unknown,
    }

    public interface IKeybindChild { }

    public interface IKeybindMapChild { }


    [A_XSDType("KeybindMap", "Input", AllowedChildren = typeof(IKeybindMapChild), Description = "Root container for keybind definitions")]
    public unsafe class InputHandler : IXMLParser<InputHandler>
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("Input");

        public static InputHandler instance { get; set; } = null!;

        // Set while a keybind's action runs from a held key's repeat rather than its press.
        public static bool firingRepeat { get; internal set; }

        public KeyStateTracker keyTracker = new KeyStateTracker();
        public GestureMatcher gestureMatcher;

        public static char lastCharInput = '\0';
        public static Queue<char> charInputWriteQueue = new Queue<char>();
        public static Queue<char> charInputReadQueue = new Queue<char>();

        [A_XSDElementProperty("Keybind", "Input")]
        public List<KeybindDefinition> keybindDefinitions = new List<KeybindDefinition>();

        [A_XSDElementProperty("NamedModifier", "Input")]
        public List<NamedModifier> namedModifiers = new List<NamedModifier>();

        // what the next key press goes to instead of the keybinds
        private static Action<Keys, List<Keys>>? _capture;

        public static bool isCapturing => _capture != null;

        public static void Capture(Action<Keys, List<Keys>> onCaptured) => _capture = onCaptured;

        public static void CancelCapture() => _capture = null;

        public bool IsKeyDown(Keys k) => keyTracker.IsDown(k);

        public bool IsModifierDown(InputModifier modifier) => gestureMatcher.IsDown(modifier);

        public InputHandler()
        {
            gestureMatcher = new GestureMatcher(keyTracker);
        }

        #region ---- event ingress ----
        private static List<RawInputEvent> _posted = new List<RawInputEvent>();
        private static List<RawInputEvent> _draining = new List<RawInputEvent>();
        private static readonly object _postLock = new object();

        // The one entry for OS input; any platform backend posts here, from any thread.
        public static void Post(in RawInputEvent evt)
        {
            lock (_postLock)
                _posted.Add(evt);
        }

        // Applies the tick's posted events in the order they arrived.
        private void Drain()
        {
            lock (_postLock)
                (_posted, _draining) = (_draining, _posted);

            foreach (PointerState pointer in _pointers.Values)
                pointer.pressed = pointer.released = 0;

            for (int i = 0; i < _draining.Count; i++)
            {
                RawInputEvent evt = _draining[i];
                switch (evt.kind)
                {
                    case InputEventKind.PointerMove:
                        MovePointer(evt);
                        break;
                    case InputEventKind.Button:
                        PressPointer(evt);
                        break;
                    case InputEventKind.Scroll:
                        if (evt.window != null) ApplyScroll(evt.window, evt.delta.X, evt.delta.Y);
                        break;
                    case InputEventKind.ScrollContact:
                        contactDown = evt.action == RawAction.Down;
                        if (!contactDown) contactLifts++;
                        break;
                    case InputEventKind.Text:
                        char c = (char)evt.codepoint;
                        if (c != '\0')
                        {
                            charInputWriteQueue.Enqueue(c);
                            lastCharInput = c;
                        }
                        break;
                }
            }
        }

        #endregion

        #region ---- pointers ----
        // One pointer — the mouse, a touch contact or a pen — with its buttons as bit masks by PointerEvent button.
        public class PointerState
        {
            public int id;
            public PointerKind kind;
            public RenderWindow window;
            public Vector2 position;
            public int down;
            public int pressed;
            public int released;

            // per-button tap counting
            public int[] taps = new int[buttonCount];
            public double[] lastTapAt = new double[buttonCount];

            public bool JustPressed(int button) => (pressed & (1 << button)) != 0;

            public bool JustReleased(int button) => (released & (1 << button)) != 0;
        }

        private const int buttonCount = Keys.MouseButton8 - Keys.MouseLeft + 1;

        private Dictionary<int, PointerState> _pointers = new Dictionary<int, PointerState>();

        // The pointer the UI follows: the first one seen.
        public PointerState primaryPointer { get; private set; }

        private PointerState PointerFor(in RawInputEvent evt)
        {
            if (!_pointers.TryGetValue(evt.pointerId, out PointerState pointer))
            {
                pointer = new PointerState { id = evt.pointerId, kind = evt.pointerKind };
                _pointers.Add(evt.pointerId, pointer);
                primaryPointer ??= pointer;
            }
            if (evt.window != null) pointer.window = evt.window;
            return pointer;
        }

        private void MovePointer(in RawInputEvent evt)
        {
            PointerState pointer = PointerFor(evt);
            pointer.position = evt.position;
            if (ReferenceEquals(pointer, primaryPointer) && evt.window != null) evt.window.mousePos = evt.position;
        }

        private void PressPointer(in RawInputEvent evt)
        {
            int button = evt.key - Keys.MouseLeft;
            if (button < 0 || button >= buttonCount) return;

            PointerState pointer = PointerFor(evt);
            int bit = 1 << button;
            if (evt.action == RawAction.Down)
            {
                if ((pointer.down & bit) != 0) return;
                pointer.down |= bit;
                pointer.pressed |= bit;
                double tapWindow = SettingsRegistry.Get<InputSettings>().doubleClick.timeout;
                pointer.taps[button] = evt.timestamp - pointer.lastTapAt[button] < tapWindow ? pointer.taps[button] + 1 : 1;
                pointer.lastTapAt[button] = evt.timestamp;
            }
            else if ((pointer.down & bit) != 0)
            {
                pointer.down &= ~bit;
                pointer.released |= bit;
            }
        }
        #endregion

        #region ---- scroll input ----
        private void ApplyScroll(RenderWindow target, double offsetX, double offsetY)
        {
            double now = ScrollClock;
            if (swallowMomentum)
            {
                if (!contactDown && now - lastScrollAt < momentumGap)
                {
                    lastScrollAt = now;
                    return;
                }
                swallowMomentum = false;
            }
            if (scrollLifted && MathF.Max(MathF.Abs((float)offsetX), MathF.Abs((float)offsetY)) < momentumFloor)
            {
                scrollEndWrite = true;
                swallowMomentum = true;
                lastScrollAt = now;
                return;
            }

            target.scrollDeltaWrite.X += (float)offsetX;
            target.scrollDeltaWrite.Y += (float)offsetY;
            if (!scrollOpen)
            {
                scrollOpen = true;
                scrollTouched = false;
                scrollLifted = false;
            }
            if (contactDown) scrollTouched = true;
            lastScrollAt = now;
        }
        #endregion

        #region ---- scroll gestures ----
        // wheel quiet period, and the touchpad momentum that may follow a lift
        private const double wheelQuiet = 0.15;
        private const double momentumGap = 0.05;
        private const float momentumFloor = 0.15f;

        private bool scrollOpen;
        private bool scrollTouched;
        private bool scrollLifted;
        private bool scrollEndWrite;
        private bool swallowMomentum;
        private int seenLifts;

        // fingers on a scroll surface, and how many times the last one has lifted
        private bool contactDown;
        private int contactLifts;
        private double lastScrollAt;
        private double liftAt;

        // True for the tick a scroll gesture finished on.
        public bool scrollEnded { get; private set; }

        private static double ScrollClock => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        // A wheel gesture ends after a quiet period; a touchpad one once the fingers lift and the momentum fades.
        private void SettleScroll()
        {
            scrollEnded = false;
            double now = ScrollClock;
            if (contactLifts != seenLifts)
            {
                seenLifts = contactLifts;
                if (scrollOpen && scrollTouched)
                {
                    scrollLifted = true;
                    liftAt = now;
                }
            }
            if (scrollLifted && contactDown) scrollLifted = false;
            if (!scrollOpen) return;

            double quiet = scrollTouched ? momentumGap : wheelQuiet;
            double since = now - Math.Max(lastScrollAt, scrollLifted ? liftAt : 0.0);
            if (scrollEndWrite || (!scrollTouched || scrollLifted) && since >= quiet)
            {
                scrollEnded = true;
                swallowMomentum |= scrollTouched && !contactDown;
                scrollOpen = scrollTouched = scrollLifted = scrollEndWrite = false;
            }
            else if (!scrollTouched || scrollLifted)
                FrameScheduler.RequestFrameAt(Engine.totalTime + quiet - since);
        }
        #endregion

        public void ActivateKeybinds()
        {
            Drain();
            SettleScroll();
            lock (charInputWriteQueue)
            {
                (charInputWriteQueue, charInputReadQueue) = (charInputReadQueue, charInputWriteQueue);
            }
            foreach (RenderWindow window in Engine.windows.Values)
            {
                window.scrollDelta = window.scrollDeltaWrite;
                window.scrollDeltaWrite = new Vector2(0, 0);
            }

            keyTracker.Update(_draining, Engine.totalTime, Engine.deltaTime.TotalSeconds);
            _draining.Clear();

            // A pending capture eats the tick, so the key being bound never fires what it is bound to.
            if (_capture != null)
            {
                Keys? pressed = keyTracker.FirstPressed();
                if (pressed != null)
                {
                    Action<Keys, List<Keys>> capture = _capture;
                    _capture = null;
                    capture(pressed.Value, keyTracker.HeldModifiers());
                }
                charInputReadQueue.Clear();
                return;
            }

            // Evaluate all gesture keybinds
            gestureMatcher.Update(Engine.deltaTime.TotalSeconds);

            // Suppress char input if a modifier combo fired
            if (gestureMatcher.ShouldSuppressCharInput())
                charInputReadQueue.Clear();
        }

        public static InputHandler ParseXML(string xmlName)
        {
            InputHandler handler = new InputHandler();

            (MethodInfo method, A_XSDActionDependencyAttribute attr)[] tagged = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(m => (method: m, attr: m.GetCustomAttribute<A_XSDActionDependencyAttribute>()))
                .Where(x => x.attr != null)
                .ToArray();

            foreach (string path in VirtualFileSystem.EnumerateAll("XML/Documents/Inputs", "*.inputs.axml"))
            {
                XElement root = DataFiles.LoadXml(path);
                XNamespace ns = root.GetDefaultNamespace();
                string groupName = Paths.DocName(path);

                foreach (XElement keybindElement in root.Elements())
                {
                    if (keybindElement.Name.LocalName == "NamedModifier")
                    {
                        NamedModifier named = new NamedModifier();
                        XAttribute roleAttr = keybindElement.Attribute("Modifier");
                        XAttribute boundAttr = keybindElement.Attribute("Key");

                        if (roleAttr != null)
                            named.modifier = (InputModifier)Enum.Parse(typeof(InputModifier), roleAttr.Value);
                        if (boundAttr != null)
                            named.key = (Keys)Enum.Parse(typeof(Keys), boundAttr.Value);

                        handler.gestureMatcher.AddModifier(groupName, named);
                        continue;
                    }

                    KeybindDefinition def = new KeybindDefinition();

                    // Trigger key
                    XAttribute triggerAttr = keybindElement.Attribute("Trigger");
                    if (triggerAttr != null)
                        def.trigger = (Keys)Enum.Parse(typeof(Keys), triggerAttr.Value);

                    // Action — resolve via A_XSDActionDependency
                    XAttribute actionAttr = keybindElement.Attribute("Action");
                    if (actionAttr != null)
                    {
                        MethodInfo methodInfo = tagged
                            .FirstOrDefault(x => string.Equals(x.attr.Name, actionAttr.Value, StringComparison.OrdinalIgnoreCase))
                            .method;

                        if (methodInfo == null)
                            throw new Exception($"Action method '{actionAttr.Value}' not found in A_XSDActionDependency.");

                        def.action = (Action)Delegate.CreateDelegate(typeof(Action), methodInfo);
                        def.actionName = actionAttr.Value;
                    }

                    XAttribute accessAttr = keybindElement.Attribute("Access");
                    if (accessAttr != null)
                        def.access = (KeybindAccess)Enum.Parse(typeof(KeybindAccess), accessAttr.Value);

                    // Child elements: Modifiers and Conditions
                    foreach (XElement child in keybindElement.Elements())
                    {
                        string localName = child.Name.LocalName;

                        if (localName == "Modifier")
                        {
                            KeybindModifier mod = new KeybindModifier();
                            XAttribute keyAttr = child.Attribute("Key");
                            if (keyAttr != null)
                                mod.key = (Keys)Enum.Parse(typeof(Keys), keyAttr.Value);
                            def.modifiers.Add(mod);
                        }
                        else
                        {
                            // It's a condition — resolve type via XSD
                            InputCondition condition = CreateCondition(localName, child);
                            if (condition != null)
                                def.conditions.Add(condition);
                        }
                    }

                    handler.gestureMatcher.AddKeybind(groupName, def);
                }
            }
            return handler;
        }

        private static InputCondition CreateCondition(string typeName, XElement element)
        {
            InputCondition condition = typeName switch
            {
                "Press" => new KeyStateTracker.PressCondition(),
                "Release" => new KeyStateTracker.ReleaseCondition(),
                "Hold" => new KeyStateTracker.HoldCondition(),
                "HoldRelease" => new KeyStateTracker.HoldReleaseCondition(),
                "MaxHoldTime" => new KeyStateTracker.MaxHoldTimeCondition(),
                "MultiTap" => new KeyStateTracker.MultiTapCondition(),
                "Continuous" => new KeyStateTracker.ContinuousCondition(),
                "Repeat" => new KeyStateTracker.RepeatCondition(),
                "Toggle" => new KeyStateTracker.ToggleCondition(),
                "HoldContinuous" => new KeyStateTracker.HoldContinuousCondition(),
                "Chord" => new KeyStateTracker.ChordCondition(),
                _ => null
            };

            if (condition == null)
            {
                Log.Warn($"unknown condition type '{typeName}'");
                return null;
            }

            // Parse attributes onto the condition object
            foreach (XAttribute attr in element.Attributes())
            {
                // Try property first (most condition params are properties)
                PropertyInfo prop = condition.GetType().GetProperty(
                    attr.Name.LocalName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (prop != null && prop.CanWrite)
                {
                    if (prop.PropertyType == typeof(float))
                        prop.SetValue(condition, float.Parse(attr.Value,
                            System.Globalization.CultureInfo.InvariantCulture));
                    else if (prop.PropertyType == typeof(int))
                        prop.SetValue(condition, int.Parse(attr.Value));
                    else if (prop.PropertyType == typeof(Keys))
                        prop.SetValue(condition, Enum.Parse(typeof(Keys), attr.Value));
                    else if (prop.PropertyType == typeof(double))
                        prop.SetValue(condition, double.Parse(attr.Value,
                            System.Globalization.CultureInfo.InvariantCulture));
                    continue;
                }

                // Fall back to field
                FieldInfo field = condition.GetType().GetField(
                    attr.Name.LocalName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (field != null)
                {
                    if (field.FieldType == typeof(float))
                        field.SetValue(condition, float.Parse(attr.Value,
                            System.Globalization.CultureInfo.InvariantCulture));
                    else if (field.FieldType == typeof(int))
                        field.SetValue(condition, int.Parse(attr.Value));
                    else if (field.FieldType == typeof(Keys))
                        field.SetValue(condition, Enum.Parse(typeof(Keys), attr.Value));
                    else if (field.FieldType == typeof(double))
                        field.SetValue(condition, double.Parse(attr.Value,
                            System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            return condition;
        }

        public static void SetActiveKeybindGroup(string groupName)
        {
            instance.gestureMatcher.SetActiveGroup(groupName);
        }

        [A_XSDActionDependency("InputHandler.LoadInputs", "Bootstrap")]
        public static bool LoadInputs()
        {
            instance = ParseXML("InputMap.inputs.axml");
            Engine.inputHandler = instance;

            foreach (KeybindOverride bind in SettingsRegistry.Get<InputBindings>().binds)
                instance.gestureMatcher.Rebind(bind.action, bind.trigger, InputBindings.ParseModifiers(bind.modifiers));

            // Activate the default group (or first available)
            instance.gestureMatcher.SetActiveGroup("default");

            return true;
        }
    }
}