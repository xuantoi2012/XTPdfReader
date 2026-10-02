using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using iText.Kernel.Pdf;
using XTPdfMergeApp;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Workspace;

internal static partial class Program
{
    static async Task TestSignaturePresenceAsync()
    {
        // Synthetic /Sig values test presence only; they are deliberately NOT cryptographic signatures.
        string empty = Path.Combine(Output, "empty-signature-field.pdf");
        string signed = Path.Combine(Output, "filled-signature-fields.pdf");
        string encrypted = Path.Combine(Output, "encrypted-signature-fields.pdf");
        WriteSignatureFixture(empty, false);
        WriteSignatureFixture(signed, true);
        WriteSignatureFixture(encrypted, true, "signature-password");
        var before = File.ReadAllBytes(signed);
        var emptyInfo = await PdfSignatureService.ReadAsync(empty);
        Check(emptyInfo.Error == null && !emptyInfo.HasSignatures, "An unsigned signature field does not trigger a signed-PDF warning");
        var signedInfo = await PdfSignatureService.ReadAsync(signed);
        Check(signedInfo.Error == null && signedInfo.SignatureCount == 2 && signedInfo.IsCertified,
            "Visible and invisible populated signature fields and DocMDP are detected");
        Check(before.SequenceEqual(File.ReadAllBytes(signed)), "Signature inspection leaves PDF bytes unchanged");
        var many = await PdfSignatureService.ReadManyAsync(new[] { signed, signed, empty });
        Check(many.Count == 2, "Repeated source files are inspected once per operation");
        var locked = await PdfSignatureService.ReadAsync(encrypted);
        Check(locked.Error != null, "An unreadable encrypted PDF is not classified as unsigned");
        await PdfThumbnailService.SetDocumentPasswordAsync(encrypted, "signature-password");
        var unlocked = await PdfSignatureService.ReadAsync(encrypted);
        Check(unlocked.Error == null && unlocked.SignatureCount == 2, "Signature detection uses the PDF password already entered in the app");
        await PdfThumbnailService.ForgetDocumentPasswordAsync(encrypted);
        var missing = await PdfSignatureService.ReadAsync(Path.Combine(Output, "missing-signature.pdf"));
        Check(missing.Error != null && !missing.HasSignatures, "Missing PDFs report an inspection error");
        await TestCancelledRotationHistoryAsync();
    }

    static async Task TestCancelledRotationHistoryAsync()
    {
        var history = new UndoRedoManager();
        bool allowWrite = false;
        history.Record(new RotateSourcePagesCommand(90, 1, _ => Task.FromResult(allowWrite)));
        Check(!await history.UndoAsync() && history.CanUndo && !history.CanRedo,
            "Cancelling a signature warning during rotation Undo leaves history unchanged");
        allowWrite = true;
        Check(await history.UndoAsync() && !history.CanUndo && history.CanRedo,
            "Completed rotation Undo moves the command only after writing succeeds");
        allowWrite = false;
        Check(!await history.RedoAsync() && history.CanRedo && !history.CanUndo,
            "Cancelling rotation Redo leaves history unchanged");
        var completion = new TaskCompletionSource<bool>();
        var busy = new UndoRedoManager();
        busy.Record(new RotateSourcePagesCommand(90, 1, _ => completion.Task));
        var pending = busy.UndoAsync();
        Check(busy.IsBusy && !busy.CanUndo && !busy.CanRedo && !await busy.UndoAsync(),
            "Repeated Undo cannot start another source-file write while one is pending");
        completion.SetResult(true);
        Check(await pending && !busy.IsBusy && busy.CanRedo, "Pending source-file Undo completes before enabling history actions");
        var failing = new UndoRedoManager();
        failing.Record(new RotateSourcePagesCommand(90, 1, _ => Task.FromException<bool>(new IOException("write failed"))));
        try { await failing.UndoAsync(); throw new Exception("Expected write error"); }
        catch (IOException) { Check(!failing.IsBusy && failing.CanUndo && !failing.CanRedo, "A failed source-file write does not advance history"); }
    }

