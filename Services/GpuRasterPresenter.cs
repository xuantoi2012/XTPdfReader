using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

/// <summary>Opt-in GPU raster composition. PDF interpretation stays in the native MuPDF worker.</summary>
internal sealed class GpuRasterPresenter : IDisposable
{
    internal static bool Requested => Environment.GetEnvironmentVariable("XTPDF_GPU_COMPOSITOR") == "1";
    [StructLayout(LayoutKind.Sequential)]
    internal struct Command
    {
        public int Id;
        public float X,Y,W,H,Cx,Cy,Angle,Alpha,Left,Top,Right,Bottom;
    }
    private sealed record Texture(int Id, long Bytes) { public long Frame; }
    private readonly Dictionary<BitmapSource,Texture> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly List<Command> _commands = new();
    private readonly Dictionary<BitmapSource,Task<byte[]>> _pending = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<BitmapSource> _seen = new(ReferenceEqualityComparer.Instance);
    private long _pendingBytes;
    private int _generation;
    private bool _incomplete;
    internal Action? FrameReady { get; set; }
    private readonly D3DImage _image = new();
    private IntPtr _renderer;
    private long _frame, _bytes;
    private int _nextId, _width, _height;
    private double _dpi;
    private Rect _clip;
    private double _angle, _cx, _cy;
    private bool _locked, _failed;
    internal string? Failure { get; private set; }
    internal string? Adapter { get; private set; }
    internal long Frames { get; private set; }
    internal long Uploads { get; private set; }
    internal long TextureBytes => _bytes;
    internal double UploadMilliseconds { get; private set; }
    internal double SubmitMilliseconds { get; private set; }
    internal D3DImage Image => _image;
    private readonly long Budget;
    private readonly IntPtr _window;
    internal GpuRasterPresenter(long textureBudget = 192L * 1024 * 1024,IntPtr window=default)
    {
        if(textureBudget<=0 || textureBudget>256L*1024*1024) throw new ArgumentOutOfRangeException(nameof(textureBudget));
        Budget=textureBudget;
        _window=window;
    }

