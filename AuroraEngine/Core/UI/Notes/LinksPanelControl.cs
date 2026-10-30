using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Core.UI.Notes
{
    // A file's backlinks and outgoing links, read from the link index of its vault and of the vaults referencing it.
    public class LinksPanelControl : StackPanelControl
    {
        private const int captionSize = 12;

        public LinksPanelControl()
        {
            Spacing = 4f;
            alpha = 0f;
            horizontalAlignment = HorizontalAlignment.Stretch;
            padding = new Thickness(8f, 10f, 8f, 10f);
        }

        // Refreshes the indexes the file's links live in and rebuilds the rows.
        public void Show(string? path)
        {
            for (int i = children.Count - 1; i >= 0; i--)
                children[i].Destroy();
            if (path == null) return;

            List<LinkIndex> indexes = new List<LinkIndex>();
            foreach (string home in new[] { path }.Concat(SheetBook.referencingVaults?.Invoke(path) ?? Enumerable.Empty<string>()))
                if (LinkIndex.indexOf?.Invoke(home) is LinkIndex index && !indexes.Contains(index))
                {
                    index.Refresh();
                    indexes.Add(index);
                }
            if (indexes.Count == 0) return;

            List<LinkEdge> incoming = indexes.SelectMany(index => index.Incoming(path)).ToList();
            IReadOnlyList<LinkEdge> outgoing = indexes[0].Outgoing(path);

            AddChild(Caption($"Backlinks ({incoming.Count})", PaletteRole.Ink));
            foreach (LinkEdge edge in incoming)
                AddChild(Row(edge.source, edge.kind, edge.kind != LinkKind.Link, edge.source));

            AddChild(Caption($"Outgoing ({outgoing.Count})", PaletteRole.Ink));
            foreach (LinkEdge edge in outgoing)
                AddChild(edge.resolved is string target
                    ? Row(target, edge.kind, edge.kind != LinkKind.Link, target)
                    : Row(edge.target, edge.kind, false, null));
        }

        // A file's name without its type's suffix.
        public static string NameOf(string path)
        {
            string name = Path.GetFileName(path);
            string suffix = FileTypes.Of(path)?.suffix ?? Path.GetExtension(path);
            return name.Length > suffix.Length ? name[..^suffix.Length] : name;
        }

        private static TabToolsControl Row(string file, LinkKind kind, bool strong, string? opens)
        {
            TabToolsControl row = new TabToolsControl { padding = Thickness.Zero, Spacing = 6f };
            LabelControl name = row.Text(opens == null ? file : NameOf(file), _ => { if (opens != null) LinkIndex.openFile?.Invoke(opens); });
            name.role = strong ? PaletteRole.Ink : PaletteRole.MutedInk;
            row.AddChild(Caption(kind.ToString().ToLowerInvariant(), PaletteRole.MutedInk));
            return row;
        }

        private static LabelControl Caption(string text, PaletteRole role) => new LabelControl
        {
            text = text,
            fontSize = captionSize,
            role = role,
            hitTestable = false,
            verticalPosition = 0.5f
        };
    }
}
