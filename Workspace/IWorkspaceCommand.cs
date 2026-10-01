namespace XTPdfMergeApp.Workspace;

internal interface IWorkspaceCommand
{
    string Description { get; }
    void Execute();
    void Undo();
    System.Collections.Generic.IEnumerable<string>? AffectedSources => null;
}

internal interface IAsyncWorkspaceCommand : IWorkspaceCommand
{
    System.Threading.Tasks.Task<bool> ExecuteAsync();
    System.Threading.Tasks.Task<bool> UndoAsync();
}
