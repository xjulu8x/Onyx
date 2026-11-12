using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopOverlay;

/// <summary>
/// Press feedback for buttons, set from a style: <c>local:Feedback.Press="True"</c>. The control shrinks a little the
/// moment the mouse goes down (not on release, so it feels direct) and settles back without bouncing.
/// </summary>
public static class Feedback
{
    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Feedback), new PropertyMetadata(false, OnPressChanged));

    public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);
    public static void SetPress(DependencyObject d, bool value) => d.SetValue(PressProperty, value);

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.PreviewMouseLeftButtonDown += (_, _) => To(scale, 0.96, 90);
        element.PreviewMouseLeftButtonUp += (_, _) => To(scale, 1, 200);
        element.MouseLeave += (_, _) => To(scale, 1, 200);
    }

    private static void To(ScaleTransform scale, double value, double ms)
    {
        var animation = new DoubleAnimation(value, Theme.Ms(ms)) { EasingFunction = Theme.Settle };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
