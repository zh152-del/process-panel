using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ProcessPanel;

// ==================== 鼠标追踪光效（Reveal）====================
// 三个效果融合为一套：
//   ① Cursor Spotlight —— 内部径向柔光，中心跟随鼠标
//   ② Border Glow      —— 边缘局部变亮（画刷中心=鼠标，靠近的边最亮）
//   ③ Edge Light Sweep —— 入场时亮区从鼠标处向外扩散（180ms），不是自动旋转
//
// 实现要点：
//   · 边缘用「环带几何填充」（外圆角矩形 − 内圆角矩形），不用粗 Pen（粗笔画在圆角处会被裁成平切）
//   · 环带完全落在控件内部（0→1.5 芯线，1.5→5.5 光晕），不外扩，圆角天然连续
//   · 实时读取控件的 ScaleTransform 并同步到绘制（悬停缩放不错位）
//   · 绘制裁剪到列表视口（滚动出视口的部分不画）
//   · 画刷/几何复用，鼠标移动只改渐变中心

/// <summary>视觉参数（统一放这里，不散落在事件处理器）。</summary>
internal static class RevealParams
{
    // 边缘
    public const double EdgeBand = 4.5;             // 单层光带宽度（与卡片边框重叠，避免多层感）
    public const double EdgePeak = 1.00;            // 边缘峰值（提亮 3 倍 → 满亮）
    public const double EdgeReach = 165;            // 亮区半径（鼠标多远内的边会亮）
    public const double SweepStartReach = 42;       // 入场扫光起始半径
    public const double SweepMs = 180;              // 入场扩散时长

    // 内部
    public const double SpotRadius = 96;
    public const double SpotCore = 0.22;            // 内部高光主透明度（0.10～0.30）

    // 时序
    public const double FadeInMs = 160;
    public const double FadeOutMs = 220;

    public static readonly Color GlowColor = Color.FromRgb(0xDC, 0xEA, 0xFF); // 冷白
}

/// <summary>光效绘制层（独立于控件内容）。</summary>
internal sealed class RevealAdorner : Adorner
{
    private Point _mouse;
    private readonly double _cornerRadius;
    private readonly RadialGradientBrush _spot;
    private readonly RadialGradientBrush _edge;

    private Geometry? _edgeRing;
    private double _geoW = -1, _geoH = -1;

    public RevealAdorner(UIElement adorned, double cornerRadius) : base(adorned)
    {
        _cornerRadius = cornerRadius;
        IsHitTestVisible = false;

        static Color WithA(double a) => Color.FromArgb(
            (byte)Math.Round(Math.Clamp(a, 0, 1) * 255),
            RevealParams.GlowColor.R, RevealParams.GlowColor.G, RevealParams.GlowColor.B);

        _spot = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            GradientOrigin = new Point(0, 0), Center = new Point(0, 0),
            RadiusX = RevealParams.SpotRadius, RadiusY = RevealParams.SpotRadius,
            GradientStops =
            {
                new GradientStop(WithA(RevealParams.SpotCore), 0),
                new GradientStop(WithA(RevealParams.SpotCore * 0.45), 0.55),
                new GradientStop(WithA(0), 1),
            },
        };

