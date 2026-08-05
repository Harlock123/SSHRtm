using Spectre.Console;

namespace SSHRtm.UI;

public static class Theme
{
    public static readonly Color Ok = Color.Green;
    public static readonly Color Warn = Color.Yellow;
    public static readonly Color Crit = Color.Red;
    public static readonly Color Dim = Color.Grey58;
    public static readonly Color Accent = Color.Cyan1;
    public static readonly Color Border = Color.Grey35;
    public static readonly Color Header = Color.White;
    public static readonly Color Selected = Color.DeepSkyBlue1;
    public static readonly Color Error = Color.Red3;

    public static Color UsageColor(double pct) =>
        pct >= 90 ? Crit : pct >= 70 ? Warn : Ok;

    public static string FormatBytes(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        < 1024L * 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N2} GB",
        _ => $"{bytes / (1024.0 * 1024 * 1024 * 1024):N2} TB",
    };

    public static string FormatRate(double bps) => $"{FormatBytes(bps)}/s";

    public static string FormatUptime(double seconds)
    {
        var t = TimeSpan.FromSeconds((int)seconds);
        if (t.Days > 0) return $"{t.Days}d {t.Hours:D2}h{t.Minutes:D2}m";
        return $"{t.Hours:D2}h{t.Minutes:D2}m{t.Seconds:D2}s";
    }

    public static string BarMarkup(double pct, int width = 20)
    {
        var color = UsageColor(pct);
        var filled = Math.Clamp((int)Math.Round(pct / 100.0 * width), 0, width);
        var bar = new string('█', filled) + new string('░', width - filled);
        return $"[{color}]{bar}[/] [bold]{pct,5:N1}%[/]";
    }

    // Bar without the trailing percentage text — for tight layouts (compact tiles).
    public static string MicroBar(double pct, int width = 6)
    {
        var color = UsageColor(pct);
        var filled = Math.Clamp((int)Math.Round(pct / 100.0 * width), 0, width);
        var bar = new string('█', filled) + new string('░', width - filled);
        return $"[{color}]{bar}[/]";
    }
}
