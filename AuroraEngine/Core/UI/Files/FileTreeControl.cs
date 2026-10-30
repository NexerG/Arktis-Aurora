using ArctisAurora.Core.Animation;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Controls;
using System.Numerics;

namespace ArctisAurora.Core.UI.Files
{
    // A browser whose folders open in place: a folder row toggles, and only an open folder puts its
    // contents on the list.
    public abstract class FileTreeControl : FileBrowserControl
    {
        // expander captions
        private const string closedPrefix = "chevron-right";
        private const string openPrefix = "chevron-down";

        // row height tweens when a folder opens or closes
        private const float expandSeconds = 0.18f;
        private const float collapseSeconds = 0.14f;

        // full paths of the open folders, so a rebuild leaves the tree as it was
        private readonly HashSet<string> expanded = new HashSet<string>();

        // expander turns, played to rest from the direction the old caption pointed
        private const string openTurn = "expander-open";
        private const string closeTurn = "expander-close";

        // the rows on the list in order with their depth, the folder whose rows grow in, the folder whose
        // expander turns on the next populate, and a collapse's tweens
        private readonly List<(FileRowControl row, int depth)> listed = new List<(FileRowControl, int)>();
        private string? growing;
        private (string path, string effect)? turning;
        private readonly List<AnimationHandle> running = new List<AnimationHandle>();

        protected override void PopulateRows()
        {
            listed.Clear();
            AddEntries(root, 0, false);
        }

        private void AddEntries(FileObject folder, int depth, bool grow)
        {
            foreach (FileObject child in folder.Children)
            {
                if (child.type == FileObject.FileType.Directory)
                {
                    if (!AcceptsFolder(child)) continue;
                    bool isOpen = expanded.Contains(child.path);
                    Track(AddRow(child, depth, isOpen ? openPrefix : closedPrefix, () => Toggle(child.path)), depth, grow);
                    if (isOpen) AddEntries(child, depth + 1, grow || child.path == growing);
                    continue;
                }

                if (!Accepts(child)) continue;
                Track(AddRow(child, depth, string.Empty, () => Activate(child)), depth, grow);
            }
        }

        private void Track(FileRowControl row, int depth, bool grow)
        {
            listed.Add((row, depth));
            if (turning is { } turn && row.file.path == turn.path) row.gutter.effect = turn.effect;
            if (!grow) return;

            row.preferredHeight = 0f;
            if (Animations.Tween(row, "Height", new Vector4(rowHeight, 0f, 0f, 0f), expandSeconds, Curve.Ease(EaseKind.CubicOut)) == AnimationHandle.None)
                row.preferredHeight = rowHeight;
        }

        // Opens a folder without rebuilding, so a caller that is about to rebuild anyway does it once.
        protected void Expand(string path) => expanded.Add(path);

        private void Toggle(string path)
        {
            if (running.Count > 0) Collapsed();

            if (expanded.Remove(path))
            {
                Collapse(path);
                return;
            }

            expanded.Add(path);
            growing = path;
            turning = (path, openTurn);
            Rebuild();
            growing = null;
            turning = null;
        }

        // Shrinks the rows under a folder to nothing, then rebuilds without them.
        private void Collapse(string path)
        {
            int at = listed.FindIndex(entry => entry.row.file.path == path);
            int end = at + 1;
            while (at >= 0 && end < listed.Count && listed[end].depth > listed[at].depth) end++;

            if (at < 0 || end == at + 1)
            {
                turning = (path, closeTurn);
                Rebuild();
                turning = null;
                return;
            }

            IconControl gutter = listed[at].row.gutter;
            gutter.iconName = closedPrefix;
            gutter.effect = closeTurn;

            for (int i = at + 1; i < end; i++)
            {
                Animations.StopAll(listed[i].row);
                AnimationHandle handle = Animations.Tween(listed[i].row, "Height", Vector4.Zero, collapseSeconds,
                                                          Curve.Ease(EaseKind.CubicOut), i == end - 1 ? Collapsed : null);
                if (handle == AnimationHandle.None)
                {
                    Collapsed();
                    return;
                }
                running.Add(handle);
            }
        }

        private void Collapsed()
        {
            foreach (AnimationHandle handle in running)
                Animations.Stop(handle);
            running.Clear();
            Rebuild();
        }
    }
}
