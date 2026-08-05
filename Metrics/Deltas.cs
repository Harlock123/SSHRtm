namespace SSHRtm.Metrics;

public static class Deltas
{
    private const int SectorBytes = 512;

    private static double SafeDiff(ulong curr, ulong prev) =>
        curr >= prev ? (double)(curr - prev) : 0;

    public static Dictionary<string, double> CpuUsage(
        Dictionary<string, CpuCounters>? prev,
        Dictionary<string, CpuCounters> curr)
    {
        var result = new Dictionary<string, double>();
        foreach (var (k, c) in curr)
        {
            if (prev is not null && prev.TryGetValue(k, out var p))
            {
                var dt = SafeDiff(c.Total, p.Total);
                var di = SafeDiff(c.Idle, p.Idle);
                var pct = dt > 0 ? 100.0 * (dt - di) / dt : 0;
                result[k] = Math.Clamp(pct, 0, 100);
            }
            else
            {
                result[k] = 0;
            }
        }
        return result;
    }

    public static Dictionary<string, NetRate> NetRate(
        Dictionary<string, NetCounters>? prev,
        Dictionary<string, NetCounters> curr,
        double seconds)
    {
        var result = new Dictionary<string, NetRate>();
        if (seconds <= 0) return result;
        foreach (var (iface, c) in curr)
        {
            var p = prev is not null && prev.TryGetValue(iface, out var pp) ? pp : c;
            result[iface] = new NetRate(
                SafeDiff(c.RxBytes, p.RxBytes) / seconds,
                SafeDiff(c.TxBytes, p.TxBytes) / seconds,
                SafeDiff(c.RxPackets, p.RxPackets) / seconds,
                SafeDiff(c.TxPackets, p.TxPackets) / seconds);
        }
        return result;
    }

    public static Dictionary<string, DiskRate> DiskRate(
        Dictionary<string, DiskCounters>? prev,
        Dictionary<string, DiskCounters> curr,
        double seconds)
    {
        var result = new Dictionary<string, DiskRate>();
        if (seconds <= 0) return result;
        foreach (var (dev, c) in curr)
        {
            var p = prev is not null && prev.TryGetValue(dev, out var pp) ? pp : c;
            var rBps = SafeDiff(c.SectorsRead, p.SectorsRead) * SectorBytes / seconds;
            var wBps = SafeDiff(c.SectorsWritten, p.SectorsWritten) * SectorBytes / seconds;
            var busy = SafeDiff(c.IoTimeMs, p.IoTimeMs) / (seconds * 1000.0) * 100.0;
            result[dev] = new DiskRate(rBps, wBps, Math.Clamp(busy, 0, 100), c.IoInFlight);
        }
        return result;
    }
}
