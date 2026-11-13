using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DesktopOverlay;

/// <summary>On/off switch in the terminal look: a framed slot with a square knob; on, the slot fills with the accent color.</summary>
public class ToggleSwitch : Border
{
    private static Color On => Theme.Accent;
    private static readonly Color Off = Color.FromArgb(0x00, 255, 255, 255);
    private static readonly Color KnobOff = Color.FromRgb(0x6A, 0x6A, 0x6A);
    private static readonly Color KnobOn = Color.FromRgb(0x0A, 0x0A, 0x0A);
    private readonly TranslateTransform _knob = new();
    private readonly SolidColorBrush _bg, _knobFill;
    private bool _isOn;

    public event Action<bool> Toggled;

    public ToggleSwitch(bool isOn)
    {
        _isOn = isOn;
        Width = 36; Height = 18; CornerRadius = new CornerRadius(2); Cursor = Cursors.Hand;
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
        Background = _bg = new SolidColorBrush(isOn ? On : Off);
        _knobFill = new SolidColorBrush(isOn ? KnobOn : KnobOff);
        _knob.X = isOn ? 20 : 2;
        Child = new Rectangle
        {
            Width = 12, Height = 12, Fill = _knobFill, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, RenderTransform = _knob,
        };
        MouseLeftButtonUp += (_, e) => { Set(!_isOn); Toggled?.Invoke(_isOn); e.Handled = true; };
    }

    public void Set(bool isOn)
    {
        _isOn = isOn;
        var time = Theme.Ms(160);
        _knob.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(isOn ? 20 : 2, time) { EasingFunction = Theme.Settle });
        _bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(isOn ? On : Off, time));
        _knobFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(isOn ? KnobOn : KnobOff, time));
    }
}
