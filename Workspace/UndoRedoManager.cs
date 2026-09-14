using System;
using System.Collections.Generic;

namespace XTPdfMergeApp.Workspace;

internal sealed class UndoRedoManager
{
    private readonly Stack<IWorkspaceCommand> _undo = new();
    private readonly Stack<IWorkspaceCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.TryPeek(out var command) ? command.Description : null;
    public string? RedoDescription => _redo.TryPeek(out var command) ? command.Description : null;

    public event EventHandler? StateChanged;

    public void Execute(IWorkspaceCommand command)
    {
        command.Execute();
        _undo.Push(command);
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var command)) return;
        command.Undo();
        _redo.Push(command);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var command)) return;
        command.Execute();
        _undo.Push(command);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

