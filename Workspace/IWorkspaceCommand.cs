namespace XTPdfMergeApp.Workspace;

internal interface IWorkspaceCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}

