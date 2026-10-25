using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace OmniPlay.WpfSpike;

/// 页面最底层：三段斜向渐变 + 主题色光斑。mac 那侧是 SwiftUI 画的，这里用
/// DrawingContext 自己画同一样东西，两边吃的都是核心给的同一份配方。
public sealed class GlassBackdrop : FrameworkElement
{
    private ThemeSpec? _spec;

    public void Apply(ThemeSpec spec)
    {
        _spec = spec;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var spec = _spec;
        var size = RenderSize;
        if (spec is null || size.Width < 1 || size.Height < 1) return;
        var rect = new Rect(size);

        if (spec.Backdrop.Count == 0)
        {
            dc.DrawRectangle(new SolidColorBrush(spec.Background), null, rect);
            return;
        }

        var stops = new GradientStopCollection();
        for (var i = 0; i < spec.Backdrop.Count; i++)
            stops.Add(new GradientStop(spec.Backdrop[i], (double)i / Math.Max(1, spec.Backdrop.Count - 1)));
        var wash = new LinearGradientBrush(stops, spec.BackdropFrom, spec.BackdropTo,
                                          MappingMode.RelativeToBoundingBox);
        dc.DrawRectangle(wash, null, rect);

        // 光斑：核心给的是归一化圆心与扩散半径，按短边折算成像素
        var glass = spec.Glass;
        if (glass is null) return;
        var diagonal = Math.Sqrt(size.Width * size.Width + size.Height * size.Height);
        foreach (var glow in glass.Glows)
        {
            var radius = Math.Max(1d, diagonal * glow.Spread * 0.5);
            var center = new Point(size.Width * glow.X, size.Height * glow.Y);
            var brush = new RadialGradientBrush
            {
                MappingMode = MappingMode.RelativeToBoundingBox,
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            brush.GradientStops.Add(new GradientStop(glow.Color, 0));
            brush.GradientStops.Add(new GradientStop(ThemeSpec.Fade(glow.Color, 0), 1));
            dc.DrawEllipse(brush, null, center, radius, radius);
        }
    }
}

/// 自绘玻璃板。四层从下往上：采样本窗口内容的模糊层、主题染色、高光、描边与内容。
/// 采样用 VisualBrush 把「板下方那块画面」原样映射进来再高斯模糊，
/// 所以底下的海报墙滚动时玻璃里跟着变色——这是 Mica/Acrylic 做不到的那一条。
public sealed class GlassPlate : Grid
{
    private readonly Border _sampleClip;
    private readonly Rectangle _sampled;
    private readonly VisualBrush? _sampler;
    private readonly Border _tint;
    private readonly Border _sheen;
    private readonly Border _rim;
    private readonly DropShadowEffect _shadow = new() { BlurRadius = 22, ShadowDepth = 2, RenderingBias = EffectRenderingBias.Performance };
    private UIElement? _source;
    private ThemeSpec _spec = null!;

    public GlassPlate(ThemeSpec spec, UIElement content, UIElement? sampleSource)
    {
        _sampled = new Rectangle();
        _sampler = sampleSource is null ? null : new VisualBrush(sampleSource)
        {
            Stretch = Stretch.Fill,
            TileMode = BrushTileMode.None,
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        if (_sampler is not null) _sampled.Fill = _sampler;
        _sampled.Effect = new BlurEffect { Radius = 26, KernelType = KernelType.GaussianKernel };

        _sampleClip = new Border { Child = _sampled };
        _tint = new Border();
        _sheen = new Border();
        _rim = new Border { Child = content };

        Children.Add(_sampleClip);
        Children.Add(_tint);
        Children.Add(_sheen);
        Children.Add(_rim);

        Apply(spec, sampleSource);
        SizeChanged += (_, _) => RefreshSample();
    }

    public void Apply(ThemeSpec spec, UIElement? sampleSource = null)
    {
        _spec = spec;
        if (sampleSource is not null)
        {
            _source = sampleSource;
            if (_sampler is not null) _sampler.Visual = sampleSource;
        }

        var glass = spec.Glass;
        var radius = new CornerRadius(glass?.CornerRadius ?? 8);
        _sampleClip.CornerRadius = radius;
        _tint.CornerRadius = radius;
        _sheen.CornerRadius = radius;
        _rim.CornerRadius = radius;

        if (glass is null)
        {
            // 经典风格：不采样、不打高光，就是一块实底卡面
            _sampleClip.Visibility = Visibility.Collapsed;
            _sheen.Visibility = Visibility.Collapsed;
            _tint.Background = new SolidColorBrush(spec.Surface);
            _rim.BorderBrush = new SolidColorBrush(Colors.Transparent);
            _rim.BorderThickness = new Thickness(0);
            Effect = null;
            return;
        }

        _sampleClip.Visibility = Visibility.Visible;
        _sheen.Visibility = Visibility.Visible;
        _tint.Background = new SolidColorBrush(glass.Tint);
        _sheen.Background = new LinearGradientBrush(glass.SheenTop, glass.SheenBottom,
                                                   new Point(0, 0), new Point(glass.SheenEndX, glass.SheenEndY),
                                                   MappingMode.RelativeToBoundingBox);
        _rim.BorderBrush = new SolidColorBrush(glass.Border);
        _rim.BorderThickness = new Thickness(1);
        _shadow.Color = glass.Shadow;
        Effect = _shadow;
        RefreshSample();
    }

    /// 重算「板下方那块画面」。Popup 里的板与窗口内容不同根，拿不到变换时退回纯染色。
    public void RefreshSample()
    {
        if (_sampler is null || _source is null) return;
        try
        {
            var transform = _sampled.TransformToVisual(_source);
            var origin = transform.Transform(new Point(0, 0));
            _sampler.Viewbox = new Rect(origin, new Size(_sampled.ActualWidth, _sampled.ActualHeight));
            _sampleClip.Visibility = _spec.Glass is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (InvalidOperationException)
        {
            _sampleClip.Visibility = Visibility.Collapsed;
        }
    }
}
