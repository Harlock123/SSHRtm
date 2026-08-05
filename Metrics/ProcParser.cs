using System.Globalization;

namespace SSHRtm.Metrics;

public static class ProcParser
{
    public static Dictionary<string, string[]> SplitSections(string text)
    {
        // Delimiter '==SEP:NAME==' marks the END of section NAME (it follows the content).
        var sections = new Dictionary<string, string[]>();
        var buf = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("==SEP:", StringComparison.Ordinal) &&
                line.EndsWith("==", StringComparison.Ordinal))
            {
                var name = line.Substring(6, line.Length - 8);
                sections[name] = buf.ToArray();
                buf = new List<string>();
            }
            else
            {
                buf.Add(line);
            }
        }
        return sections;
    }

    public static Dictionary<string, CpuCounters> ParseStat(string[] lines)
    {
        var result = new Dictionary<string, CpuCounters>();
        foreach (var line in lines)
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            var name = parts[0];
            if (name.Length > 3 && !char.IsDigit(name[3])) continue;

            ulong total = 0, idle = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (!ulong.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) continue;
                total += v;
                if (i == 4 || i == 5) idle += v; // idle + iowait
            }
            result[name] = new CpuCounters(idle, total);
        }
        return result;
    }

    public static MemoryStats ParseMemInfo(string[] lines)
    {
        var m = new Dictionary<string, ulong>();
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line.Substring(0, colon);
            var rest = line.Substring(colon + 1).TrimStart();
            var space = rest.IndexOf(' ');
            var num = space > 0 ? rest.Substring(0, space) : rest;
            if (ulong.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                m[key] = v * 1024;
        }
        var total = m.GetValueOrDefault("MemTotal");
        var avail = m.TryGetValue("MemAvailable", out var a) ? a : m.GetValueOrDefault("MemFree");
        var used = total > avail ? total - avail : 0;
        var swapTotal = m.GetValueOrDefault("SwapTotal");
        var swapFree = m.GetValueOrDefault("SwapFree");
        var swapUsed = swapTotal > swapFree ? swapTotal - swapFree : 0;
        return new MemoryStats(
            total, avail, used,
            total > 0 ? 100.0 * used / total : 0,
            m.GetValueOrDefault("Cached"),
            m.GetValueOrDefault("Buffers"),
            swapTotal, swapUsed,
            swapTotal > 0 ? 100.0 * swapUsed / swapTotal : 0);
    }

    public static Dictionary<string, NetCounters> ParseNetDev(string[] lines)
    {
        var result = new Dictionary<string, NetCounters>();
        foreach (var line in lines.Skip(2))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var iface = line.Substring(0, colon).Trim();
            if (iface == "lo") continue;
            var fields = line.Substring(colon + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 16) continue;
            ulong Get(int i) => ulong.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            result[iface] = new NetCounters(
                Get(0), Get(1), Get(2), Get(3),
                Get(8), Get(9), Get(10), Get(11));
        }
        return result;
    }

    public static Dictionary<string, DiskCounters> ParseDiskStats(string[] lines)
    {
        var result = new Dictionary<string, DiskCounters>();
        foreach (var line in lines)
        {
            var t = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 14) continue;
            var name = t[2];
            if (!IsRealDevice(name)) continue;
            ulong Get(int i) => ulong.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            result[name] = new DiskCounters(
                Get(3), Get(5), Get(7), Get(9), Get(11), Get(12));
        }
        return result;
    }

    private static bool IsRealDevice(string name)
    {
        if (name.StartsWith("sd") && name.Length > 2 && char.IsLetter(name[2])) return true;
        if (name.StartsWith("nvme")) return true;
        if (name.StartsWith("vd") && name.Length > 2 && char.IsLetter(name[2])) return true;
        if (name.StartsWith("xvd") && name.Length > 3 && char.IsLetter(name[3])) return true;
        if (name.StartsWith("dm-")) return true;
        if (name.StartsWith("md") && name.Length > 2 && char.IsDigit(name[2])) return true;
        return false;
    }

    public static List<FilesystemInfo> ParseDf(string[] lines)
    {
        var result = new List<FilesystemInfo>();
        foreach (var line in lines.Skip(1))
        {
            var t = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 7) continue;
            var pct = t[5].TrimEnd('%');
            if (!int.TryParse(pct, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) continue;
            var mount = string.Join(' ', t.Skip(6));
            ulong Get(int i) => ulong.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            result.Add(new FilesystemInfo(t[0], t[1], Get(2), Get(3), Get(4), p, mount));
        }
        return result;
    }

    public static List<ProcessInfo> ParsePs(string[] lines)
    {
        var result = new List<ProcessInfo>();
        foreach (var line in lines)
        {
            var t = line.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            if (!int.TryParse(t[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) continue;
            if (!double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)) continue;
            if (!double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mem)) continue;
            result.Add(new ProcessInfo(pid, cpu, mem, t[3]));
        }
        return result;
    }

    public static Snapshot BuildSnapshot(string raw, RawCounters? prev, double deltaSeconds)
    {
        var s = SplitSections(raw);
        var cpu = ParseStat(s.GetValueOrDefault("STAT") ?? Array.Empty<string>());
        var mem = ParseMemInfo(s.GetValueOrDefault("MEMINFO") ?? Array.Empty<string>());
        var net = ParseNetDev(s.GetValueOrDefault("NETDEV") ?? Array.Empty<string>());
        var disk = ParseDiskStats(s.GetValueOrDefault("DISKSTATS") ?? Array.Empty<string>());
        var df = ParseDf(s.GetValueOrDefault("DF") ?? Array.Empty<string>());
        var ps = ParsePs(s.GetValueOrDefault("PS") ?? Array.Empty<string>());

        var nprocLines = s.GetValueOrDefault("NPROC") ?? Array.Empty<string>();
        var nproc = nprocLines.Length > 0 && int.TryParse(nprocLines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var np) ? np : 0;

        var hostLines = s.GetValueOrDefault("END") ?? Array.Empty<string>();
        var hostname = hostLines.Length > 0 ? hostLines[0].Trim() : "?";

        var loadLines = s.GetValueOrDefault("LOADAVG") ?? Array.Empty<string>();
        var loadAvg = loadLines.Length > 0
            ? loadLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3).ToArray()
            : new[] { "?", "?", "?" };

        var uptimeLines = s.GetValueOrDefault("UPTIME") ?? Array.Empty<string>();
        var uptime = 0.0;
        if (uptimeLines.Length > 0)
        {
            var first = uptimeLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (first.Length > 0)
                double.TryParse(first[0], NumberStyles.Float, CultureInfo.InvariantCulture, out uptime);
        }

        var cpuPct = Deltas.CpuUsage(prev?.Cpu, cpu);
        var netRate = Deltas.NetRate(prev?.Net, net, deltaSeconds);
        var diskRate = Deltas.DiskRate(prev?.Disk, disk, deltaSeconds);

        return new Snapshot(
            hostname, nproc, loadAvg, uptime,
            cpuPct, mem, netRate, diskRate, df, ps,
            new RawCounters(cpu, net, disk));
    }
}
