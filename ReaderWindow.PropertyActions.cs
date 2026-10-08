using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp
{
    /// <summary>Actions of the floating property bars that are not a style: duplicate the selection, bring it to the front.</summary>
    public partial class ReaderWindow
    {
        private IReadOnlyList<QuickAnnotationSpec> SelectionMembers()
            => _selAnn is { } spec ? new[] { spec }.Concat(_selMates).ToList() : Array.Empty<QuickAnnotationSpec>();

        private void PropDuplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelection();
        private void PropFront_Click(object sender, RoutedEventArgs e) => BringSelectionToFront();

        /// <summary>A copy 12 pt down and right of the selection (a group is copied as a new group, text boxes with their shape). The copy is selected.</summary>
        private void DuplicateSelection()
        {
            if (_selAnn is not { } primary || _selRow is not { } row || GetCachedPageAnnotations(row) is not { } page) return;
            if (!Controls.PdfPermissionDialog.Require(this, new[] { row.SourcePath }, PdfPermissionOperation.Annotate)) return;
            var members = SelectionMembers();
            double dw = page.Geometry.DisplayWidth, dh = page.Geometry.DisplayHeight;
            double du = 12 / dw, dv = 12 / dh;
            // Near the right / bottom edge the copy goes the other way so it stays on the page.
            if (members.Max(m => m.U2) + du > 1) du = -du;
            if (members.Max(m => m.V2) + dv > 1) dv = -dv;
            if (members.Min(m => m.U1) + du < 0) du = 0;
            if (members.Min(m => m.V1) + dv < 0) dv = 0;

            string group = primary.Group.Length > 0 ? NewAnnotationName() : "";
            var changes = new List<QuickAnnotationChange>();
            QuickAnnotationSpec? selectedCopy = null;
            foreach (var member in members)
            {
                var copy = member.Translate(du, dv) with
                {
                    Name = NewAnnotationName(), ObjectNumber = 0, Generation = 0, Group = member.Group.Length > 0 ? group : "", Author = "", Date = null, Resolved = false
                };
                changes.Add(new QuickAnnotationChange(null, copy));
                if (member.Name == primary.Name) selectedCopy = copy;
            }
            CommitAnnotationChanges(row, changes, members.Count > 1 ? "Duplicate group" : "Duplicate " + KindLabel(primary.Kind));
            if (selectedCopy != null) SelectAnnotation(row, selectedCopy);
        }

        /// <summary>Z-order of annotations is their order in /Annots: rewriting a member appends it, so the selection (a group in its own order) ends up on top.</summary>
        private void BringSelectionToFront()
        {
            if (_selAnn is not { } primary || _selRow is not { } row) return;
            if (!Controls.PdfPermissionDialog.Require(this, new[] { row.SourcePath }, PdfPermissionOperation.Annotate)) return;
            var members = SelectionMembers().OrderBy(m => m.Name == primary.Name ? 1 : 0).ToList();
            var changes = members.Select(m => new QuickAnnotationChange(m, Regenerated(m))).ToList();
            CommitAnnotationChanges(row, changes, "Bring to front");
            SelectAnnotation(row, Regenerated(primary));
        }
    }
}
