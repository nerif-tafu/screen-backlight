using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BacklightStreamer.Models;
using BacklightStreamer.Services;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace BacklightStreamer.Views;

public partial class StreamPreviewControl : UserControl
{
    private const double Pad = 40;
    private StreamFramePreview? _preview;
    private byte[]? _cachedCapturePixels;
    private int _cachedCaptureW;
    private int _cachedCaptureH;

    private double _monitorLeft;
    private double _monitorTop;
    private double _monitorW;
    private double _monitorH;
    private double _contentLeft;
    private double _contentTop;
    private double _contentW;
    private double _contentH;

    public StreamPreviewControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
    }

    public void UpdatePreview(StreamFramePreview preview)
    {
        _preview = preview;
        if (preview.CapturePixels.Length > 0 && preview.CaptureWidth > 0 && preview.CaptureHeight > 0)
        {
            _cachedCapturePixels = preview.CapturePixels;
            _cachedCaptureW = preview.CaptureWidth;
            _cachedCaptureH = preview.CaptureHeight;
        }

        PlaceholderText.Visibility = Visibility.Collapsed;
        Redraw();
    }

    public void UpdateGuide(StreamFramePreview guide)
    {
        _preview = new StreamFramePreview
        {
            Config = guide.Config,
            StripRgb = [],
            CapturePixels = _cachedCapturePixels ?? guide.CapturePixels,
            CaptureWidth = _cachedCaptureW > 0 ? _cachedCaptureW : guide.CaptureWidth,
            CaptureHeight = _cachedCaptureH > 0 ? _cachedCaptureH : guide.CaptureHeight,
            SourceCaptureWidth = guide.SourceCaptureWidth,
            SourceCaptureHeight = guide.SourceCaptureHeight,
            BorderInset = guide.BorderInset,
            SampleRadius = guide.SampleRadius,
            DiffusionDepth = guide.DiffusionDepth,
            SampleRegions = guide.SampleRegions,
            CaptureBackend = guide.CaptureBackend,
            IsLive = false
        };
        PlaceholderText.Visibility = Visibility.Collapsed;
        Redraw();
    }

    private void ComputeLayout(double canvasW, double canvasH)
    {
        var (ledW, ledH) = LedLayoutHelper.AspectFromConfig(_preview?.Config);
        var availW = canvasW - Pad * 2;
        var availH = canvasH - Pad * 2;
        var scale = Math.Min(availW / ledW, availH / ledH);

        _monitorW = ledW * scale;
        _monitorH = ledH * scale;
        _monitorLeft = Pad + (availW - _monitorW) / 2;
        _monitorTop = Pad + (availH - _monitorH) / 2;

        var hasCapture = _preview is { CaptureWidth: > 0, CaptureHeight: > 0 };
        if (!hasCapture)
        {
            _contentLeft = _monitorLeft;
            _contentTop = _monitorTop;
            _contentW = _monitorW;
            _contentH = _monitorH;
            return;
        }

        var captureAspect = _preview!.CaptureWidth / (double)_preview.CaptureHeight;
        var boxAspect = _monitorW / _monitorH;

        if (captureAspect > boxAspect)
        {
            _contentW = _monitorW;
            _contentH = _monitorW / captureAspect;
            _contentLeft = _monitorLeft;
            _contentTop = _monitorTop + (_monitorH - _contentH) / 2;
        }
        else
        {
            _contentH = _monitorH;
            _contentW = _monitorH * captureAspect;
            _contentLeft = _monitorLeft + (_monitorW - _contentW) / 2;
            _contentTop = _monitorTop;
        }
    }

    private void Redraw()
    {
        PreviewCanvas.Children.Clear();
        if (_preview == null) return;

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 80 || height < 80) return;

        ComputeLayout(width, height);
        var sourceW = Math.Max(1, _preview.SourceCaptureWidth);
        var sourceH = Math.Max(1, _preview.SourceCaptureHeight);

        var monitor = new Border
        {
            Width = _monitorW,
            Height = _monitorH,
            Background = new SolidColorBrush(Color.FromRgb(36, 48, 68)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(45, 58, 79)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };
        Canvas.SetLeft(monitor, _monitorLeft);
        Canvas.SetTop(monitor, _monitorTop);
        PreviewCanvas.Children.Add(monitor);

        if (_preview.CapturePixels.Length > 0 && _preview.CaptureWidth > 0 && _preview.CaptureHeight > 0)
        {
            var bitmap = new WriteableBitmap(
                _preview.CaptureWidth,
                _preview.CaptureHeight,
                96,
                96,
                PixelFormats.Bgra32,
                null);
            bitmap.WritePixels(
                new Int32Rect(0, 0, _preview.CaptureWidth, _preview.CaptureHeight),
                _preview.CapturePixels,
                _preview.CaptureWidth * 4,
                0);

            var imageHost = new Grid
            {
                Width = _monitorW,
                Height = _monitorH,
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            };
            imageHost.Children.Add(new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            monitor.Child = imageHost;
        }

        DrawSamplingOverlay(sourceW, sourceH);

        if (_preview.IsLive && _preview.Config != null && _preview.StripRgb.Length > 0)
        {
            var cfg = _preview.Config;
            DrawEdge(_preview, cfg.LeftStart, cfg.LeftEnd, cfg.ReverseLeft, "left");
            DrawEdge(_preview, cfg.TopStart, cfg.TopEnd, cfg.ReverseTop, "top");
            DrawEdge(_preview, cfg.RightStart, cfg.RightEnd, cfg.ReverseRight, "right");
            DrawEdge(_preview, cfg.BottomStart, cfg.BottomEnd, cfg.ReverseBottom, "bottom");
        }

        DrawLegend();
    }

    private void DrawSamplingOverlay(int sourceW, int sourceH)
    {
        if (_preview == null) return;

        var scaleX = _contentW / sourceW;
        var scaleY = _contentH / sourceH;
        var insetX = _preview.BorderInset * scaleX;
        var insetY = _preview.BorderInset * scaleY;
        var depthPx = _preview.DiffusionDepth > 0 ? _preview.DiffusionDepth : _preview.SampleRadius;
        var radiusX = depthPx * scaleX;
        var radiusY = depthPx * scaleY;
        var innerW = Math.Max(0, _contentW - insetX * 2);
        var innerH = Math.Max(0, _contentH - insetY * 2);

        var insetGuide = new System.Windows.Shapes.Rectangle
        {
            Width = innerW,
            Height = innerH,
            Stroke = new SolidColorBrush(Color.FromArgb(220, 250, 204, 21)),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = System.Windows.Media.Brushes.Transparent
        };
        Canvas.SetLeft(insetGuide, _contentLeft + insetX);
        Canvas.SetTop(insetGuide, _contentTop + insetY);
        PreviewCanvas.Children.Add(insetGuide);

        AddEdgeBand(_contentLeft + insetX, _contentTop + insetY, radiusX, innerH);
        AddEdgeBand(_contentLeft + insetX, _contentTop + insetY, innerW, radiusY);
        AddEdgeBand(_contentLeft + _contentW - insetX - radiusX, _contentTop + insetY, radiusX, innerH);
        AddEdgeBand(_contentLeft + insetX, _contentTop + _contentH - insetY - radiusY, innerW, radiusY);

        foreach (var region in _preview.SampleRegions)
        {
            if (region is null) continue;

            var overlay = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, region.Value.Width * scaleX),
                Height = Math.Max(1, region.Value.Height * scaleY),
                Fill = new SolidColorBrush(Color.FromArgb(56, 56, 189, 248)),
                Stroke = new SolidColorBrush(Color.FromArgb(160, 56, 189, 248)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(overlay, _contentLeft + region.Value.Left * scaleX);
            Canvas.SetTop(overlay, _contentTop + region.Value.Top * scaleY);
            PreviewCanvas.Children.Add(overlay);
        }
    }

    private void AddEdgeBand(double left, double top, double width, double height)
    {
        if (width < 1 || height < 1) return;

        var band = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(Color.FromArgb(28, 56, 189, 248)),
            Stroke = new SolidColorBrush(Color.FromArgb(90, 56, 189, 248)),
            StrokeThickness = 1,
            StrokeDashArray = [3, 2]
        };
        Canvas.SetLeft(band, left);
        Canvas.SetTop(band, top);
        PreviewCanvas.Children.Add(band);
    }

    private void DrawLegend()
    {
        if (_preview == null) return;

        var (ledW, ledH) = LedLayoutHelper.AspectFromConfig(_preview.Config);
        var legend = new TextBlock
        {
            Text = $"LED aspect {ledW}:{ledH} · inset {_preview.BorderInset}px · radius {(_preview.DiffusionDepth > 0 ? _preview.DiffusionDepth : _preview.SampleRadius)}px",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            Background = new SolidColorBrush(Color.FromArgb(160, 15, 23, 42)),
            Padding = new Thickness(6, 3, 6, 3)
        };
        Canvas.SetLeft(legend, _monitorLeft + 6);
        Canvas.SetTop(legend, _monitorTop + 6);
        PreviewCanvas.Children.Add(legend);
    }

    private void DrawEdge(
        StreamFramePreview preview,
        int start,
        int end,
        bool reverse,
        string edgeKey)
    {
        var indices = StreamPacketBuilder.EdgeStripIndicesForPreview(start, end, reverse).ToList();
        if (indices.Count == 0) return;

        for (var i = 0; i < indices.Count; i++)
        {
            var stripIndex = indices[i];
            var t = indices.Count <= 1 ? 0.5 : i / (double)(indices.Count - 1);
            var (x, y) = EdgeDotPosition(edgeKey, t);

            var rgbOffset = stripIndex * 3;
            if (rgbOffset + 2 >= preview.StripRgb.Length) continue;

            var color = Color.FromRgb(
                preview.StripRgb[rgbOffset],
                preview.StripRgb[rgbOffset + 1],
                preview.StripRgb[rgbOffset + 2]);

            AddDiffusedGlow(x, y, color);
        }
    }

    private (double x, double y) EdgeDotPosition(string edgeKey, double t) =>
        edgeKey switch
        {
            "left" => (_monitorLeft - 10, _monitorTop + _monitorH - t * _monitorH),
            "top" => (_monitorLeft + t * _monitorW, _monitorTop - 10),
            "right" => (_monitorLeft + _monitorW + 10, _monitorTop + t * _monitorH),
            _ => (_monitorLeft + _monitorW - t * _monitorW, _monitorTop + _monitorH + 10)
        };

    private void AddDiffusedGlow(double x, double y, Color color)
    {
        const double glowSize = 22;

        var glow = new Ellipse
        {
            Width = glowSize,
            Height = glowSize,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop(color, 0),
                    new GradientStop(Color.FromArgb(180, color.R, color.G, color.B), 0.35),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
                }
            }
        };

        Canvas.SetLeft(glow, x - glowSize / 2);
        Canvas.SetTop(glow, y - glowSize / 2);
        PreviewCanvas.Children.Add(glow);
    }
}
