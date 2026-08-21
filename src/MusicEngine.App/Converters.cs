namespace MusicEngine.App;

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

/// <summary>Non-empty string → Visible (pass "inverse" for empty → Visible).</summary>
public sealed class StringToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is string s && s.Trim().Length > 0;
        var inverse = parameter as string == "inverse";
        return has != inverse ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// true → error red, false → subtle gray (MVVM-09). Returns frozen Brushes from
/// the theme resources instead of hex strings, so WPF never runs BrushConverter
/// per binding and the colours can't drift from the App.xaml palette.
/// </summary>
public sealed class BoolToRedConverter : IValueConverter
{
    private static readonly Brush ErrorBrush = Frozen("#FF6B6B");
    private static readonly Brush SubtleBrush = Frozen("#98A0B3");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Theme wins when the palette defines these (App.xaml) — fall back to the
        // frozen duplicates only if the resources are gone.
        var (key, fallback) = value is true ? ("DangerBrush", ErrorBrush) : ("SubtleTextBrush", SubtleBrush);
        return Application.Current?.TryFindResource(key) as Brush ?? fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → frozen SolidColorBrush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && hex.Length > 0 && ColorConverter.ConvertFromString(hex) is Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
