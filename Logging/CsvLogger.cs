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
    private readonly StreamWriter _writer;
    private List<string>? _columns;

    public string FilePath { get; }
    public string ServerName { get; }

    public CsvLogger(string directory, string serverName)
    {
        Directory.CreateDirectory(directory);
        ServerName = serverName;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        FilePath = Path.Combine(directory, $"{CsvColumns.SafeFileName(serverName)}-{stamp}.csv");
        _writer = new StreamWriter(FilePath, append: false) { NewLine = "\n" };
    }

    public void Write(Snapshot snap)
    {
        var row = CsvColumns.Flatten(snap);
        row["timestamp"] = DateTime.Now.ToString(CsvColumns.TimestampFormat, CultureInfo.InvariantCulture);

        if (_columns is null)
        {
            _columns = row.Keys.ToList();
            _writer.WriteLine(string.Join(",", _columns.Select(CsvColumns.EscapeCsv)));
        }

        var values = _columns.Select(c => row.TryGetValue(c, out var v) ? v : string.Empty);
        _writer.WriteLine(string.Join(",", values.Select(CsvColumns.EscapeCsv)));
        _writer.Flush(); // keep flush on so `tail -f` / Excel-reopen shows fresh data
    }

    public void Dispose()
    {
        try { _writer.Flush(); } catch { }
        _writer.Dispose();
    }
}
