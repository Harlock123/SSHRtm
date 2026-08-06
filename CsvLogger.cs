using System.Globalization;
using SSHRtm.Metrics;

namespace SSHRtm;

// CSV logger for a single server's detail-view stream.
//
// The set of columns depends on the machine (how many CPUs / NICs / disks /
// mounts it has), which we don't know until we see the first snapshot. So we
// discover columns from the first snapshot, write them as the header row,
// and freeze the schema. Later snapshots use the same column order; any keys
// that only appear later are dropped (rare — would mean a NIC/disk/mount
// showed up mid-session).
public sealed class CsvLogger : IDisposable
{
    private const int ProcSlots = 20;
    private static readonly string[] LoadNames = { "1m", "5m", "15m" };

    private readonly StreamWriter _writer;
    private List<string>? _columns;

    public string FilePath { get; }
    public string ServerName { get; }

    public CsvLogger(string directory, string serverName)
    {
        Directory.CreateDirectory(directory);
        ServerName = serverName;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        FilePath = Path.Combine(directory, $"{SafeFileName(serverName)}-{stamp}.csv");
        _writer = new StreamWriter(FilePath, append: false) { NewLine = "\n" };
    }

    public void Write(Snapshot snap)
    {
        var row = Flatten(snap);

        if (_columns is null)
        {
            _columns = row.Keys.ToList();
            _writer.WriteLine(string.Join(",", _columns.Select(EscapeCsv)));
        }

        var values = _columns.Select(c => row.TryGetValue(c, out var v) ? v : string.Empty);
        _writer.WriteLine(string.Join(",", values.Select(EscapeCsv)));
        _writer.Flush(); // keep flush on so `tail -f` / Excel-reopen shows fresh data
    }

    private static Dictionary<string, string> Flatten(Snapshot s)
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

        d["timestamp"] = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return d;
    }

    private static string Fmt(double v)
        => v.ToString("F2", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }

    public void Dispose()
    {
        try { _writer.Flush(); } catch { }
        _writer.Dispose();
    }
}
