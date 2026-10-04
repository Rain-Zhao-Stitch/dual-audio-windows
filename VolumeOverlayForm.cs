using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Wpf = System.Windows;
using Media = System.Windows.Media;

namespace DualAudio;

// A transparent WPF surface over a native DWM acrylic popup. Keeping this HWND
// non-layered is essential: global Form.Opacity would disable the real backdrop.
internal sealed class VolumeOverlayForm : IDisposable
{
    private const int LogicalWidth = 296;
    private const int LogicalHeight = 66;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly GlassVisual _visual = new();
    private HwndSource? _source;
    private long _shownAt;
    private long _lastFrame;
    private double _displayValue;
    private int _holdMilliseconds = 1300;
    private Rectangle _bounds;
    private double _scale = 1;
    private bool _disposed;

    public int CurrentValue { get; private set; }
    public bool Visible => _source is not null && IsWindowVisible(Handle);
    internal bool GlassEnabled { get; private set; }
    internal Rectangle DisplayBounds => _bounds;
    internal IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public VolumeOverlayForm() => _timer.Tick += Animate;

    internal static void RenderPreview(string path)
    {
        var visual = new GlassVisual { Volume = 50, Level = 0.5, GlassEnabled = false };
        visual.Measure(new Wpf.Size(LogicalWidth, LogicalHeight));
        visual.Arrange(new Wpf.Rect(0, 0, LogicalWidth, LogicalHeight));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(LogicalWidth * 3,
            LogicalHeight * 3, 288, 288, Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    public void ShowVolume(int value) => ShowVolume(value, 1300);

    internal void ShowVolume(int value, int holdMilliseconds)
    {
        if (_disposed) return;
        CurrentValue = Math.Clamp(value, 0, 100);
        _holdMilliseconds = holdMilliseconds;
        EnsureWindow();
        PositionOverlay();
        if (!Visible) _displayValue = CurrentValue;
        _shownAt = _lastFrame = Stopwatch.GetTimestamp();
        _visual.Volume = CurrentValue;
        _visual.Level = _displayValue / 100;
        _visual.Opacity = 1;
        _visual.InvalidateVisual();
        ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
        // The launcher's STARTUPINFO can override the first ShowWindow call.
        if (!IsWindowVisible(Handle)) ShowWindow(Handle, 4);
        _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        if (_source is not null) ShowWindow(Handle, 0);
    }

    private void EnsureWindow()
    {
        if (_source is not null) return;
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var parameters = new HwndSourceParameters("Dual Audio · 总音量")
        {
            Width = LogicalWidth, Height = LogicalHeight,
            PositionX = screen.WorkingArea.Left, PositionY = screen.WorkingArea.Top,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, no frame
            ExtendedWindowStyle = 0x080000A0, // NOACTIVATE | TOOLWINDOW
            UsesPerPixelOpacity = false,
            HwndSourceHook = WindowProc
        };
        _source = new HwndSource(parameters);
        _source.CompositionTarget.BackgroundColor = Media.Colors.Transparent;
        _source.RootVisual = _visual;
        ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        if (_source is null) return;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var dark = 0;
        var rounded = 2;
        var acrylic = 3; // DWMSBT_TRANSIENTWINDOW: Desktop Acrylic
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
        var frameResult = DwmExtendFrameIntoClientArea(Handle, ref margins);
        var backdropResult = DwmSetWindowAttribute(Handle, 38, ref acrylic, sizeof(int));
        GlassEnabled = frameResult >= 0 && backdropResult >= 0;
        _visual.GlassEnabled = GlassEnabled;
        _visual.InvalidateVisual();
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x84) { handled = true; return new IntPtr(-1); }
        if (message == 0x21) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }
        if (message == 0x86) // Keep the acrylic's visual state active, without taking focus.
        {
            handled = true;
            return DefWindowProc(hwnd, message, new IntPtr(1), new IntPtr(-1));
        }
        if (message == 0x31E) ApplyBackdrop(); // DWM composition changed
        return IntPtr.Zero;
    }

