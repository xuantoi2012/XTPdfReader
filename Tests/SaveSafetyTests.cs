using System.IO;
using System.Text;
using iText.Kernel.Pdf;
using XTPdfMergeApp;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Workspace;

internal static partial class Program
{
    static async Task TestSaveSafetyAsync()
    {
        string folder = Path.Combine(Output, "save-safety");
        Directory.CreateDirectory(folder);
        string a = Path.Combine(folder, "a.pdf"), b = Path.Combine(folder, "b.pdf");
        File.WriteAllText(a, "original-a"); File.WriteAllText(b, "original-b");
        try
        {
            PdfFileTransaction.Run(new[] { a, b }, (target, stage) =>
            {
                File.WriteAllText(stage, "replacement");
                if (target == b) throw new IOException("Injected prepare failure");
            });
            throw new Exception("Expected prepare failure");
        }
        catch (IOException) { Check(File.ReadAllText(a) == "original-a" && File.ReadAllText(b) == "original-b", "Preparing multiple PDFs fails without modifying any original"); }
        using (var locked = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { PdfFileTransaction.Run(new[] { a, b }, (_, stage) => File.WriteAllText(stage, "replacement")); throw new Exception("Expected commit failure"); }
            catch (IOException) { Check(File.ReadAllText(a) == "original-a" && File.ReadAllText(b) == "original-b", "A later commit failure rolls back the first PDF"); }
        }
        Check(!Directory.GetFiles(folder, "*.tmp*").Any(), "Failed transactions remove temporary files and completed rollback backups");
        using (var doc = new PdfDocument(new PdfWriter(a))) { doc.AddNewPage(); doc.AddNewPage(); }
        byte[] originalOutput = File.ReadAllBytes(b);
        Check(!XTPdfMerger.TryMergePages(new[] { (a, 99) }, b, out var invalidError) && invalidError.Length > 0 && File.ReadAllBytes(b).SequenceEqual(originalOutput), "Invalid-page merge preserves an existing destination");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(!XTPdfMerger.TryMergePages(new[] { (a, 1) }, b, out var cancelError, cancellationToken: cancelled.Token) && cancelError.Length == 0 && File.ReadAllBytes(b).SequenceEqual(originalOutput), "Cancelling merge preserves an existing destination");

        string inline = Path.Combine(folder, "inline.pdf");
        using (var doc = new PdfDocument(new PdfWriter(inline)))
        {
            var page = doc.AddNewPage();
            page.GetPdfObject().Put(PdfName.Contents, new PdfStream(Encoding.ASCII.GetBytes("q BI /W 1 /H 1 /BPC 8 /CS /DeviceGray ID \0 EI Q")).MakeIndirect(doc));
        }
        var parts = new[] { new ExportPart { Label = "One", FileName = "a.pdf", Pages = new[] { (a, 1) } }, new ExportPart { Label = "Two", FileName = "b.pdf", Pages = new[] { (inline, 1) } } };
        byte[] beforeA = File.ReadAllBytes(a);
        var result = await PdfExportService.ExportAsync(parts, folder, true, new HashSet<string>(), false);
        Check(result.Written == 0 && result.Error.Contains("flatten", StringComparison.OrdinalIgnoreCase), "Unsupported flatten reports an explicit error instead of silently keeping hidden layers");
        Check(File.ReadAllBytes(a).SequenceEqual(beforeA) && File.ReadAllBytes(b).SequenceEqual(originalOutput), "A failed split/flatten batch preserves every destination");
        string expression = Path.Combine(folder, "visibility-expression.pdf");
        using (var doc = new PdfDocument(new PdfWriter(expression)))
        {
            var page = doc.AddNewPage();
            var ocg = new PdfDictionary(); ocg.Put(PdfName.Type, PdfName.OCG); ocg.Put(PdfName.Name, new PdfString("Hidden")); ocg.MakeIndirect(doc);
            var ve = new PdfArray(); ve.Add(new PdfName("And")); ve.Add(ocg);
            var membership = new PdfDictionary(); membership.Put(PdfName.Type, new PdfName("OCMD")); membership.Put(new PdfName("VE"), ve); membership.MakeIndirect(doc);
            var properties = new PdfDictionary(); properties.Put(new PdfName("M"), membership);
            var resources = new PdfDictionary(); resources.Put(PdfName.Properties, properties); page.GetPdfObject().Put(PdfName.Resources, resources);
            var config = new PdfDictionary(); config.Put(PdfName.OFF, new PdfArray(ocg));
            var layers = new PdfDictionary(); layers.Put(PdfName.OCGs, new PdfArray(ocg)); layers.Put(PdfName.D, config); doc.GetCatalog().Put(PdfName.OCProperties, layers);
            page.GetPdfObject().Put(PdfName.Contents, new PdfStream(Encoding.ASCII.GetBytes("/OC /M BDC 1 0 0 rg 0 0 20 20 re f EMC")).MakeIndirect(doc));
        }
        Check(!PdfLayerFlattener.Flatten(expression, b, new HashSet<string> { "Hidden" }) && File.ReadAllBytes(b).SequenceEqual(originalOutput),
            "Unsupported layer visibility expressions fail without overwriting a destination");

        string allowed = Path.Combine(folder, "allowed-encrypted.pdf");
        using (var doc = new PdfDocument(new PdfWriter(allowed, new WriterProperties().SetStandardEncryption(Encoding.UTF8.GetBytes("user"), Encoding.UTF8.GetBytes("owner"),
            EncryptionConstants.ALLOW_COPY | EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS | EncryptionConstants.ALLOW_PRINTING, EncryptionConstants.ENCRYPTION_AES_128)))) doc.AddNewPage();
        await PdfThumbnailService.SetDocumentPasswordAsync(allowed, "user");
        byte[] protectedBytes = File.ReadAllBytes(allowed);
        byte[] viewTail = PdfLayerService.BuildVisibilityTail(allowed, new HashSet<string>(), out long originalLength);
        using (var view = new PdfDocument(new PdfReader(new MemoryStream(protectedBytes.Concat(viewTail).ToArray()), PdfSecurityService.ReaderPropertiesFor(allowed))))
            Check(view.GetNumberOfPages() == 1 && originalLength == protectedBytes.Length && protectedBytes.SequenceEqual(File.ReadAllBytes(allowed)),
                "Encrypted layer views use the entered password and never modify the source file");
        Check(XTPdfMerger.TryMergePages(new[] { (allowed, 1) }, b, out var passwordError), "Encrypted PDFs with Copy permission merge using the entered user password: " + passwordError);
        var change = new QuickAnnotationChange(null, new QuickAnnotationSpec("password-note", QuickAnnotationKind.Comment, 1, .2, .2, .25, .25, "Pending encrypted note"));
        AnnotationStore.Apply(allowed, new[] { change });
        string copy = await AnnotationWorkingCopy.GetAsync(allowed);
        Check(PdfThumbnailService.TryGetDocumentPassword(copy) == "user" && (await PdfThumbnailService.TryGetPageCountAsync(copy)).PageCount == 1, "Annotation working copies forward passwords to the PDF renderer");
        Check(XTPdfMerger.TryMergePages(new[] { (copy, 1) }, b, out _), "Encrypted annotation working copies can be exported");
        AnnotationStore.ReleaseReader(allowed);
        AnnotationWorkingCopy.WriteInPlace(allowed, new[] { change });
        using (var doc = new PdfDocument(new PdfReader(allowed, PdfSecurityService.ReaderPropertiesFor(allowed))))
            Check(doc.GetReader().IsEncrypted() && doc.GetPage(1).GetAnnotations().Any(an => PdfName.Text.Equals(an.GetSubtype())), "Permitted user annotations save incrementally while preserving encryption");
        try { PdfPageEditService.RotatePages(allowed, new[] { 1 }, 90); throw new Exception("Expected permission denial"); }
        catch (UnauthorizedAccessException) { Check(true, "Modify permission is enforced by the source-edit backend"); }
        AnnotationStore.Forget(allowed); AnnotationWorkingCopy.Forget(allowed);
        await PdfThumbnailService.ForgetDocumentPasswordAsync(allowed);
        string restricted = Path.Combine(folder, "restricted.pdf");
        WriteSignatureFixture(restricted, false, "signature-password");
        await PdfThumbnailService.SetDocumentPasswordAsync(restricted, "signature-password");
        originalOutput = File.ReadAllBytes(b);
        Check(!XTPdfMerger.TryMergePages(new[] { (restricted, 1) }, b, out var denied) && denied.Length > 0 && File.ReadAllBytes(b).SequenceEqual(originalOutput), "Copy-restricted PDFs cannot overwrite a merge/export destination");
        await PdfThumbnailService.ForgetDocumentPasswordAsync(restricted);
        TestScopedHistory(a, b);
        TestRecovery(a);
        Check(ReaderWindow.ToolbarNeedsOverflow(1700, 760) && !ReaderWindow.ToolbarNeedsOverflow(1000, 1920), "Toolbar overflow works for narrow and wide logical viewports, including DPI scaling");
    }

