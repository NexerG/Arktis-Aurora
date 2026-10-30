using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers.Tabs
{
    // A row of small tool buttons an editor shows on its pane's tab row while its tab is active.
    // Buttons act on press and leave the active control where it was.
    public class TabToolsControl : StackPanelControl
    {
        // metrics
        private const float buttonSize = 20f;
        private const float iconSize = 12f;
        private const int captionSize = 12;
        private const float captionInset = 6f;

        public override bool takesActiveControl => false;

        public TabToolsControl()
        {
            orientation = Orientation.Horizontal;
            Spacing = 2f;
            alpha = 0f;
            padding = new Thickness(0f, 4f, 0f, 8f);
        }

        public IconControl Icon(string icon, Action<TabToolButton> press)
        {
            IconControl ink = Ink(icon);
            TabToolButton button = Button(press);
            button.preferredWidth = buttonSize;
            button.AddChild(ink);
            AddChild(button);
            return ink;
        }

        // A caption, with a chevron when it drops a menu.
        public LabelControl Text(string text, Action<TabToolButton> press, bool drops = false)
        {
            LabelControl caption = new LabelControl
            {
                text = text,
                fontSize = captionSize,
                role = PaletteRole.Ink,
                hitTestable = false
            };
            StackPanelControl row = new StackPanelControl
            {
                orientation = Orientation.Horizontal,
                Spacing = 4f,
                alpha = 0f,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f
            };
            row.AddChild(caption);
            if (drops) row.AddChild(Ink("chevron-down"));

            TabToolButton button = Button(press);
            button.padding = new Thickness(0f, captionInset, 0f, captionInset);
            button.AddChild(row);
            AddChild(button);
            return caption;
        }

        public void Separator() => AddChild(new PanelControl
        {
            preferredWidth = 1f,
            preferredHeight = 14f,
            margin = new Thickness(0f, 4f, 0f, 4f),
            verticalAlignment = VerticalAlignment.Center,
            role = PaletteRole.Line
        });

        // Lights a glyph the way the format bar does; a call that changes nothing writes nothing.
        public static void Light(IconControl ink, bool on, ref bool? shown)
        {
            if (shown == on) return;

            shown = on;
            ink.PaintOr(null, on ? PaletteRole.Accent : PaletteRole.MutedInk);
        }

        public static void Drop(Control owner, List<ContextMenuEntry> entries)
        {
            if (ContextMenus.ClosedOn(owner)) return;
            ContextMenus.Open(entries, owner, new Vector2(owner.arrangedRect.x, owner.arrangedRect.Bottom));
        }

        private static TabToolButton Button(Action<TabToolButton> press)
        {
            TabToolButton button = new TabToolButton { preferredHeight = buttonSize, verticalAlignment = VerticalAlignment.Center, cornerRole = CornerRole.Control };
            button.PaintOr(null, PaletteRole.Clear);
            button.pressed = press;
            return button;
        }

        private static IconControl Ink(string icon)
        {
            IconControl ink = new IconControl
            {
                setName = "default",
                iconName = icon,
                preferredWidth = iconSize,
                preferredHeight = iconSize,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f
            };
            ink.PaintOr(null, PaletteRole.MutedInk);
            return ink;
        }

        // Acts on press, not release: with the active control left alone, no release arrives.
        public class TabToolButton : ButtonControl
        {
            public Action<TabToolButton>? pressed;

            public override bool takesActiveControl => false;

            public override bool OnPointerPress(PointerEvent e)
            {
                base.OnPointerPress(e);
                if (e.button == PointerEvent.leftButton) pressed?.Invoke(this);
                return true;
            }
        }
    }
}