    private void PositionOverlay()
    {
        var screen = Screen.FromHandle(GetForegroundWindow());
        var area = screen.WorkingArea;
        var monitor = MonitorFromPoint(new NativePoint { X = area.Left, Y = area.Top }, 2);
        var dpiResult = GetDpiForMonitor(monitor, 0, out var dpi, out _);
        _scale = (dpiResult >= 0 && dpi > 0 ? dpi : 96) / 96d;
        var width = (int)Math.Round(LogicalWidth * _scale);
        var height = (int)Math.Round(LogicalHeight * _scale);
        _bounds = new Rectangle(area.Right - width - (int)Math.Round(16 * _scale),
            area.Top + (int)Math.Round(16 * _scale), width, height);
        SetWindowPos(Handle, new IntPtr(-1), _bounds.X, _bounds.Y, width, height, 0x0010);
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, (int)(46 * _scale), (int)(46 * _scale));
        if (region != IntPtr.Zero && SetWindowRgn(Handle, region, true) == 0) DeleteObject(region);
        _visual.Measure(new Wpf.Size(LogicalWidth, LogicalHeight));
        _visual.Arrange(new Wpf.Rect(0, 0, LogicalWidth, LogicalHeight));
        _visual.UpdateLayout();
    }

    private void Animate(object? sender, EventArgs e)
    {
        if (!Visible) return;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_shownAt, now).TotalMilliseconds;
        var frame = Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds;
        _lastFrame = now;
        _displayValue += (CurrentValue - _displayValue) * (1 - Math.Exp(-frame / 65));
        _visual.Level = _displayValue / 100;
        _visual.Glint = Math.Clamp(elapsed / 360, 0, 1);
        var exiting = Math.Clamp((elapsed - _holdMilliseconds) / 160, 0, 1);
        _visual.Opacity = 1 - exiting;
        var offset = (int)Math.Round(exiting * exiting * 7 * _scale);
        SetWindowPos(Handle, IntPtr.Zero, _bounds.X, _bounds.Y + offset, 0, 0, 0x0015);
        _visual.InvalidateVisual();
        if (exiting >= 1) Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
        _timer.Dispose();
        _source?.Dispose();
        _source = null;
    }

    private sealed class GlassVisual : Wpf.FrameworkElement
    {
        public int Volume { get; set; }
        public double Level { get; set; }
        public double Glint { get; set; }
        public bool GlassEnabled { get; set; }
        private static Media.SolidColorBrush Brush(byte a, byte r, byte g, byte b)
            => new(Media.Color.FromArgb(a, r, g, b));

        protected override void OnRender(Media.DrawingContext dc)
        {
            base.OnRender(dc);
            dc.PushTransform(new Media.ScaleTransform(RenderSize.Width / LogicalWidth, RenderSize.Height / LogicalHeight));
            var rect = new Wpf.Rect(0.75, 0.75, 294.5, 64.5);
            dc.PushClip(new Media.RectangleGeometry(rect, 23, 23));
            dc.DrawRoundedRectangle(Brush(GlassEnabled ? (byte)20 : (byte)235, 115, 125, 135), null, rect, 23, 23);
            var rim = new Media.LinearGradientBrush(
                Media.Color.FromArgb(130, 255, 255, 255),
                Media.Color.FromArgb(28, 255, 255, 255), 90);
            dc.DrawRoundedRectangle(null, new Media.Pen(rim, 0.8), rect, 23, 23);
            DrawText(dc, "双设备同步音量", 12, 17, 10, "Microsoft YaHei UI", true);
            var track = new Wpf.Rect(32, 39, 226, 4);
            dc.DrawRoundedRectangle(Brush(45, 255, 255, 255), null, track, 2, 2);
            if (Level > 0)
                dc.DrawRoundedRectangle(Brush(250, 255, 255, 255), null,
                    new Wpf.Rect(track.X, track.Y, Math.Max(2, track.Width * Math.Clamp(Level, 0, 1)), 4), 2, 2);
            for (var i = 0; i < 17; i++)
                dc.DrawEllipse(Brush(65, 255, 255, 255), null, new Wpf.Point(36 + i * 13.6, 47), 0.8, 0.8);
            DrawSpeaker(dc, 17, 41, false, Volume == 0);
            DrawSpeaker(dc, 264, 41, true, false);
            dc.Pop();
            dc.Pop();
        }

        private static void DrawSpeaker(Media.DrawingContext dc, double x, double y, bool waves, bool muted)
        {
            dc.PushTransform(new Media.TranslateTransform(x, y));
            var body = Media.Geometry.Parse("M0,-2 L3,-2 L7,-5 Q8,-6 8,-4 L8,4 Q8,6 7,5 L3,2 L0,2 Z");
            dc.DrawGeometry(Media.Brushes.White, null, body);
            var pen = new Media.Pen(Media.Brushes.White, 1.1)
            { StartLineCap = Media.PenLineCap.Round, EndLineCap = Media.PenLineCap.Round };
            if (waves)
            {
                dc.DrawGeometry(null, pen, Media.Geometry.Parse("M11,-3 Q14,0 11,3 M14,-5 Q18,0 14,5 M17,-7 Q22,0 17,7"));
            }
            if (muted)
                dc.DrawLine(pen, new Wpf.Point(-1, -6), new Wpf.Point(10, 6));
            dc.Pop();
        }

        private void DrawText(Media.DrawingContext dc, string text, double size, double x, double y,
            string family, bool bold, byte alpha = 245)
        {
            var formatted = new Media.FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                Wpf.FlowDirection.LeftToRight, new Media.Typeface(new Media.FontFamily(family), Wpf.FontStyles.Normal,
                    bold ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal, Wpf.FontStretches.Normal),
                size, Brush(alpha, 255, 255, 255), Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, new Wpf.Point(x, y));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