    private sealed class ScopedCommand(params string[] sources) : IWorkspaceCommand
    {
        public string Description => string.Join("+", sources.Select(Path.GetFileName));
        public IEnumerable<string> AffectedSources => sources;
        public int Value;
        public void Execute() => Value++;
        public void Undo() => Value--;
    }
    static void TestScopedHistory(string a, string b)
    {
        var history = new UndoRedoManager();
        var ca = new ScopedCommand(a); var cb = new ScopedCommand(b);
        history.Execute(ca); history.Execute(cb); history.DiscardForSource(a); history.Undo();
        Check(ca.Value == 1 && cb.Value == 0 && history.CanRedo && !history.CanUndo, "Saving A retains independent B Undo");
        history.DiscardForSource(a); history.Redo();
        Check(cb.Value == 1 && history.CanUndo, "Saving A retains independent B Redo");
        history.Execute(new ScopedCommand(a, b)); history.DiscardForSource(a);
        Check(!history.CanUndo && !history.CanRedo, "Cross-document dependencies are discarded together after saving their source");
    }
    static void TestRecovery(string source)
    {
        var workspace = new PdfWorkspace();
        var document = new WorkspaceDocument { SourcePath = source };
        document.Pages.Add(workspace.CreatePlacement(source, 1)); document.Pages.Add(workspace.CreatePlacement(source, 2)); document.SetBaseline();
        document.Pages.RemoveAt(0);
        var change = new QuickAnnotationChange(null, new QuickAnnotationSpec("recover-note", QuickAnnotationKind.Comment, 2, .1, .1, .2, .2, "Recovery secret note"));
        AnnotationStore.Apply(source, new[] { change });
        var snapshot = SessionRecoveryStore.Capture(new[] { document }, new(), 0, 0);
        byte[] encrypted = SessionRecoveryStore.Encode(snapshot);
        Check(!Encoding.UTF8.GetString(encrypted).Contains("Recovery secret note"), "Recovery checkpoints encrypt annotation content with Windows user protection");
        var decoded = SessionRecoveryStore.Decode(encrypted);
        var restored = SessionRecoveryStore.RestoreDocument(decoded.Documents[0], new PdfWorkspace(), new Dictionary<string, string>(), new Dictionary<string, int> { [source] = 2 });
        Check(restored.Pages.Count == 1 && restored.Pages[0].PageNumber == 2 && restored.IsDirty && decoded.Sources[0].Changes[0].Add!.Text == "Recovery secret note", "Recovery preserves page edits, their saved baseline and pending annotations");
        var blank = new WorkspaceDocument { SourcePath = BlankPageService.CreateUntitledPdf(200, 300) };
        blank.Pages.Add(workspace.CreatePlacement(blank.SourcePath, 1)); blank.SetBaseline(); blank.MarkUntitled("Untitled.pdf");
        var embedded = SessionRecoveryStore.Capture(new[] { blank }, SessionRecoveryStore.CaptureDocuments(new[] { document }, _ => true), 0, 0);
        Check(embedded.Sources.Any(s => s.Embedded?.Length > 0) && embedded.Draft.Single().Temporary, "Recovery includes temporary blank PDFs and the merge shelf");
        var stale = snapshot.Sources.Single() with { Length = -1 };
        Check(!SessionRecoveryStore.SourceUnchanged(stale), "Changed source files cannot receive stale recovered annotation references");
        string checkpoint = Path.Combine(Output, "recovery-test.dat");
        SessionRecoveryStore.Save(snapshot, checkpoint);
        Check(SessionRecoveryStore.Load(checkpoint)!.Documents.Count == 1, "Encrypted checkpoints round-trip through atomic disk writes");
        encrypted[encrypted.Length / 2] ^= 255;
        try { SessionRecoveryStore.Decode(encrypted); throw new Exception("Expected corrupt checkpoint rejection"); }
        catch (System.Security.Cryptography.CryptographicException) { Check(true, "Corrupted recovery checkpoints are rejected"); }
        AnnotationStore.Forget(source);
    }
}