    internal bool Begin(double width, double height, double dpi)
    {
        if (_failed || width<=0 || height<=0 || !_image.IsFrontBufferAvailable) return false;
        try
        {
            if (_renderer == IntPtr.Zero)
            {
                Check(xtgpu_create(out _renderer,_window));
                var adapter=new byte[512]; Check(xtgpu_adapter(_renderer,adapter,adapter.Length));
                Adapter=Encoding.UTF8.GetString(adapter).TrimEnd('\0');
            }
            _width=checked((int)Math.Ceiling(width*dpi)); _height=checked((int)Math.Ceiling(height*dpi));
            // WPF's LockImpl increments its nesting count even when TryLock times out.
            // Balance every attempt, including the unsuccessful path.
            bool acquired=_image.TryLock(new Duration(TimeSpan.Zero));
            _locked=true;
            if (!acquired) { Unlock(); return false; }
            Check(xtgpu_resize(_renderer,_width,_height,out var surface));
            _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9,surface,true);
            _dpi=dpi; _commands.Clear(); _seen.Clear(); _incomplete=false; _frame++;
            foreach(var pair in new List<KeyValuePair<BitmapSource,Texture>>(_textures))
                if (_frame-pair.Value.Frame>120) Remove(pair.Key,pair.Value);
            return true;
        }
        catch(Exception ex) { Fail(ex); return false; }
    }

    internal void Page(Rect outer, Rect shown, int angle)
    {
        _clip=new Rect(0,0,_width/_dpi,_height/_dpi); _angle=0; _cx=0; _cy=0;
        Add(0,outer,1);
        _clip=shown; _angle=angle*Math.PI/180; _cx=shown.X+shown.Width/2; _cy=shown.Y+shown.Height/2;
    }

    internal void Draw(BitmapSource source, Rect destination, double alpha)
    {
        _seen.Add(source);
        if (!_textures.TryGetValue(source,out var texture))
        {
            long bytes=checked((long)source.PixelWidth*source.PixelHeight*4);
            if (bytes>Budget) throw new InvalidOperationException("GPU texture exceeds the experimental cache budget");
            byte[] pixels;
            if (FrameReady!=null && source.IsFrozen)
            {
                if (!_pending.TryGetValue(source,out var preparation))
                {
                    // Bound outstanding preparation as well as resident textures. PDF images
                    // are immutable, so WPF pixel conversion/copy is safe off the UI thread.
                    if (_pending.Count>=2 || _pendingBytes+bytes>Budget) { _incomplete=true; return; }
                    int generation=_generation;
                    preparation=Task.Run(()=>Prepare(source));
                    _pending.Add(source,preparation); _pendingBytes+=bytes;
                    _=preparation.ContinueWith(_=>_image.Dispatcher.BeginInvoke(new Action(()=> {
                        if(generation==_generation) FrameReady?.Invoke();
                    })),TaskScheduler.Default);
                }
                if(!preparation.IsCompleted) { _incomplete=true; return; }
                _pending.Remove(source); _pendingBytes-=bytes;
                pixels=preparation.GetAwaiter().GetResult();
            }
            else pixels=Prepare(source);
            while (_bytes+bytes>Budget)
            {
                KeyValuePair<BitmapSource,Texture>? oldest=null;
                foreach(var pair in _textures)
                    if (pair.Value.Frame!=_frame && (oldest==null || pair.Value.Frame<oldest.Value.Value.Frame)) oldest=pair;
                if (oldest==null) throw new InvalidOperationException("Visible GPU textures exceed the experimental cache budget");
                Remove(oldest.Value.Key,oldest.Value.Value);
            }
            long start=Stopwatch.GetTimestamp();
            int stride=checked(source.PixelWidth*4);
            texture=new Texture(checked(++_nextId),bytes);
            Check(xtgpu_upload(_renderer,texture.Id,source.PixelWidth,source.PixelHeight,pixels,stride));
            _textures.Add(source,texture); _bytes+=bytes; Uploads++;
            UploadMilliseconds+=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        texture.Frame=_frame;
        Add(texture.Id,destination,alpha);
    }

    private static byte[] Prepare(BitmapSource source)
    {
        BitmapSource bgra=source.Format==PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source,PixelFormats.Pbgra32,null,0);
        int stride=checked(source.PixelWidth*4);
        var pixels=GC.AllocateUninitializedArray<byte>(checked(stride*source.PixelHeight));
        bgra.CopyPixels(pixels,stride,0); return pixels;
    }

    private void Add(int id,Rect rect,double alpha) => _commands.Add(new Command {
        Id=id,X=(float)(rect.X*_dpi),Y=(float)(rect.Y*_dpi),W=(float)(rect.Width*_dpi),H=(float)(rect.Height*_dpi),
        Cx=(float)(_cx*_dpi),Cy=(float)(_cy*_dpi),Angle=(float)_angle,Alpha=(float)alpha,
        Left=(float)(_clip.Left*_dpi),Top=(float)(_clip.Top*_dpi),Right=(float)(_clip.Right*_dpi),Bottom=(float)(_clip.Bottom*_dpi)
    });

    internal bool End()
    {
        try
        {
            if(_incomplete) return false;
            long start=Stopwatch.GetTimestamp();
            Check(xtgpu_render(_renderer,_commands.ToArray(),_commands.Count));
            _image.AddDirtyRect(new Int32Rect(0,0,_width,_height));
            SubmitMilliseconds+=Stopwatch.GetElapsedTime(start).TotalMilliseconds; Frames++;
            return true;
        }
        finally
        {
            bool removed=false;
            foreach(var pair in new List<KeyValuePair<BitmapSource,Task<byte[]>>>(_pending))
                if(pair.Value.IsCompleted && !_seen.Contains(pair.Key))
                {
                    _=pair.Value.Exception;
                    _pending.Remove(pair.Key); _pendingBytes-=(long)pair.Key.PixelWidth*pair.Key.PixelHeight*4; removed=true;
                }
            Unlock();
            if(removed && _incomplete) _image.Dispatcher.BeginInvoke(new Action(()=>FrameReady?.Invoke()));
        }
    }

    internal byte[] ReadPixels()
    {
        var pixels=new byte[checked(_width*_height*4)];
        Check(xtgpu_read(_renderer,pixels,_width*4)); return pixels;
    }

    private void Remove(BitmapSource key,Texture texture)
    {
        xtgpu_remove(_renderer,texture.Id); _textures.Remove(key); _bytes-=texture.Bytes;
    }
    private static void Check(int result) { if(result<0) Marshal.ThrowExceptionForHR(result); }
    private void Unlock() { if(_locked) { _locked=false; _image.Unlock(); } }
    internal void Fail(Exception ex)
    {
        Failure=ex.Message; _failed=true;
        Debug.WriteLine("[GPU compositor fallback] "+ex);
        Dispose();
    }
    public void Dispose()
    {
        Unlock();
        if(_renderer==IntPtr.Zero) return;
        _image.Lock();
        try { _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9,IntPtr.Zero); }
        finally { _image.Unlock(); }
        xtgpu_destroy(_renderer); _renderer=IntPtr.Zero;
        _textures.Clear(); _bytes=0; _commands.Clear();
        _pending.Clear(); _seen.Clear(); _pendingBytes=0; _generation++;
    }

    [DllImport("xtgpuraster",CallingConvention=CallingConvention.Cdecl)] private static extern int xtgpu_create(out IntPtr renderer,IntPtr window);
    [DllImport("xtgpuraster",CallingConvention=CallingConvention.Cdecl)] private static extern int xtgpu_adapter(IntPtr renderer,[Out] byte[] text,int capacity);
    [DllImport("xtgpuraster",CallingConvention=CallingConvention.Cdecl)] private static extern void xtgpu_destroy(IntPtr renderer);
    [DllImport("xtgpuraster",CallingConvention=CallingConvention.Cdecl)] private static extern int xtgpu_resize(IntPtr renderer,int width,int height,out IntPtr surface);
    [DllImport("xtgpuraster",CallingConvention=CallingConvention.Cdecl)] private static extern int xtgpu_upload(IntPtr renderer,int id,int width,int height,[In] byte[] pixels,int stride);
    [DllImport("xtgpuraster",CallingConvention=CallingConvention.Cdecl)] private static extern void xtgpu_remove(IntPtr renderer,int id);
    [DllImport("xtgpuraster",CallingConvention=CallingConvention.Cdecl)] private static extern int xtgpu_render(IntPtr renderer,[In] Command[] commands,int count);
    [DllImport("xtgpuraster",CallingConvention=CallingConvention.Cdecl)] private static extern int xtgpu_read(IntPtr renderer,[Out] byte[] pixels,int stride);
}