        // 边缘芯线：前段陡，靠近的边直接到峰值
        _edge = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            GradientOrigin = new Point(0, 0), Center = new Point(0, 0),
            RadiusX = RevealParams.EdgeReach, RadiusY = RevealParams.EdgeReach,
            GradientStops =
            {
                new GradientStop(WithA(RevealParams.EdgePeak), 0),
                new GradientStop(WithA(RevealParams.EdgePeak * 0.80), 0.34),
                new GradientStop(WithA(RevealParams.EdgePeak * 0.28), 0.68),
                new GradientStop(WithA(0), 1),
            },
        };

    }

    /// <summary>鼠标位置更新（复用画刷，只改中心）。</summary>
    public void UpdateMouse(Point p)
    {
        _mouse = p;
        InvalidateVisual();
    }

    /// <summary>入场扫光：亮区从鼠标处小半径扩散到完整半径（一次性）。</summary>
    public void PlaySweep()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        _edge.BeginAnimation(RadialGradientBrush.RadiusXProperty, new DoubleAnimation
        {
            From = RevealParams.SweepStartReach, To = RevealParams.EdgeReach,
            Duration = TimeSpan.FromMilliseconds(RevealParams.SweepMs),
            EasingFunction = ease, FillBehavior = FillBehavior.Stop,
        });
        _edge.BeginAnimation(RadialGradientBrush.RadiusYProperty, new DoubleAnimation
        {
            From = RevealParams.SweepStartReach, To = RevealParams.EdgeReach,
            Duration = TimeSpan.FromMilliseconds(RevealParams.SweepMs),
            EasingFunction = ease, FillBehavior = FillBehavior.Stop,
        });
    }

    private void EnsureGeometry(double w, double h)
    {
        if (Math.Abs(w - _geoW) < 0.5 && Math.Abs(h - _geoH) < 0.5 && _edgeRing != null) return;
        _geoW = w; _geoH = h;

        _edgeRing = Ring(w, h, _cornerRadius, 0, RevealParams.EdgeBand);
    }

    /// <summary>圆角环带：外圆角矩形 − 内圆角矩形（圆角半径同步收缩，保证同心连续）。</summary>
    private static Geometry Ring(double w, double h, double radius, double from, double band)
    {
        double outerR = Math.Max(0, radius - from);
        double innerR = Math.Max(0, radius - from - band);

        var outer = new RectangleGeometry(
            new Rect(from, from, Math.Max(0, w - from * 2), Math.Max(0, h - from * 2)))
        { RadiusX = outerR, RadiusY = outerR };

        double to = from + band;
        var inner = new RectangleGeometry(
            new Rect(to, to, Math.Max(0, w - to * 2), Math.Max(0, h - to * 2)))
        { RadiusX = innerR, RadiusY = innerR };

        return new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Opacity < 0.02) return;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        EnsureGeometry(w, h);
        if (_edgeRing == null) return;

        // ① 裁剪到列表视口（卡片滚出视口时不画到视口外）
        bool clipped = false;
        var sv = FindScrollViewer(AdornedElement);
        if (sv != null)
        {
            try
            {
                var vp = sv.TranslatePoint(new Point(0, 0), this);
                dc.PushClip(new RectangleGeometry(new Rect(vp.X, vp.Y, sv.ViewportWidth, sv.ViewportHeight)));
                clipped = true;
            }
            catch { }
        }

        // ② 同步控件自身的缩放（悬停放大不错位）
        bool transformed = false;
        if (AdornedElement.RenderTransform is ScaleTransform st
            && (Math.Abs(st.ScaleX - 1) > 0.001 || Math.Abs(st.ScaleY - 1) > 0.001))
        {
            dc.PushTransform(new ScaleTransform(st.ScaleX, st.ScaleY, w / 2, h / 2));
            transformed = true;
        }

        // ③ 内部高光（限制在圆角内）
        _spot.Center = _spot.GradientOrigin = _mouse;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), _cornerRadius, _cornerRadius));
        dc.DrawEllipse(_spot, null, _mouse, RevealParams.SpotRadius, RevealParams.SpotRadius);
        dc.Pop();

        // ④ 边缘：单层光带（从卡片边缘 0 起，与卡片自身边框完全重叠，不产生多层线）
        _edge.Center = _edge.GradientOrigin = _mouse;
        dc.DrawGeometry(_edge, null, _edgeRing);

        if (transformed) dc.Pop();
        if (clipped) dc.Pop();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ScrollViewer sv) return sv;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}

/// <summary>接入点：附加属性，避免逐个复制事件代码。</summary>
internal static class RevealBehavior
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(RevealBehavior),
            new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached("CornerRadius", typeof(double), typeof(RevealBehavior),
            new PropertyMetadata(10.0));

    private static readonly DependencyProperty AdornerCacheProperty =
        DependencyProperty.RegisterAttached("AdornerCache", typeof(RevealAdorner), typeof(RevealBehavior),
            new PropertyMetadata(null));

    public static void SetIsActive(DependencyObject d, bool v) => d.SetValue(IsActiveProperty, v);
    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
    public static void SetCornerRadius(DependencyObject d, double v) => d.SetValue(CornerRadiusProperty, v);
    public static double GetCornerRadius(DependencyObject d) => (double)d.GetValue(CornerRadiusProperty);

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;

        if ((bool)e.NewValue)
        {
            el.MouseEnter += OnMouseEnter;
            el.MouseMove += OnMouseMove;
            el.MouseLeave += OnMouseLeave;
            el.IsVisibleChanged += OnIsVisibleChanged;
        }
        else
        {
            el.MouseEnter -= OnMouseEnter;
            el.MouseMove -= OnMouseMove;
            el.MouseLeave -= OnMouseLeave;
            el.IsVisibleChanged -= OnIsVisibleChanged;
            if (el.GetValue(AdornerCacheProperty) is RevealAdorner old) FadeTo(old, 0);
        }
    }

    private static RevealAdorner? GetOrCreate(UIElement el)
    {
        if (el.GetValue(AdornerCacheProperty) is RevealAdorner cached) return cached;

        var layer = AdornerLayer.GetAdornerLayer(el);
        if (layer == null) return null;

        var existing = layer.GetAdorners(el)?.OfType<RevealAdorner>().FirstOrDefault();
        var ad = existing ?? new RevealAdorner(el, GetCornerRadius(el));
        if (existing == null) layer.Add(ad);

        el.SetValue(AdornerCacheProperty, ad);
        return ad;
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement el) return;

        var ad = GetOrCreate(el);
        if (ad == null) return;

        ad.UpdateMouse(e.GetPosition(el));
        ad.PlaySweep();       // 入场扫光：一次性扩散，不循环
        FadeTo(ad, 1);
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement el) return;
        if (el.GetValue(AdornerCacheProperty) is RevealAdorner ad)
            ad.UpdateMouse(e.GetPosition(el));
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement el) return;
        if (el.GetValue(AdornerCacheProperty) is RevealAdorner ad) FadeTo(ad, 0);
    }

    // 控件被虚拟化回收/隐藏：立即淡出，避免销毁后继续绘制
    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement el || (bool)e.NewValue) return;
        if (el.GetValue(AdornerCacheProperty) is RevealAdorner ad) FadeTo(ad, 0);
    }

    private static void FadeTo(RevealAdorner ad, double target)
    {
        ad.BeginAnimation(System.Windows.UIElement.OpacityProperty, new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(target > 0.01 ? RevealParams.FadeInMs : RevealParams.FadeOutMs),
            FillBehavior = FillBehavior.HoldEnd,
        });
    }
}
