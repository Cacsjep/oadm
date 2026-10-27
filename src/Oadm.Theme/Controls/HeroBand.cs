using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Theme.Controls;

/// <summary>
/// The band at the top of the login, "Trust this server?" and new version windows (they have no title bar): the
/// <see cref="Art"/> in the middle (the OADM logo by default), optional dots on both sides (<see cref="ShowsDots"/>), an
/// optional update badge with sparkles (<see cref="ShowsUpdate"/>) and an X top right that runs <see cref="CloseCommand"/> or
/// closes the window. Colors and height from the theme (<c>tc|HeroBand</c>).
/// </summary>
public sealed class HeroBand : Border
{
    public static readonly StyledProperty<Control?> ArtProperty =
        AvaloniaProperty.Register<HeroBand, Control?>(nameof(Art));

    public static readonly StyledProperty<bool> ShowsDotsProperty =
        AvaloniaProperty.Register<HeroBand, bool>(nameof(ShowsDots));

    public static readonly StyledProperty<bool> ShowsUpdateProperty =
        AvaloniaProperty.Register<HeroBand, bool>(nameof(ShowsUpdate));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<HeroBand, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<string> CloseTipProperty =
        AvaloniaProperty.Register<HeroBand, string>(nameof(CloseTip), "Close");

    private readonly HeroDecor _decor = new();
    private readonly ContentControl _art = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) };

    public HeroBand()
    {
        _close.Classes.Add("toolbar");
        _close.Classes.Add("iconOnly");
        _close.Content = new OadmIcon { Data = ToolbarButton.ResolveIcon("close") };
        ToolTip.SetTip(_close, CloseTip);
        _close.Click += (_, _) =>
        {
            if (CloseCommand is { } command)
            {
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                }
            }
            else
            {
                (TopLevel.GetTopLevel(this) as Window)?.Close();
            }
        };
        _art.Content = new OadmMark { Width = 120, Height = 120 };
        Child = new Panel { Children = { _decor, _art, _close } };
    }

    /// <summary>The picture in the middle; the OADM logo (120 px) when not set.</summary>
    public Control? Art
    {
        get => GetValue(ArtProperty);
        set => SetValue(ArtProperty, value);
    }

    /// <summary>Faint dots on both sides of the picture, none above or below it.</summary>
    public bool ShowsDots
    {
        get => GetValue(ShowsDotsProperty);
        set => SetValue(ShowsDotsProperty, value);
    }

    /// <summary>An accent badge with an up arrow at the logo's top right, and a few sparkles (new version).</summary>
    public bool ShowsUpdate
    {
        get => GetValue(ShowsUpdateProperty);
        set => SetValue(ShowsUpdateProperty, value);
    }

    /// <summary>What the X does (e.g. Quit); null closes the window.</summary>
    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <summary>Tooltip of the X.</summary>
    public string CloseTip
    {
        get => GetValue(CloseTipProperty);
        set => SetValue(CloseTipProperty, value);
    }

    /// <summary>The X, for tests.</summary>
    public Button CloseButton => _close;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ArtProperty)
        {
            _art.Content = Art ?? new OadmMark { Width = 120, Height = 120 };
        }
        else if (change.Property == ShowsDotsProperty || change.Property == ShowsUpdateProperty)
        {
            _decor.ShowsDots = ShowsDots;
            _decor.ShowsUpdate = ShowsUpdate;
            _decor.InvalidateVisual();
        }
        else if (change.Property == CloseTipProperty)
        {
            ToolTip.SetTip(_close, CloseTip);
        }
    }

    /// <summary>Dots and the update badge, drawn around the middle of the band.</summary>
    private sealed class HeroDecor : Control
    {
        private const double DotStep = 20, DotRadius = 1.5, FreeHalfWidth = 70;

        public bool ShowsDots { get; set; }

        public bool ShowsUpdate { get; set; }

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width, height = Bounds.Height;
            var center = new Point(width / 2, height / 2);
            if (ShowsDots && Brush("Oadm.SplitterGripBrush") is { } dot)
            {
                for (double y = DotStep / 2; y < height; y += DotStep)
                {
                    for (double x = DotStep / 2; x < width; x += DotStep)
                    {
                        // Nothing above or below the logo, nothing under the X.
                        if (Math.Abs(x - center.X) <= FreeHalfWidth || (x > width - 44 && y < 48))
                        {
                            continue;
                        }

                        context.DrawEllipse(dot, null, new Point(x, y), DotRadius, DotRadius);
                    }
                }
            }

            if (!ShowsUpdate)
            {
                return;
            }

            IBrush? accent = Brush("Oadm.AccentBrush"), light = Brush("Oadm.AccentHoverBrush"), teal = Brush("Oadm.TealBrush");
            Sparkle(context, light, center + new Point(-96, -42), 7);
            Sparkle(context, teal, center + new Point(102, 32), 5);
            context.DrawEllipse(accent, null, center + new Point(-82, 44), 2.5, 2.5);
            context.DrawEllipse(teal, null, center + new Point(108, -18), 2, 2);

            // The badge: an accent circle with a ring of the band's color and a white up arrow.
            var badge = center + new Point(74, -48);
            context.DrawEllipse(accent, new Pen(Brush("Oadm.WindowBackgroundBrush"), 4), badge, 18, 18);
            var arrow = new Pen(Brush("Oadm.TextOnAccentBrush"), 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawLine(arrow, badge + new Point(0, 9), badge + new Point(0, -8));
            context.DrawLine(arrow, badge + new Point(-7, -1), badge + new Point(0, -8));
            context.DrawLine(arrow, badge + new Point(7, -1), badge + new Point(0, -8));
        }

        /// <summary>A four-point star of half size <paramref name="r"/>.</summary>
        private static void Sparkle(DrawingContext context, IBrush? brush, Point c, double r)
        {
            double k = r * 0.3;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(c + new Point(0, -r), true);
                g.LineTo(c + new Point(k, -k));
                g.LineTo(c + new Point(r, 0));
                g.LineTo(c + new Point(k, k));
                g.LineTo(c + new Point(0, r));
                g.LineTo(c + new Point(-k, k));
                g.LineTo(c + new Point(-r, 0));
                g.LineTo(c + new Point(-k, -k));
                g.EndFigure(true);
            }

            context.DrawGeometry(brush, null, geometry);
        }

        private IBrush? Brush(string key) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
    }
}
