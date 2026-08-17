namespace MusicEngine.App;

using System.Windows.Media;

/// <summary>Accent color presets applied by swapping DynamicResource brushes.</summary>
public static class AccentTheme
{
    public static readonly (string Key, string Label, string Hex)[] Presets =
    {
        ("green", "Spotify green", "#1DB954"),
        ("violet", "Violet", "#8B5CF6"),
        ("blue", "Blue", "#3B82F6"),
        ("amber", "Amber", "#F59E0B"),
        ("rose", "Rose", "#F43F5E"),
    };

    public static void Apply(string key)
    {
        var hex = Presets.FirstOrDefault(p => p.Key == key).Hex is { Length: > 0 } h ? h : "#1DB954";
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var accent = new SolidColorBrush(color);
        accent.Freeze();
        var soft = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B));
        soft.Freeze();

        var res = System.Windows.Application.Current?.Resources;
        if (res is null) return;
        res["AccentBrush"] = accent;
        res["AccentSoftBrush"] = soft;
    }
}
