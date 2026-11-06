using System.Globalization;
using SSHRtm.Metrics;

namespace SSHRtm;

// One snapshot as CSV cells, keyed by column name, in column order. Both the
// single-server logger and the combined logger write these same columns; the
// combined one prefixes each with the server's name.
public static class CsvColumns
{
    public const int ProcSlots = 20;
    private static readonly string[] LoadNames = { "1m", "5m", "15m" };

    /// <summary>Local time as the logs write it: YYYYMMDD-HHMMSS.</summary>
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>The combined log's timestamp, with milliseconds so ticks never collide.</summary>
    public const string PreciseTimestampFormat = "yyyyMMdd-HHmmss.fff";

    public static Dictionary<string, string> Flatten(Snapshot s, bool includeProcesses = true)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);

        d["hostname"]   = s.Hostname ?? string.Empty;
        d["num_cpu"]    = s.NumCpu.ToString(CultureInfo.InvariantCulture);
        d["uptime_sec"] = s.UptimeSec.ToString("F1", CultureInfo.InvariantCulture);

        for (int i = 0; i < LoadNames.Length; i++)
        {
            d[$"load_{LoadNames[i]}"] = i < s.LoadAvg.Length ? s.LoadAvg[i] : string.Empty;
        }

        d["cpu_overall_pct"] = Fmt(s.CpuPct.GetValueOrDefault("cpu"));

        var coreKeys = s.CpuPct.Keys
            .Where(k => k.StartsWith("cpu", StringComparison.Ordinal) && k != "cpu")
            .OrderBy(k => int.TryParse(k.AsSpan(3), out var n) ? n : int.MaxValue);
        foreach (var k in coreKeys)
        {
            d[$"{k}_pct"] = Fmt(s.CpuPct[k]);
        }

        var m = s.Memory;
        d["mem_total_bytes"]     = m.Total.ToString(CultureInfo.InvariantCulture);
        d["mem_used_bytes"]      = m.Used.ToString(CultureInfo.InvariantCulture);
        d["mem_available_bytes"] = m.Available.ToString(CultureInfo.InvariantCulture);
        d["mem_cached_bytes"]    = m.Cached.ToString(CultureInfo.InvariantCulture);
        d["mem_buffers_bytes"]   = m.Buffers.ToString(CultureInfo.InvariantCulture);
        d["mem_used_pct"]        = Fmt(m.UsedPct);
        d["swap_total_bytes"]    = m.SwapTotal.ToString(CultureInfo.InvariantCulture);
        d["swap_used_bytes"]     = m.SwapUsed.ToString(CultureInfo.InvariantCulture);
        d["swap_used_pct"]       = Fmt(m.SwapPct);

        foreach (var (iface, r) in s.NetRate.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            d[$"net_{iface}_rx_bps"] = Fmt(r.RxBps);
            d[$"net_{iface}_tx_bps"] = Fmt(r.TxBps);
            d[$"net_{iface}_rx_pps"] = Fmt(r.RxPps);
            d[$"net_{iface}_tx_pps"] = Fmt(r.TxPps);
        }

        foreach (var (dev, r) in s.DiskRate.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            d[$"disk_{dev}_read_bps"]  = Fmt(r.ReadBps);
            d[$"disk_{dev}_write_bps"] = Fmt(r.WriteBps);
            d[$"disk_{dev}_busy_pct"]  = Fmt(r.BusyPct);
            d[$"disk_{dev}_inflight"]  = r.InFlight.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var fs in s.Filesystems.OrderBy(f => f.Mount, StringComparer.Ordinal))
        {
            var mount = fs.Mount;
            d[$"fs_{mount}_type"]     = fs.Type ?? string.Empty;
            d[$"fs_{mount}_total_kb"] = fs.TotalKb.ToString(CultureInfo.InvariantCulture);
            d[$"fs_{mount}_used_kb"]  = fs.UsedKb.ToString(CultureInfo.InvariantCulture);
            d[$"fs_{mount}_avail_kb"] = fs.AvailKb.ToString(CultureInfo.InvariantCulture);
            d[$"fs_{mount}_used_pct"] = fs.UsedPct.ToString(CultureInfo.InvariantCulture);
        }

        if (includeProcesses)
        {
            // Fixed proc slots so the schema stays constant even though the top-N
            // list rotates: procN is whatever process is ranked N-th at that tick.
            for (int i = 0; i < ProcSlots; i++)
            {
                var p = i < s.TopProcs.Count ? s.TopProcs[i] : null;
                d[$"proc{i + 1}_pid"]     = p?.Pid.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                d[$"proc{i + 1}_cpu_pct"] = p is null ? string.Empty : Fmt(p.Cpu);
                d[$"proc{i + 1}_mem_pct"] = p is null ? string.Empty : Fmt(p.Mem);
                d[$"proc{i + 1}_command"] = p?.Command ?? string.Empty;
            }
        }

        return d;
    }

    public static string Fmt(double v)
        => v.ToString("F2", CultureInfo.InvariantCulture);

    public static string EscapeCsv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 || c is '\\' or '/' or ':' ? '_' : c).ToArray();
        return new string(chars);
    }
}
