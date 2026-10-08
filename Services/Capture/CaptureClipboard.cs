using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services.Capture
{
    internal static class CaptureClipboard
    {
        /// <summary>Puts the picture on the clipboard as a bitmap and as a PNG (which keeps the transparent area; most programs read the PNG first).</summary>
        public static void Copy(BitmapSource image)
        {
            var data = new DataObject();
            data.SetImage(image);
            data.SetData("PNG", new MemoryStream(CaptureImaging.EncodePng(image)));
            Clipboard.SetDataObject(data, true);
        }
    }
}
