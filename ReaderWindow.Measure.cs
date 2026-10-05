using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    // ── Công cụ Measure: kéo giữa 2 điểm → độ dài thật theo tỷ lệ của bản vẽ (không ghi vào PDF) ──

    private sealed record MeasureDrag(PageRow Row, double StartU, double StartV);
    private MeasureDrag? _measureDrag;
    private bool _measureFrozen;
    private readonly Dictionary<(string Path, int Page), double> _manualScales = new();
    private static readonly Dictionary<string, (long Length, DateTime Stamp, IReadOnlyDictionary<int, XTSheetPageInfo> Infos)> MeasureInfoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mẫu số tỷ lệ của trang: từ thông tin sheet trong PDF, hoặc số người dùng đã nhập cho trang này; null nếu chưa biết.</summary>
    private double? ScaleOf(PageRow row)
    {
        if (_manualScales.TryGetValue((row.SourcePath, row.PageNumber), out double manual)) return manual;
        try
        {
            var info = new System.IO.FileInfo(row.SourcePath);
            IReadOnlyDictionary<int, XTSheetPageInfo> infos;
            lock (MeasureInfoCache)
            {
                if (!MeasureInfoCache.TryGetValue(row.SourcePath, out var hit) || hit.Length != info.Length || hit.Stamp != info.LastWriteTimeUtc)
                    MeasureInfoCache[row.SourcePath] = hit = (info.Length, info.LastWriteTimeUtc, XTSheetIndex.Read(row.SourcePath));
                infos = hit.Infos;
            }
            return infos.TryGetValue(row.PageNumber, out var sheet) ? PageMeasure.ParseScale(sheet.Scale) : null;
        }
        catch { return null; }
    }

    private void BeginMeasure(PageHit hit)
    {
        ClearMeasure();
        if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point start)) return;
        _ = LoadPageAnnotationsAsync(hit.Row); // cần hình học trang (khổ giấy theo point)
        _measureDrag = new MeasureDrag(hit.Row, hit.U, hit.V);
        ReaderContentHost.CaptureMouse();
        ReaderMeasureLine.X1 = ReaderMeasureLine.X2 = start.X;
        ReaderMeasureLine.Y1 = ReaderMeasureLine.Y2 = start.Y;
        ReaderMeasureLine.Visibility = Visibility.Visible;
    }

    private bool UpdateMeasure(Point pointInHost)
    {
        if (_measureDrag is not { } drag) return false;
        if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var current)) return true;
        if (!TryPageToLayer(drag.Row, drag.StartU, drag.StartV, out Point a) || !TryPageToLayer(drag.Row, current.U, current.V, out Point b)) return true;
        ReaderMeasureLine.X2 = b.X;
        ReaderMeasureLine.Y2 = b.Y;
        ShowMeasureLabel(drag, current.U, current.V, b);
        return true;
    }

    private bool FinishMeasure(Point pointInHost)
    {
        if (_measureDrag is not { } drag) return false;
        UpdateMeasure(pointInHost);
        _measureDrag = null;
        if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
        double length = Math.Abs(ReaderMeasureLine.X2 - ReaderMeasureLine.X1) + Math.Abs(ReaderMeasureLine.Y2 - ReaderMeasureLine.Y1);
        if (length < 4) { ClearMeasure(); return true; }
        _measureFrozen = true; // đoạn đo ở lại màn hình tới khi đo tiếp / Esc / đổi công cụ
        if (ScaleOf(drag.Row) == null) _ = AskScaleAsync(drag);
        return true;
    }

    private void ShowMeasureLabel(MeasureDrag drag, double u, double v, Point at)
    {
        if (GetCachedPageAnnotations(drag.Row)?.Geometry is not { } geometry) { ReaderMeasureLabel.Visibility = Visibility.Collapsed; return; }
        double dxPt = (u - drag.StartU) * geometry.DisplayWidth, dyPt = (v - drag.StartV) * geometry.DisplayHeight;
        double points = Math.Sqrt(dxPt * dxPt + dyPt * dyPt);
        double? scale = ScaleOf(drag.Row);
        ReaderMeasureText.Text = scale is { } s
            ? $"{PageMeasure.FormatLength(PageMeasure.RealLengthMm(points, s))}   (1:{s:0.##})"
            : $"{points * 25.4 / 72:0.#} mm on paper — scale unknown";
        ReaderMeasureLabel.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(ReaderMeasureLabel, at.X + 12);
        System.Windows.Controls.Canvas.SetTop(ReaderMeasureLabel, at.Y + 12);
    }

    /// <summary>Trang không có tỷ lệ trong thông tin sheet: hỏi người dùng 1 lần cho trang này (vd 1:100).</summary>
    private async Task AskScaleAsync(MeasureDrag drag)
    {
        await Task.Yield();
        string? text = TextPromptWindow.Ask(this, "Scale of this sheet", "This page has no scale in its sheet info. Enter it to measure real lengths (for example 1:100):", "1:100");
        if (PageMeasure.ParseScale(text) is not { } scale) return;
        _manualScales[(drag.Row.SourcePath, drag.Row.PageNumber)] = scale;
        if (_measureFrozen && GetCachedPageAnnotations(drag.Row)?.Geometry != null &&
            TryPageToLayer(drag.Row, drag.StartU, drag.StartV, out _))
        {
            // recompute the label for the frozen segment from the line's end in page coordinates
            if (TryGetPagePoint(drag.Row, new Point(ReaderMeasureLine.X2, ReaderMeasureLine.Y2), clamp: true, out var end))
                ShowMeasureLabel(drag, end.U, end.V, new Point(ReaderMeasureLine.X2, ReaderMeasureLine.Y2));
        }
    }

    private void ClearMeasure()
    {
        _measureDrag = null;
        _measureFrozen = false;
        ReaderMeasureLine.Visibility = Visibility.Collapsed;
        ReaderMeasureLabel.Visibility = Visibility.Collapsed;
        if (ReaderContentHost.IsMouseCaptured && _readerTool == ReaderTool.Measure) ReaderContentHost.ReleaseMouseCapture();
    }
}
