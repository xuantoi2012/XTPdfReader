using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Workspace;

internal sealed class UndoRedoManager
{
    private sealed record Entry(IWorkspaceCommand Command, HashSet<string>? Sources);
    private readonly Stack<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();
    public bool IsBusy { get; private set; }
    public bool CanUndo => !IsBusy && _undo.Count > 0;
    public bool CanRedo => !IsBusy && _redo.Count > 0;
    private int _version;
    public string? UndoDescription => _undo.TryPeek(out var entry) ? entry.Command.Description : null;
    public string? RedoDescription => _redo.TryPeek(out var entry) ? entry.Command.Description : null;
    public event EventHandler? StateChanged;

    public void Execute(IWorkspaceCommand command)
    {
        var sources = Capture(command);
        command.Execute();
        var after = Capture(command);
        if (sources != null && after != null) sources.UnionWith(after);
        _undo.Push(new Entry(command, sources));
        _redo.Clear();
        _version++;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Record(IWorkspaceCommand executedCommand)
    {
        _undo.Push(new Entry(executedCommand, Capture(executedCommand)));
        _redo.Clear();
        _version++;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _version++;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static HashSet<string>? Capture(IWorkspaceCommand command)
        => command.AffectedSources is { } paths
            ? new HashSet<string>(paths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase) : null;

    public void DiscardForSource(string path)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(path) };
        var all = _undo.Concat(_redo).ToArray();
        if (all.Any(entry => entry.Sources == null)) { Clear(); return; }
        // Cross-document operations invalidate dependent history on both documents.
        bool expanded;
        do
        {
            expanded = false;
            foreach (var entry in all)
                if (entry.Sources!.Overlaps(affected))
                {
                    int count = affected.Count;
                    affected.UnionWith(entry.Sources);
                    expanded |= affected.Count != count;
                }
        } while (expanded);
        Filter(_undo);
        Filter(_redo);
        _version++;
        StateChanged?.Invoke(this, EventArgs.Empty);
        void Filter(Stack<Entry> stack)
        {
            var kept = stack.Where(entry => !entry.Sources!.Overlaps(affected)).Reverse().ToArray();
            stack.Clear();
            foreach (var entry in kept) stack.Push(entry);
        }
    }

    public void Undo() => TransferSynchronously(_undo, _redo, true);
    public Task<bool> UndoAsync() => TransferAsync(_undo, _redo, true);
    public void Redo() => TransferSynchronously(_redo, _undo, false);
    public Task<bool> RedoAsync() => TransferAsync(_redo, _undo, false);

    private void TransferSynchronously(Stack<Entry> source, Stack<Entry> destination, bool undo)
    {
        if (IsBusy || !source.TryPeek(out var entry)) return;
        var command = entry.Command;
        if (command is IAsyncWorkspaceCommand)
            throw new InvalidOperationException("Source-file undo/redo must use UndoAsync/RedoAsync.");
        if (undo) command.Undo(); else command.Execute();
        source.Pop();
        destination.Push(entry);
        _version++;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> TransferAsync(Stack<Entry> source, Stack<Entry> destination, bool undo)
    {
        if (IsBusy || !source.TryPeek(out var entry)) return false;
        int version = _version;
        IsBusy = true;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (entry.Command is IAsyncWorkspaceCommand asynchronous)
            {
                bool completed = await (undo ? asynchronous.UndoAsync() : asynchronous.ExecuteAsync());
                if (!completed) return false;
            }
            else if (undo) entry.Command.Undo();
            else entry.Command.Execute();
            if (_version != version && (!source.TryPeek(out var current) || !ReferenceEquals(current, entry)))
            {
                if (entry.Sources == null) Clear();
                else foreach (string path in entry.Sources) DiscardForSource(path);
                return true;
            }
            source.Pop();
            destination.Push(entry);
            _version++;
            return true;
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

