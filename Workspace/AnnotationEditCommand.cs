using System.Collections.Generic;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Workspace;

/// <summary>Annotation edit kept in memory (<see cref="AnnotationStore"/>): Undo drops the entries from the unsaved log, Redo adds them again.</summary>
internal sealed class AnnotationEditCommand(string description, string path, IReadOnlyList<QuickAnnotationChange> changes) : IWorkspaceCommand
{
    public string Description { get; } = description;
    public IEnumerable<string> AffectedSources => new[] { path };
    public void Execute() => AnnotationStore.Apply(path, changes);
    public void Undo() => AnnotationStore.Revert(path, changes);
}
