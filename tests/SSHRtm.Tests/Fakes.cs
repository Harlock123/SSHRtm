using SSHRtm.Metrics;

namespace SSHRtm.Tests;

/// <summary>Made-up snapshots and states: no real hosts.</summary>
internal static class Fakes
{
    public static ServerState Ok(string name, Snapshot snap, DateTime atUtc) =>
        new(name, ServerStatus.Ok, null, snap, atUtc);

    public static Snapshot Snapshot(string host, int cores, string nic)
    {
        var cpu = new Dictionary<string, double> { ["cpu"] = 25 };
        for (int i = 0; i < cores; i++) cpu[$"cpu{i}"] = 10 + i;

        return new Snapshot(
            host,
            cores,
            new[] { "0.10", "0.20", "0.30" },
            12345.6,
            cpu,
            new MemoryStats(8_000_000_000, 6_000_000_000, 2_000_000_000, 25, 1_000_000_000, 100_000_000, 0, 0, 0),
            new Dictionary<string, NetRate> { [nic] = new(1000, 2000, 10, 20) },
            new Dictionary<string, DiskRate> { ["sda"] = new(4096, 8192, 3.5, 1) },
            new List<FilesystemInfo> { new("/dev/sda1", "xfs", 100_000, 40_000, 60_000, 40, "/") },
            new List<ProcessInfo> { new(1, 5, 0.5, "systemd"), new(200, 2.5, 1.5, "sshd") },
            new RawCounters(new(), new(), new()));
    }
}

/// <summary>A small CSV reader for checking what the loggers wrote (quoted fields included).</summary>
internal static class Csv
{
    public static List<List<string>> Read(string path)
    {
        var rows = new List<List<string>>();
        foreach (var line in File.ReadAllLines(path))
        {
            var row = new List<string>();
            var field = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
                else field.Append(c);
            }
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    public static Dictionary<string, string> Row(List<string> header, List<string> row) =>
        header.Zip(row).ToDictionary(p => p.First, p => p.Second);
}
