using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ClaudeUsage.Controls;

public class UsageGauge : FrameworkElement
{
    private const double StartAngle = 135.0;
    private const double SweepAngle = 270.0;

    #region Dependency Properties

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(UsageGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeElapsedPercentProperty =
        DependencyProperty.Register(nameof(TimeElapsedPercent), typeof(double?), typeof(UsageGauge),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(UsageGauge),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ResetTextProperty =
        DependencyProperty.Register(nameof(ResetText), typeof(string), typeof(UsageGauge),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDarkThemeProperty =
        DependencyProperty.Register(nameof(IsDarkTheme), typeof(bool), typeof(UsageGauge),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double? TimeElapsedPercent { get => (double?)GetValue(TimeElapsedPercentProperty); set => SetValue(TimeElapsedPercentProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string ResetText { get => (string)GetValue(ResetTextProperty); set => SetValue(ResetTextProperty, value); }
    public bool IsDarkTheme { get => (bool)GetValue(IsDarkThemeProperty); set => SetValue(IsDarkThemeProperty, value); }

    #endregion

    private static readonly FontFamily SegoeUI = new("Segoe UI");
    private static readonly Typeface BoldTypeface = new(SegoeUI, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface SemiBoldTypeface = new(SegoeUI, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NormalTypeface = new(SegoeUI, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var dark = IsDarkTheme;
        var value = Math.Clamp(Value, 0, 100);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var cx = w / 2;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Layout
        var labelFontSize = 13.0;
        var labelAreaH = labelFontSize + 16;
        var bottomTextH = 36.0;

        var availableH = h - labelAreaH - bottomTextH;
        var maxRadiusFromH = availableH / 2.0;
        var maxRadiusFromW = w * 0.40;
        var radius = Math.Min(maxRadiusFromH, maxRadiusFromW);
        var arcThick = radius * 0.26;
        var tickGap = 8.0;

        var cy = labelAreaH + radius + arcThick / 2;

        var valueTextY = cy + radius * 0.7;
        var resetTextY = valueTextY + 16;

        // Draw
        DrawLabel(dc, cx, labelFontSize, dark, labelFontSize, dpi);
        DrawBackgroundArc(dc, cx, cy, radius, arcThick, dark);
        DrawTickRing(dc, cx, cy, radius, arcThick, tickGap, dark);
        DrawFillArc(dc, cx, cy, radius, arcThick, value);

        if (TimeElapsedPercent.HasValue)
            DrawTimeMarker(dc, cx, cy, radius, arcThick, TimeElapsedPercent.Value);

        DrawNeedle(dc, cx, cy, radius, arcThick, tickGap, value, dark);
        DrawScaleLabels(dc, cx, cy, radius, arcThick, dark, dpi);
        DrawValueText(dc, cx, valueTextY, value, dark, dpi);
        DrawResetText(dc, cx, resetTextY, dark, dpi);
    }

    // --- Helper: angle to point on circle ---
    private static Point AngleToPoint(double cx, double cy, double radius, double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return new Point(cx + Math.Cos(rad) * radius, cy + Math.Sin(rad) * radius);
    }

    // --- Helper: draw an arc stroke ---
    private static void DrawArcStroke(DrawingContext dc, Pen pen, double cx, double cy, double r, double startAngle, double sweepAngle)
    {
        if (sweepAngle <= 0) return;

        var start = AngleToPoint(cx, cy, r, startAngle);
        var end = AngleToPoint(cx, cy, r, startAngle + sweepAngle);
        var size = new Size(r, r);
        var isLargeArc = sweepAngle > 180;

        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, size, 0, isLargeArc, SweepDirection.Clockwise, true));

        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        dc.DrawGeometry(null, pen, geom);
    }

    // --- Drawing methods ---

    private void DrawLabel(DrawingContext dc, double cx, double y, bool dark, double fontSize, double dpi)
    {
        if (string.IsNullOrEmpty(Label)) return;
        var brush = dark ? new SolidColorBrush(Color.FromRgb(210, 210, 210)) : new SolidColorBrush(Color.FromRgb(50, 50, 50));
        var ft = new FormattedText(Label, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, BoldTypeface, fontSize, brush, dpi);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y - ft.Height / 2));
    }

    private static void DrawBackgroundArc(DrawingContext dc, double cx, double cy, double r, double thick, bool dark)
    {
        var color = dark ? Color.FromRgb(55, 55, 55) : Color.FromRgb(225, 225, 225);
        var pen = new Pen(new SolidColorBrush(color), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DrawArcStroke(dc, pen, cx, cy, r, StartAngle, SweepAngle);
    }

    private static void DrawFillArc(DrawingContext dc, double cx, double cy, double r, double thick, double value)
    {
        if (value <= 0) return;

        var (startColor, endColor) = GetGradientForValue(value);
        var sweep = SweepAngle * value / 100.0;

        var startRad = StartAngle * Math.PI / 180;
        var endRad = (StartAngle + sweep) * Math.PI / 180;
        var startPt = new Point(cx + Math.Cos(startRad) * r, cy + Math.Sin(startRad) * r);
        var endPt = new Point(cx + Math.Cos(endRad) * r, cy + Math.Sin(endRad) * r);

        var gradBrush = new LinearGradientBrush(startColor, endColor, startPt, endPt)
        {
            MappingMode = BrushMappingMode.Absolute
        };

        var pen = new Pen(gradBrush, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DrawArcStroke(dc, pen, cx, cy, r, StartAngle, sweep);
    }

    private static void DrawTickRing(DrawingContext dc, double cx, double cy, double r, double arcThick, double gap, bool dark)
    {
        var arcInner = r - arcThick / 2;
        var tickOuterR = arcInner - gap;
        var tickThick = arcThick * 0.4;
        var tickInnerR = tickOuterR - tickThick;

        var color = dark ? Color.FromRgb(95, 95, 95) : Color.FromRgb(170, 170, 170);
        var brush = new SolidColorBrush(color);

        for (double pct = 0; pct <= 100.01; pct += 2.5)
        {
            var i = (int)Math.Round(pct);
            var major = i == 20 || i == 50 || i == 80;

            var angle = CosmeticAngle(pct);
            var rad = angle * Math.PI / 180;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);

            var penWidth = major ? 2.8 : 1.5;
            var inner = major ? tickInnerR : tickOuterR - tickThick * 0.5;

            var pen = new Pen(brush, penWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen,
                new Point(cx + cos * inner, cy + sin * inner),
                new Point(cx + cos * tickOuterR, cy + sin * tickOuterR));
        }
    }

    private static void DrawScaleLabels(DrawingContext dc, double cx, double cy, double r, double thick, bool dark, double dpi)
    {
        var brush = dark ? new SolidColorBrush(Color.FromRgb(150, 150, 150)) : new SolidColorBrush(Color.FromRgb(120, 120, 120));
        var fontSize = 9.0;
        var labelR = r - thick / 2 - 26;
        var labelY = cy;

        var angle20 = StartAngle + SweepAngle * 20.0 / 100;
        var angle80 = StartAngle + SweepAngle * 80.0 / 100;
        var x20 = cx + Math.Cos(angle20 * Math.PI / 180) * labelR;
        var x80 = cx + Math.Cos(angle80 * Math.PI / 180) * labelR;

        var ft20 = new FormattedText("20", CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, NormalTypeface, fontSize, brush, dpi);
        var ft80 = new FormattedText("80", CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, NormalTypeface, fontSize, brush, dpi);

        dc.DrawText(ft20, new Point(x20 - ft20.Width / 2, labelY - ft20.Height / 2));
        dc.DrawText(ft80, new Point(x80 - ft80.Width / 2, labelY - ft80.Height / 2));
    }

    private static void DrawTimeMarker(DrawingContext dc, double cx, double cy, double r, double thick, double pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        var angle = StartAngle + SweepAngle * pct / 100;
        var rad = angle * Math.PI / 180;

        var innerR = r - thick / 2 - 3;
        var outerR = r + thick / 2 + 3;

        var pen = new Pen(Brushes.White, 2.5);
        dc.DrawLine(pen,
            new Point(cx + Math.Cos(rad) * innerR, cy + Math.Sin(rad) * innerR),
            new Point(cx + Math.Cos(rad) * outerR, cy + Math.Sin(rad) * outerR));
    }

    private static void DrawNeedle(DrawingContext dc, double cx, double cy, double r, double arcThick, double tickGap, double value, bool dark)
    {
        var angle = StartAngle + SweepAngle * value / 100;
        var rad = angle * Math.PI / 180;

        var arcInner = r - arcThick / 2;
        var tipLen = arcInner - tickGap - 12;

        var tipX = cx + Math.Cos(rad) * tipLen;
        var tipY = cy + Math.Sin(rad) * tipLen;

        var needleColor = dark ? Color.FromRgb(190, 190, 190) : Color.FromRgb(70, 70, 70);
        var needleBrush = new SolidColorBrush(needleColor);

        // 1. Background circle
        var outerDotColor = dark ? Color.FromRgb(155, 155, 155) : Color.FromRgb(140, 140, 140);
        dc.DrawEllipse(new SolidColorBrush(outerDotColor), null, new Point(cx, cy), 11, 11);

        // 2. Needle — tapered trapezoid
        var perp = rad + Math.PI / 2;
        double baseHalfW = 7, tipHalfW = 2.5;

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(new Point(tipX + Math.Cos(perp) * tipHalfW, tipY + Math.Sin(perp) * tipHalfW), true, true);
            ctx.LineTo(new Point(cx + Math.Cos(perp) * baseHalfW, cy + Math.Sin(perp) * baseHalfW), true, false);
            ctx.LineTo(new Point(cx - Math.Cos(perp) * baseHalfW, cy - Math.Sin(perp) * baseHalfW), true, false);
            ctx.LineTo(new Point(tipX - Math.Cos(perp) * tipHalfW, tipY - Math.Sin(perp) * tipHalfW), true, false);
        }
        geom.Freeze();
        dc.DrawGeometry(needleBrush, null, geom);

        // Rounded tip
        dc.DrawEllipse(needleBrush, null, new Point(tipX, tipY), tipHalfW, tipHalfW);

        // 3. Inner circle on top
        var innerDotColor = dark ? Color.FromRgb(70, 70, 70) : Color.FromRgb(230, 230, 230);
        dc.DrawEllipse(new SolidColorBrush(innerDotColor), null, new Point(cx, cy), 6.5, 6.5);
    }

    private void DrawValueText(DrawingContext dc, double cx, double y, double value, bool dark, double dpi)
    {
        var color = GetColorForValue(value);
        var brush = new SolidColorBrush(color);
        var ft = new FormattedText($"{(int)value}%", CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, SemiBoldTypeface, 20, brush, dpi);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y - ft.Height / 2));
    }

    private void DrawResetText(DrawingContext dc, double cx, double y, bool dark, double dpi)
    {
        if (string.IsNullOrEmpty(ResetText)) return;
        var color = dark ? Color.FromRgb(120, 130, 140) : Color.FromRgb(120, 120, 120);
        var brush = new SolidColorBrush(color);
        var ft = new FormattedText(ResetText, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, NormalTypeface, 10, brush, dpi);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y - ft.Height / 2));
    }

    // --- Cosmetic angle mapping ---
    private static double CosmeticAngle(double pct)
    {
        if (pct <= 20) return 135 + (pct / 20) * (180 - 135);
        if (pct <= 50) return 180 + ((pct - 20) / 30) * (270 - 180);
        if (pct <= 80) return 270 + ((pct - 50) / 30) * (360 - 270);
        return 360 + ((pct - 80) / 20) * (405 - 360);
    }

    private static (Color start, Color end) GetGradientForValue(double value)
    {
        if (value >= 90) return (Color.FromRgb(0xFF, 0x92, 0x1F), Color.FromRgb(0xEB, 0x48, 0x24));
        if (value >= 70) return (Color.FromRgb(0xFF, 0xD3, 0x94), Color.FromRgb(0xFF, 0xB3, 0x57));
        return (Color.FromRgb(0x52, 0xD1, 0x7C), Color.FromRgb(0x22, 0x91, 0x8B));
    }

    private static Color GetColorForValue(double value)
    {
        if (value >= 90) return Color.FromRgb(0xEB, 0x48, 0x24);
        if (value >= 70) return Color.FromRgb(0xFF, 0xB3, 0x57);
        return Color.FromRgb(0x52, 0xD1, 0x7C);
    }
}
