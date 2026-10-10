using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static void TestGpuRaster()
    {
        if(Application.Current==null) CreateReaderTestApplication();
        using var gpu=new GpuRasterPresenter();
        var data=new byte[32*32*4];
        for(int y=0;y<32;y++) for(int x=0;x<32;x++)
        {
            int p=(y*32+x)*4; data[p]=(byte)(x*6); data[p+1]=(byte)(y*6); data[p+2]=80; data[p+3]=255;
        }
        var bitmap=BitmapSource.Create(32,32,96,96,PixelFormats.Pbgra32,null,data,128); bitmap.Freeze();
        foreach(int rotation in new[]{0,90,180,270}) foreach(double alpha in new[]{1.0,0.5})
        {
            Check(gpu.Begin(64,64,1),"Hardware Direct3D9Ex compositor starts: "+gpu.Failure);
            var outer=new Rect(0,0,64,64); var shown=new Rect(8,8,48,48); var dest=new Rect(4,12,32,32);
            gpu.Page(outer,shown,rotation); gpu.Draw(bitmap,dest,alpha); gpu.End();
            var expected=new RenderTargetBitmap(64,64,96,96,PixelFormats.Pbgra32);
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White,null,outer);
                dc.PushClip(new RectangleGeometry(shown));
                dc.PushTransform(new RotateTransform(rotation,32,32));
                dc.PushOpacity(alpha); dc.DrawImage(bitmap,dest); dc.Pop(); dc.Pop(); dc.Pop();
            }
            expected.Render(visual); var reference=new byte[64*64*4]; expected.CopyPixels(reference,256,0);
            var actual=gpu.ReadPixels();
            int max=reference.Zip(actual,(a,b)=>Math.Abs(a-b)).Max();
            Check(max<=1,$"GPU rotation {rotation}, opacity {alpha}, clip and premultiplied pixels match WPF within 1 level (max {max})");
        }
        Check(gpu.Uploads==1,"A stable page image is uploaded once across repeated frames");
        Check(gpu.TextureBytes==4096,"GPU texture cache accounts BGRA bytes");
        gpu.Image.Lock();
        try { Check(!gpu.Begin(64,64,1),"A nested/unavailable surface lock falls back without rendering"); }
        finally {gpu.Image.Unlock();}
        Check(gpu.Begin(64,64,1),"Failed TryLock attempts are balanced and do not poison future frames"); gpu.End();
        Check(gpu.Begin(80,48,1),"GPU surface resizes"); gpu.Page(new Rect(0,0,80,48),new Rect(0,0,80,48),0); gpu.End();
        Check(gpu.ReadPixels().Length==80*48*4,"Resized target has the expected dimensions");
        gpu.Dispose(); Check(gpu.TextureBytes==0,"Disposal releases texture ownership");
        Check(gpu.Begin(32,32,1),"A disposed presenter can recreate its device after reload"); gpu.End();
        gpu.Fail(new InvalidOperationException("injected device failure"));
        Check(!gpu.Begin(32,32,1),"A device failure selects WPF fallback instead of retrying every frame");
        using var bounded=new GpuRasterPresenter(8192);
        var fixtures=Enumerable.Range(0,3).Select(_=>bitmap.Clone()).ToArray();
        foreach(var fixture in fixtures)
        {
            fixture.Freeze();
            Check(bounded.Begin(64,64,1),"Bounded cache frame starts");
            bounded.Page(new Rect(0,0,64,64),new Rect(0,0,64,64),0);
            bounded.Draw(fixture,new Rect(0,0,32,32),1); bounded.End();
            Check(bounded.TextureBytes<=8192,"Texture LRU respects its byte budget");
        }
        Check(bounded.Uploads==3 && bounded.TextureBytes==8192,"Old GPU textures are evicted while current frame remains resident");
        using var asynchronous=new GpuRasterPresenter();
        int ready=0; asynchronous.FrameReady=()=>ready++;
        foreach(var fixture in fixtures)
        {
            Check(asynchronous.Begin(64,64,1),"Async preparation frame starts");
            asynchronous.Page(new Rect(0,0,64,64),new Rect(0,0,64,64),0);
            asynchronous.Draw(fixture,new Rect(0,0,32,32),1); asynchronous.End();
            Pump(TimeSpan.FromMilliseconds(100));
        }
        for(int attempt=0;attempt<10;attempt++)
        {
            Check(asynchronous.Begin(64,64,1),"Async prepared image frame starts");
            asynchronous.Page(new Rect(0,0,64,64),new Rect(0,0,64,64),0);
            asynchronous.Draw(fixtures[2],new Rect(0,0,32,32),1);
            if(asynchronous.End()) break;
            Pump(TimeSpan.FromMilliseconds(100));
        }
        Check(ready>0 && asynchronous.Frames>0,"Background preparation completes and obsolete pending images do not block newer frames");
    }

    static void BenchmarkGpuReader(string path)
    {
        if(Application.Current==null) CreateReaderTestApplication();
        ReaderPerformanceProfile.Apply(ReaderPerformanceMode.Balance);
        var reader=new ReaderWindow {Width=1300,Height=850};
        var app=Application.Current!; var prior=app.MainWindow; app.MainWindow=reader;
        string? original=Environment.GetEnvironmentVariable("XTPDF_GPU_COMPOSITOR");
        Exception? failure=null; var frame=new DispatcherFrame(); reader.Show();
        reader.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async()=>
        {
            try
            {
                Environment.SetEnvironmentVariable("XTPDF_GPU_COMPOSITOR","0");
                await reader.Session.OpenFilesInReaderAsync(new[]{path});
                var group=reader.Session.Documents.Single();
                await reader.ShowPageAsync(group,group.Pages[group.Pages.Count/2]);
                var view=(ContinuousPdfView)reader.FindName("ReaderContinuousView");
                var zoomMode=typeof(ReaderWindow).GetField("_readerZoomMode",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                zoomMode.SetValue(reader,Enum.Parse(zoomMode.FieldType,"Manual"));
                await Task.Delay(2000);
                view.ZoomAt(1.5,new Point(view.ViewportWidth/2,view.ViewportHeight/2));
                await Task.Delay(2000);
                var origin=view.ViewOffset;
                // ABBA order; repeat the same pan/zoom trace before timing each backend.
                foreach(bool enabled in new[]{false,true,true,false})
                {
                    Environment.SetEnvironmentVariable("XTPDF_GPU_COMPOSITOR",enabled?"1":"0");
                    await Animate(90,false);
                    long uploads=view.GpuPresenter?.Uploads??0;
                    long gpuFrames=view.GpuPresenter?.Frames??0;
                    long drawn=view.CompositionFrames;
                    double drawMs=view.CompositionMilliseconds;
                    double submit=view.GpuPresenter?.SubmitMilliseconds??0;
                    using var process=Process.GetCurrentProcess();
                    var cpu=process.TotalProcessorTime; var watch=Stopwatch.StartNew();
                    var intervals=await Animate(180,true);
                    process.Refresh();
                    Check(view.CompositionFrames-drawn>120,"Animation produces actual page surface frames");
                    if(enabled) Check(view.GpuPresenter is {Failure:null} && view.GpuPresenter.Frames-gpuFrames>0,"Real reader produces GPU frames without device failure: "+view.GpuPresenter?.Failure+", state="+AdaptiveMemoryController.State);
                    var sorted=intervals.Order().ToArray();
                    Console.WriteLine($"{(enabled?"GPU":"WPF")}: frames={sorted.Length}, drawn={view.CompositionFrames-drawn}, GPU frames={(view.GpuPresenter?.Frames??0)-gpuFrames}, frame interval p50={sorted[sorted.Length/2]:F2}ms p95={sorted[(int)(sorted.Length*.95)]:F2}ms, >25ms={sorted.Count(x=>x>25)}, app CPU={(process.TotalProcessorTime-cpu).TotalMilliseconds:F0}ms/{watch.Elapsed.TotalMilliseconds:F0}ms, draw CPU={view.CompositionMilliseconds-drawMs:F2}ms, GPU uploads={(view.GpuPresenter?.Uploads??0)-uploads}, submit={(view.GpuPresenter?.SubmitMilliseconds??0)-submit:F2}ms, zoom={view.Zoom:F2}");
                    SavePng(reader,enabled?"gpu-reader":"wpf-reader");
                    await Task.Delay(1500); // capture settled content separately from the timed interaction
                    var dpi=VisualTreeHelper.GetDpi(reader);
                    var capture=new RenderTargetBitmap((int)Math.Ceiling(reader.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(reader.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                    capture.Render(reader);
                    using(var stream=System.IO.File.Create(System.IO.Path.Combine(Output,enabled?"gpu-reader-settled.png":"wpf-reader-settled.png")))
                    {
                        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture)); encoder.Save(stream);
                    }
                    if(enabled) Console.WriteLine("GPU adapter: "+view.GpuPresenter?.Adapter);
                }
                async Task<List<double>> Animate(int count,bool measure)
                {
                    var times=new List<double>(); var done=new TaskCompletionSource(); int index=0; long last=0;
                    TimeSpan previousRender=TimeSpan.MinValue;
                    EventHandler handler=null!;
                    handler=(_,args)=>
                    {
                        var rendering=(RenderingEventArgs)args;
                        if(rendering.RenderingTime==previousRender) return;
                        previousRender=rendering.RenderingTime;
                        long now=Stopwatch.GetTimestamp();
                        if(last!=0 && measure) times.Add(Stopwatch.GetElapsedTime(last,now).TotalMilliseconds);
                        last=now;
                        double phase=(index%90)*Math.PI*2/90;
                        view.RestoreViewOffset(new Point(origin.X+Math.Sin(phase)*140,origin.Y+Math.Cos(phase)*120));
                        // Constant-amplitude zoom trace exercises image/region resampling.
                        view.ZoomAt(1.5+Math.Sin(phase)*.08,new Point(view.ViewportWidth/2,view.ViewportHeight/2));
                        if(++index>=count) {CompositionTarget.Rendering-=handler; done.TrySetResult();}
                    };
                    CompositionTarget.Rendering+=handler;
                    try {await done.Task.WaitAsync(TimeSpan.FromSeconds(30));}
                    finally {CompositionTarget.Rendering-=handler;}
                    return times;
                }
            }
            catch(Exception ex) {failure=ex;}
            finally {Environment.SetEnvironmentVariable("XTPDF_GPU_COMPOSITOR",original); reader.Close(); app.MainWindow=prior; frame.Continue=false;}
        }));
        Dispatcher.PushFrame(frame); if(failure!=null) throw failure;
    }
}
