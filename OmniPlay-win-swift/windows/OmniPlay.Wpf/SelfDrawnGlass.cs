using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Windows.Shapes;

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
        var wash = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            StartPoint = spec.BackdropFrom,
            EndPoint = spec.BackdropTo,
            GradientStops = stops,
        };
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
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
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
    private readonly DropShadowEffect _shadow = new() { BlurRadius = 22, ShadowDepth = 2 };
    private UIElement? _source;
    private ThemeSpec _spec = null!;

    public GlassPlate(ThemeSpec spec, UIElement content, UIElement? sampleSource)
    {
        _sampled = new Rectangle();
        _sampler = sampleSource is null ? null : new VisualBrush(sampleSource)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        if (_sampler is not null) _sampled.Fill = _sampler;
        _sampled.Effect = new BlurEffect { Radius = 26 };

        _sampleClip = new Border { Child = _sampled };
        _tint = new Border();
        _sheen = new Border();
        _rim = new Border { Child = content };

        Children.Add(_sampleClip);
        Children.Add(_tint);
        Children.Add(_sheen);
        Children.Add(_rim);

        Apply(spec, sampleSource);
        // 采样得等板子真的进了视觉树。构造这会儿它还没挂上去，去问两个视觉之间的
        // 变换等于问一个没有组合目标的视觉要坐标，milcore 抛的是 Internal CLR
        // error（0x80131506）——不是异常，catch 不住，进程当场就没了。
        Loaded += (_, _) => RefreshSample();
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
                                                    new Point(0, 0), new Point(glass.SheenEndX, glass.SheenEndY))
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
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
            // 先确认两边都还挂在窗口上：少这一眼，下面那句变换就会把 milcore
            // 打进无效组合目标，直接带走进程（见构造函数里那段注释）。
            if (VisualTreeHelper.GetParent(_sampled) is null ||
                PresentationSource.FromVisual(_sampled) is null ||
                PresentationSource.FromVisual(_source) is null)
            {
                _sampleClip.Visibility = Visibility.Collapsed;
                return;
            }
            var transform = _sampled.TransformToVisual(_source);
            var origin = transform.Transform(new Point(0, 0));
            var size = new Size(_sampled.ActualWidth, _sampled.ActualHeight);
            if (size.Width < 1 || size.Height < 1)
            {
                // 零尺寸的采样区配上高斯模糊是块非法渲染目标，有些驱动直接撂挑子
                _sampleClip.Visibility = Visibility.Collapsed;
                return;
            }
            _sampler.Viewbox = new Rect(origin, size);
            _sampleClip.Visibility = _spec.Glass is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception)
        {
            // 拿不到变换不碍事：玻璃退回纯染色，界面照常能用
            _sampleClip.Visibility = Visibility.Collapsed;
        }
    }
}
