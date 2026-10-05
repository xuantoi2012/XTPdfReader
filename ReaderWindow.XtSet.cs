using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    /// <summary>
    /// Mở file .xtset (bộ hồ sơ XT_SHEETS/XT_PRINT): mở PDF đã ghép sẵn, hoặc <b>Rebuild</b> — ghép lại từ các PDF thành phần theo công thức
    /// (thứ tự, cách gộp layer, bookmark Hạng mục → Subset → Sheet), rồi mở kết quả.
    /// </summary>
    internal async Task OpenXtSetAsync(string xtsetPath)
    {
        var recipe = XTSetRebuild.Read(xtsetPath);
        if (recipe == null)
        {
            AppDialog.Show(this, "This is not a valid XT set file (.xtset).", "XT set", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string output = XTSetRebuild.OutputPath(xtsetPath, recipe);
        var missing = XTSetRebuild.MissingParts(xtsetPath, recipe);
        bool outputExists = File.Exists(output);

        string title = recipe.ProjectName.Length > 0 ? recipe.ProjectName : Path.GetFileNameWithoutExtension(xtsetPath);
        if (missing.Count > 0)
        {
            string list = string.Join("\n", missing.Take(5).Select(p => " • " + Path.GetFileName(p))) + (missing.Count > 5 ? $"\n … (+{missing.Count - 5})" : "");
            AppDialog.Show(this, $"{missing.Count} part file(s) of \"{title}\" are missing, so it cannot be rebuilt:\n{list}" +
                (outputExists ? "\n\nThe merged PDF will be opened as it is." : ""), "XT set", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (outputExists) await Session.OpenFilesInReaderAsync(new[] { output });
            return;
        }

        string question = $"\"{title}\" has {recipe.Items.Count} parts.\n\n" +
            (outputExists
                ? $"Yes: rebuild \"{Path.GetFileName(output)}\" from the parts (replaces the existing file).\nNo: open the existing PDF as it is."
                : $"\"{Path.GetFileName(output)}\" does not exist yet. Build it from the parts?");
        var answer = AppDialog.Show(this, question, "XT set",
            outputExists ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.No && !outputExists)) return;
        if (answer == MessageBoxResult.No)
        {
            await Session.OpenFilesInReaderAsync(new[] { output });
            return;
        }

        // Đang mở bản ghép cũ: phải đóng (hỏi lưu nếu có sửa) trước khi ghi đè.
        var open = _groups.FirstOrDefault(g => string.Equals(g.SourcePath, output, StringComparison.OrdinalIgnoreCase));
        if (open != null && !await CloseGroupAsync(open)) return;

        string error = "";
        bool ok;
        Mouse.OverrideCursor = Cursors.Wait;
        try { ok = await Task.Run(() => XTSetRebuild.Rebuild(xtsetPath, recipe, out error)); }
        catch (Exception ex) { ok = false; error = ex.Message; }
        finally { Mouse.OverrideCursor = null; }

        if (!ok)
        {
            AppDialog.Show(this, "Could not rebuild the set:\n" + error, "XT set", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        try { PdfFileBuffer.Invalidate(Path.GetFullPath(output), PdfFileBuffer.InvalidateReason.Changed); } catch { }
        await Session.OpenFilesInReaderAsync(new[] { output });
        XTStyle.Controls.XTGrowl.Success($"Rebuilt {Path.GetFileName(output)} from {recipe.Items.Count} parts", this);
    }
}
