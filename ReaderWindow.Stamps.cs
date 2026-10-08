using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Threading.Tasks;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>Stamp (docs/UI_REDESIGN.md, mockup 12): bảng chọn dấu, đặt dấu lên trang bằng 1 lần bấm (giữ Shift để đặt tiếp), chuột phải lên dấu để xoá.</summary>
    public partial class ReaderWindow
    {
        private Popup? _stampPopup;
        private StampPopup? _stampPanel;
        private (StampDefinition Definition, int Opacity, bool Signature)? _activeStamp;

        private void ReaderStampTool_Click(object sender, RoutedEventArgs e)
        {
            if (_readerGroup == null) return;
            if (_stampPopup == null)
            {
                _stampPanel = new StampPopup();
                _stampPanel.StampChosen += (definition, opacity, signature) =>
                {
                    _activeStamp = (definition, opacity, signature);
                    if (_stampPopup != null) _stampPopup.IsOpen = false;
                    SetReaderTool(ReaderTool.Stamp);
                };
                _stampPopup = new Popup
                {
                    PlacementTarget = ReaderStampToolButton,
                    Placement = PlacementMode.Bottom,
                    StaysOpen = false,
                    AllowsTransparency = true,
                    Child = _stampPanel
                };
            }
            _stampPopup.IsOpen = !_stampPopup.IsOpen;
        }

        /// <summary>Đặt dấu đang chọn sao cho tâm dấu nằm tại điểm bấm.</summary>
        private async Task PlaceStampAsync(PageHit hit)
        {
            if (_activeStamp is not { } stamp) return;
            var page = await LoadPageAnnotationsAsync(hit.Row);
            if (page == null) return;

            string sub = stamp.Signature ? $"{Environment.UserName} · {DateTime.Now:dd/MM/yyyy}" : stamp.Definition.Sub;
            var (width, height) = PdfQuickAnnotationService.StampSize(stamp.Definition, sub, page.Geometry.DisplayWidth);
            double u1 = Math.Clamp(hit.U - width / 2 / page.Geometry.DisplayWidth, 0, 1), v1 = Math.Clamp(hit.V - height / 2 / page.Geometry.DisplayHeight, 0, 1);
            string encoded = stamp.Definition.Encode(stamp.Opacity, sub);
            var spec = new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Stamp, hit.Row.PageNumber, u1, v1,
                u1 + width / page.Geometry.DisplayWidth, v1 + height / page.Geometry.DisplayHeight, encoded) { Format = encoded, Template = "stamp:" + stamp.Definition.Id };
            CommitAnnotationChange(hit.Row, new QuickAnnotationChange(null, spec), "Stamp");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) SetReaderTool(ReaderTool.Hand);
        }
    }
}
