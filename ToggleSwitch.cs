using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DesktopOverlay;

/// <summary>Small animated on/off pill switch in the overlay's style.</summary>
public class ToggleSwitch : Border
{
    private static Color On => Theme.Accent;
    private static readonly Color Off = Color.FromArgb(0x38, 255, 255, 255);
    private readonly TranslateTransform _knob = new();
    private readonly SolidColorBrush _bg;
    private bool _isOn;

    public event Action<bool> Toggled;

    public ToggleSwitch(bool isOn)
    {
        _isOn = isOn;
        Width = 36; Height = 20; CornerRadius = new CornerRadius(10); Cursor = Cursors.Hand;
        Background = _bg = new SolidColorBrush(isOn ? On : Off);
        _knob.X = isOn ? 19 : 3;
        Child = new Ellipse
        {
            Width = 14, Height = 14, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, RenderTransform = _knob,
        };
        MouseLeftButtonUp += (_, e) => { Set(!_isOn); Toggled?.Invoke(_isOn); e.Handled = true; };
    }

    public void Set(bool isOn)
    {
        _isOn = isOn;
        var ease = Theme.Settle;
        _knob.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(isOn ? 19 : 3, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        _bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(isOn ? On : Off, TimeSpan.FromMilliseconds(180)));
    }
}
