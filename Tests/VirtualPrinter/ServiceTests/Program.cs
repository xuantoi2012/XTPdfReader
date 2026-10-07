using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Xps.Packaging;
using XTReader.Printing;
using XTReader.Printing.Agent;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    [STAThread]
    static int Main(string[] args)
    {
        try { RunAsync(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static async Task RunAsync(string[] args)
    {
        string repo = Path.GetFullPath(args[0]);
        string root = Path.Combine(repo, "Tests", "bin", "virtual-printer", "service", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string sid = WindowsIdentity.GetCurrent().User!.Value, other = "S-1-5-21-1-2-3-9876";
        var converter = new MuPdfConverter(Path.Combine(repo, "bin", "MuPdfRuntime", "python.exe"), Path.Combine(repo, "VirtualPrinter", "Core", "ConvertDocument.py"));
        var store = new JobStore(Path.Combine(root, "core-spool"));
        var printer = new IppPrinter(store, converter, "ipp://127.0.0.1:18632/ipp/print", 64);
        IppRequest Request(ushort op, Action<IppResponse>? attributes = null)
        {
            using var bytes = new IppResponse(41); attributes?.Invoke(bytes); byte[] payload = bytes.Finish(); BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), op);
            return IppProtocol.ReadAsync(new MemoryStream(payload), default).GetAwaiter().GetResult();
        }
        async Task ExpectStatus(Func<Task> operation, ushort status, string label)
        { try { await operation(); throw new Exception("Expected failure: " + label); } catch (IppException error) { Check(error.Status == status, label); } }
        await ExpectStatus(() => printer.HandleAsync(Request(4, r => r.Text(0x49,"document-format","image/pwg-raster")), Stream.Null, sid, "user", default), 0x040A, "reject raster input");
        byte[] versioned=await printer.HandleAsync(Request(11) with { Major=1, Minor=1 },Stream.Null,sid,"user",default);
        Check(versioned[0]==1 && versioned[1]==1,"IPP 1.1 response preserves request version");
        await ExpectStatus(() => printer.HandleAsync(Request(4, r => r.Integer(0x21,"copies",0)), Stream.Null, sid, "user", default), 0x040B, "reject invalid copies");
        await ExpectStatus(() => printer.HandleAsync(Request(4, r => r.Value(0x33,"page-ranges",[0,0,0,5,0,0,0,9])), Stream.Null, sid, "user", default), 0x040B, "reject unsupported range instead of printing all pages");
        await ExpectStatus(() => printer.HandleAsync(Request(2), new MemoryStream(new byte[65]), sid, "user", default), 0x0408, "bounded document upload");
        Check(store.Snapshot().Last().State == 8 && !Directory.EnumerateFiles(store.Root,"*.part").Any(), "failed upload is aborted with no partial file");
        await printer.HandleAsync(Request(5, r => r.Text(0x42,"job-name","Tài liệu thử")), Stream.Null,sid,"user",default);
        var held = store.List(sid).Last();
        await ExpectStatus(() => printer.HandleAsync(Request(9,r=>r.Integer(0x21,"job-id",held.Id)),Stream.Null,other,"other",default),0x0406,"cross-user job query rejected");
        await printer.HandleAsync(Request(8,r=>r.Integer(0x21,"job-id",held.Id)),Stream.Null,sid,"user",default);
        Check(store.Get(held.Id,sid).State==7,"cancel held job");
        Check(new JobStore(store.Root).Get(held.Id,sid).State==7,"durable cancellation survives restart");
        await printer.HandleAsync(Request(5),Stream.Null,sid,"user",default);
        var created=store.List(sid).Last();
        await printer.HandleAsync(Request(6,r=>r.Integer(0x21,"job-id",created.Id).Boolean("last-document",true).Text(0x49,"document-format","application/vnd.ms-xpsdocument")),new MemoryStream([1,2,3]),sid,"user",default);
        Check(store.Get(created.Id,sid).Format=="application/vnd.ms-xpsdocument" && File.Exists(printer.InputPath(created.Id)),"Create-Job can defer format until Send-Document");
        await ExpectStatus(()=>printer.HandleAsync(Request(6,r=>r.Integer(0x21,"job-id",created.Id).Boolean("last-document",true).Text(0x49,"document-format","application/vnd.ms-xpsdocument")),new MemoryStream([1,2,3]),sid,"user",default),0x0404,"duplicate Send-Document rejected");
        await printer.HandleAsync(Request(8,r=>r.Integer(0x21,"job-id",created.Id)),Stream.Null,sid,"user",default);
        string recoverInput=printer.InputPath(created.Id);
        var recovering=store.Create(sid,"user","Recover","application/pdf",1);
        File.Copy(recoverInput,printer.InputPath(recovering.Id));
        store.Update(recovering.Id,j=>j with {State=5,Reason="job-transforming"});
        var recovered=new JobStore(store.Root).Get(recovering.Id,sid);
        Check(recovered.State==4 && recovered.Reason=="job-restarted","interrupted conversion becomes retryable on restart");
        using var cancellation=new CancellationTokenSource(); store.RegisterConversion(recovering.Id,cancellation);
        store.Cancel(recovering.Id,sid);
        Check(cancellation.IsCancellationRequested,"Cancel-Job signals running converter cancellation"); store.EndConversion(recovering.Id);
        string safe=JobStore.SafeTitle("../a:\\b\n");
        Check(!safe.Contains("..") && safe.IndexOfAny(Path.GetInvalidFileNameChars())<0 && !safe.Any(char.IsControl), "sanitize title traversal and control characters");
        try { await IppProtocol.ReadAsync(new MemoryStream([2,0,0,2]),default); throw new Exception("Truncated header accepted"); }
        catch (EndOfStreamException) { Check(true,"truncated protocol rejected"); }
        byte[] groupFlood=new byte[70008]; groupFlood[0]=2; groupFlood.AsSpan(8).Fill(1);
        await ExpectStatus(()=>IppProtocol.ReadAsync(new MemoryStream(groupFlood),default),0x0400,"attribute-group flood is bounded");
        // Generate text/vector XPS using WPF without opening a window.
        string xpsPath = Path.Combine(root,"vector.xps");
        var xpsReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var xpsThread=new Thread(()=> { try { MakeXps(xpsPath); xpsReady.SetResult(); } catch(Exception error) { xpsReady.SetException(error); } });
        xpsThread.SetApartmentState(ApartmentState.STA); xpsThread.Start(); await xpsReady.Task;
        string converted = Path.Combine(root,"vector.pdf");
        await converter.ConvertAsync(xpsPath,converted,"application/vnd.ms-xpsdocument","Vector XPS","test",default);
        Check(File.Exists(converted),"XPS conversion produces a candidate PDF");
        string readerTestPipe="XTReader_ReaderSinkTest_"+Guid.NewGuid().ToString("N");
        using(var pipe=new System.IO.Pipes.NamedPipeServerStream(readerTestPipe,System.IO.Pipes.PipeDirection.In,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous))
        {
            var receive=Task.Run(async()=> { await pipe.WaitForConnectionAsync(); using var line=new StreamReader(pipe); return await line.ReadLineAsync(); });
            await new ReaderPipeSink(null,readerTestPipe).SendAsync(converted,default);
            Check(await receive==Path.GetFullPath(converted),"Reader pipe sender verifies server owner/session and sends an absolute UTF-8 path");
        }
        // Run the actual service, authenticated HTTP and actual impersonating broker; no Reader UI.
        int port;
        using (var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback,0)) { listener.Start(); port=((IPEndPoint)listener.LocalEndpoint).Port; }
        string broker="XTReader_Test_"+Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (string value in new[] { Path.Combine(repo,"VirtualPrinter","Service","bin","Debug","net10.0-windows","XTReader.PrintService.dll"),"--Port="+port,"--SpoolRoot="+Path.Combine(root,"live-spool"),"--Python="+Path.Combine(repo,"bin","MuPdfRuntime","python.exe"),"--BrokerPipe="+broker }) start.ArgumentList.Add(value);
        using var service=Process.Start(start)!;
        Task<string> serviceOutput=service.StandardOutput.ReadToEndAsync(), serviceError=service.StandardError.ReadToEndAsync();
        try
        {
            string url=$"http://127.0.0.1:{port}/ipp/print";
            using var anonymous = new HttpClient { Timeout=TimeSpan.FromSeconds(5) };
            for(int i=0;i<80;i++)
            { try { using var ready=await anonymous.PostAsync(url,new ByteArrayContent([])); if(ready.StatusCode==HttpStatusCode.Unauthorized) break; } catch(HttpRequestException){} await Task.Delay(100); }
            using var unauthorized=await anonymous.PostAsync(url,new ByteArrayContent([]));
            Check(unauthorized.StatusCode==HttpStatusCode.Unauthorized,"HTTP denies unauthenticated local requests");
            using var handler=new HttpClientHandler { UseDefaultCredentials=true, UseProxy=false };
            using var client=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(30) };
            byte[] Packet(ushort operation,Action<IppResponse>? attrs=null,byte[]? data=null)
            { using var response=new IppResponse(500+operation); attrs?.Invoke(response); var b=response.Finish(); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2),operation); return data==null ? b : b.Concat(data).ToArray(); }
            async Task<byte[]> Send(byte[] bytes)
            { using var body=new ByteArrayContent(bytes); body.Headers.ContentType=new MediaTypeHeaderValue("application/ipp"); using var response=await client.PostAsync(url,body); Check(response.StatusCode==HttpStatusCode.OK,"authenticated HTTP request accepted"); return await response.Content.ReadAsByteArrayAsync(); }
            var capabilities=await Send(Packet(11));
            Check(BinaryPrimitives.ReadUInt16BigEndian(capabilities.AsSpan(2))==0,"printer capabilities returned");
            byte[] source=await File.ReadAllBytesAsync(Path.Combine(repo,"Tests","bin","virtual-printer","phase0","word-120-headings.pdf"));
            var timer=Stopwatch.StartNew();
            Task<byte[]>[] jobs=Enumerable.Range(0,3).Select(_=>Send(Packet(2,r=>r.Text(0x42,"job-name","Tài liệu 120 trang").Integer(0x21,"copies",2),source))).ToArray();
            var accepted=await Task.WhenAll(jobs);
            Check(accepted.All(b=>BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(2))==0),"three simultaneous jobs accepted");
            var sink=new RecordingSink(); var agent=new SessionAgent(broker,Path.Combine(root,"agent"),sink);
            for(int i=0;i<100 && sink.Paths.Count<6;i++) { await agent.PollAsync(default); await Task.Delay(100); }
            Check(sink.Paths.Count==6 && sink.Paths.Distinct().Count()==6,"three named jobs produce six distinct copy PDFs through broker");
            Check(sink.Paths.All(File.Exists),"agent receives complete durable PDFs");
            Check(!await agent.PollAsync(default),"acknowledged jobs are not redelivered");
            var persisted=new JobStore(Path.Combine(root,"live-spool"));
            Check(persisted.List(sid).Count==3 && persisted.List(sid).All(j=>j.State==9),"delivery completion is persisted");
            var fake=store.Create(sid,"user","Retry","application/pdf",1);
            string retryPdf=Path.Combine(store.Root,"retry.pdf"); File.Copy(converted,retryPdf);
            store.Update(fake.Id,j=>j with { State=3,PdfPath=retryPdf });
            Check(store.Lease(other)==null,"different user cannot lease job");
            Check(store.Lease(sid)?.Id==fake.Id && store.Lease(sid)==null,"one lease per job");
            store.Release(fake.Id); Check(store.Lease(sid)?.Id==fake.Id,"disconnect makes job retryable"); store.Release(fake.Id);
            File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new { checks, endToEndSeconds=timer.Elapsed.TotalSeconds, servicePrivateBytes=service.PrivateMemorySize64, outputPdfs=sink.Paths, xpsPdf=converted, generatedRoot=root },new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS ({checks} checks). Results: {root}");
        }
        finally
        {
            if(!service.HasExited) service.Kill(true); await service.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(root,"service.log"),await serviceOutput+await serviceError);
        }
    }
    static void MakeXps(string path)
    {
        var page=new FixedPage { Width=595.276, Height=841.89 };
        page.Children.Add(new TextBlock { Text="Vector text: Tiếng Việt có dấu", FontFamily=new FontFamily("Arial"), FontSize=18, Margin=new Thickness(40,40,0,0) });
        page.Children.Add(new System.Windows.Shapes.Line { X1=40,Y1=100,X2=500,Y2=100,Stroke=Brushes.Blue,StrokeThickness=0.5 });
        page.Measure(new Size(page.Width,page.Height)); page.Arrange(new Rect(0,0,page.Width,page.Height)); page.UpdateLayout();
        var content=new PageContent(); ((System.Windows.Markup.IAddChild)content).AddChild(page); var document=new FixedDocument(); document.Pages.Add(content);
        using var xps=new XpsDocument(path,FileAccess.Write); XpsDocument.CreateXpsDocumentWriter(xps).Write(document);
    }
    private sealed class RecordingSink : IReaderSink
    {
        public List<string> Paths { get; }=[];
        public Task SendAsync(string path,CancellationToken token) { Paths.Add(path); return Task.CompletedTask; }
    }
}