    static void WriteSignatureFixture(string path, bool filled, string? password = null)
    {
        var properties = new WriterProperties();
        if (password != null) properties.SetStandardEncryption(System.Text.Encoding.UTF8.GetBytes(password),
            System.Text.Encoding.UTF8.GetBytes("signature-owner"), EncryptionConstants.ALLOW_PRINTING, EncryptionConstants.ENCRYPTION_AES_128);
        using var document = new PdfDocument(new PdfWriter(path, properties));
        var page = document.AddNewPage();
        var fields = new PdfArray();
        for (int i = 0; i < 2; i++)
        {
            var field = new PdfDictionary();
            field.Put(PdfName.FT, PdfName.Sig);
            field.Put(PdfName.T, new PdfString("Signature" + i));
            field.Put(PdfName.Type, PdfName.Annot);
            field.Put(PdfName.Subtype, PdfName.Widget);
            field.Put(PdfName.Rect, new PdfArray(i == 0 ? new float[] { 20, 20, 120, 60 } : new float[] { 0, 0, 0, 0 }));
            field.Put(PdfName.P, page.GetPdfObject());
            field.MakeIndirect(document);
            fields.Add(field);
            if (!filled) continue;
            var signature = new PdfDictionary();
            signature.Put(PdfName.Type, PdfName.Sig);
            signature.Put(PdfName.Filter, PdfName.Adobe_PPKLite);
            signature.Put(PdfName.SubFilter, PdfName.Adbe_pkcs7_detached);
            signature.Put(PdfName.Contents, new PdfString(new byte[] { 1, 2, 3 }));
            signature.Put(PdfName.ByteRange, new PdfArray(new int[] { 0, 1, 2, 3 }));
            signature.MakeIndirect(document);
            field.Put(PdfName.V, signature);
            if (i == 0)
            {
                var perms = new PdfDictionary();
                perms.Put(PdfName.DocMDP, signature);
                document.GetCatalog().Put(PdfName.Perms, perms);
            }
        }
        var form = new PdfDictionary();
        form.Put(PdfName.Fields, fields);
        document.GetCatalog().Put(PdfName.AcroForm, form);
    }

    static void TestAppDialogs(bool preview = false)
    {
        var app = Application.Current;
        if (app == null)
        {
            app = CreateReaderTestApplication();
        }
        foreach (var kind in new[] { MessageBoxButton.OK, MessageBoxButton.OKCancel, MessageBoxButton.YesNo, MessageBoxButton.YesNoCancel })
        {
            var dialog = new AppDialogWindow("A document has changes to save.", "Save changes", kind, MessageBoxImage.Warning);
            var expected = kind == MessageBoxButton.OK ? MessageBoxResult.OK : kind == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
            Check(dialog.Result == expected && dialog.ActionButtons.Single(b => b.IsDefault) != null, "Confirmation has a safe default: " + kind);
            dialog.Close();
        }
        var warning = new AppDialogWindow(
            "Save PDF changes\n\n• 332-QĐ-UBND-23-4-2026.pdf\n\nChanges may invalidate digital signatures or violate certification permissions. Save a separate copy to preserve the signed original.\n\nSignature validity and certificate trust have not been verified.\n\nContinue anyway?",
            "Digitally signed PDF", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, "Continue anyway");
        Check(warning.ActionButtons.Single(b => b.IsDefault).Text == "Cancel", "Enter defaults to Cancel for signed PDFs");
        warning.Loaded += (_, _) => warning.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (preview)
            {
                warning.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(warning.ActualWidth * 2), (int)Math.Ceiling(warning.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(warning);
                using var output = File.Create(Path.Combine(Output, "signed-pdf-popup.png"));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output);
                Console.WriteLine("Popup preview: " + Path.Combine(Output, "signed-pdf-popup.png"));
            }
            warning.Close();
        }));
        warning.ShowDialog();
        Check(warning.Result == MessageBoxResult.Cancel, "Closing the signed-PDF popup does not authorize writing");

        var accept = new AppDialogWindow("Continue?", "Confirmation", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        accept.Loaded += (_, _) => accept.Dispatcher.BeginInvoke(new Action(() => accept.ActionButtons.Single(b => b.Text == "OK").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        accept.ShowDialog();
        Check(accept.Result == MessageBoxResult.OK, "Only clicking the affirmative button authorizes the operation");
        if (preview) return;

        bool signedAllowed = true;
        Exception? failure = null;
        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.ApplicationIdle, (_, _) =>
            {
                foreach (Window window in app.Windows.OfType<Window>().ToArray()) if (window is AppDialogWindow && window.IsVisible) window.Close();
            }, app.Dispatcher);
            try
            {
                signedAllowed = await SignedPdfConfirmation.ConfirmAsync(null, new[] { Path.Combine(Output, "filled-signature-fields.pdf") }, "Save PDF changes", true);
                Check(!signedAllowed, "Cancel propagates through the write guard for signed sources");
                bool targetAllowed = await SignedPdfConfirmation.ConfirmAsync(null, Array.Empty<string>(), "Replace a PDF", false,
                    new[] { Path.Combine(Output, "filled-signature-fields.pdf") });
                Check(!targetAllowed, "An existing signed destination also requires confirmation");
                bool unsignedAllowed = await SignedPdfConfirmation.ConfirmAsync(null, new[] { Path.Combine(Output, "empty-signature-field.pdf") }, "Save PDF changes", true);
                Check(unsignedAllowed, "Unsigned documents proceed without a signature popup");
                bool unreadableAllowed = await SignedPdfConfirmation.ConfirmAsync(null, new[] { Path.Combine(Output, "missing-signature.pdf") }, "Save PDF changes", true);
                Check(!unreadableAllowed, "Signature inspection errors stop the write operation");
            }
            catch (Exception ex) { failure = ex; }
            finally { closer.Stop(); frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure != null) throw failure;
    }
}
