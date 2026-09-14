using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiscordVpn;

internal static class WindowTheme
{
    public static bool IsDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception) { return false; }
    }

    public static void Apply(Window window, bool dark)
    {
        var colors = new Dictionary<string, string> {
            ["PageBrush"] = dark ? "#202124" : "#F2F3F7",
            ["SurfaceBrush"] = dark ? "#2B2D31" : "#FFFFFF",
            ["InsetBrush"] = dark ? "#35373C" : "#F5F6FA",
            ["LineBrush"] = dark ? "#41434A" : "#E7E9EF",
            ["TextBrush"] = dark ? "#F2F3F5" : "#1C1C1E",
            ["MutedBrush"] = dark ? "#B5B7C0" : "#747780",
            ["AvatarBrush"] = dark ? "#3E4865" : "#E5EAF7",
            ["ButtonBrush"] = dark ? "#45474F" : "#EDF0F5",
            ["ButtonTextBrush"] = dark ? "#F2F3F5" : "#344054",
            ["LinkBrush"] = dark ? "#80B8FF" : "#007AFF"
        };
        foreach (var (key, color) in colors) {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze(); window.Resources[key] = brush;
        }
    }
}
