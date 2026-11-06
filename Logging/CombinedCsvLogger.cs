using System.Globalization;

namespace SSHRtm;

// CSV logger for several servers at once: one row per tick, every server's
// columns side by side.
//
// Layout: `timestamp` first (local time, with milliseconds), then for each
// server `<name>::status` and `<name>::sample_age_s`, then that server's
// snapshot columns (the same ones CsvLogger writes) prefixed `<name>::`.
//
// A server's snapshot columns depend on its machine, so the header waits until
// every server has reported once, or until HeaderWait has passed; rows from
// that wait are kept and written under the header. A server that first reports
// after the header is written gets its columns added at the end, and the file
// is rewritten once with those cells empty in the earlier rows.
//
// A server's cells are filled while its latest snapshot is fresh (status Ok and
// no older than StaleAfter); a server that is down, reconnecting or stalled has
// them empty, so a chart shows the gap.
public sealed class CombinedCsvLogger : IDisposable
{
    public const string Separator = "::";
    public const string StatusColumn = "status";
    public const string AgeColumn = "sample_age_s";

    private readonly object _gate = new();
    private readonly CombinedLogOptions _options;
    private readonly Dictionary<string, List<string>?> _serverColumns;
    private readonly List<Dictionary<string, string>> _pending = new();
    private StreamWriter _writer;
    private List<string>? _columns;
    private DateTime? _startedUtc;
    private bool _disposed;

    public string FilePath { get; }
    public IReadOnlyList<string> Servers { get; }
    public int RowsWritten { get; private set; }

    public CombinedCsvLogger(string filePath, IReadOnlyList<string> servers, CombinedLogOptions? options = null)
    {
        if (servers.Count == 0) throw new ArgumentException("Choose at least one server to log.", nameof(servers));
        FilePath = filePath;
        Servers = servers.Distinct(StringComparer.Ordinal).ToList();
        _options = options ?? new CombinedLogOptions();
        _serverColumns = Servers.ToDictionary(s => s, _ => (List<string>?)null, StringComparer.Ordinal);
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (dir is not null) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(FilePath, append: false) { NewLine = "\n" };
    }

    /// <summary>
    /// The file name for a combined log started at <paramref name="now"/>: the server names joined with
    /// "+" for up to three, else "group-Nservers".
    /// </summary>
    public static string DefaultFileName(IReadOnlyList<string> servers, DateTime now)
    {
        var stamp = now.ToString(CsvColumns.TimestampFormat, CultureInfo.InvariantCulture);
        var label = servers.Count <= 3
            ? string.Join("+", servers.Select(CsvColumns.SafeFileName))
            : $"group-{servers.Count}servers";
        return $"{label}-{stamp}.csv";
    }

    /// <summary>Writes one tick: each server's state as <paramref name="stateOf"/> gives it at <paramref name="nowUtc"/>.</summary>
    public void Write(DateTime nowUtc, Func<string, ServerState> stateOf)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _startedUtc ??= nowUtc;

            var row = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["timestamp"] = nowUtc.ToLocalTime().ToString(CsvColumns.PreciseTimestampFormat, CultureInfo.InvariantCulture),
            };
            var newServers = new List<string>();

            foreach (var server in Servers)
            {
                var st = stateOf(server);
                row[Column(server, StatusColumn)] = st.Status.ToString();

                var age = (nowUtc - st.UpdatedAt).TotalSeconds;
                if (st.Status != ServerStatus.Ok || st.Snapshot is null || age > _options.StaleAfter.TotalSeconds)
                {
                    continue;
                }

                row[Column(server, AgeColumn)] = Math.Max(0, age).ToString("F2", CultureInfo.InvariantCulture);
                var cells = CsvColumns.Flatten(st.Snapshot, _options.IncludeProcesses);
                foreach (var (key, value) in cells)
                {
                    row[Column(server, key)] = value;
                }

                if (_serverColumns[server] is null)
                {
                    _serverColumns[server] = cells.Keys.ToList();
                    newServers.Add(server);
                }
            }

            if (_columns is null)
            {
                _pending.Add(row);
                var everyoneReported = _serverColumns.Values.All(c => c is not null);
                if (everyoneReported || nowUtc - _startedUtc.Value >= _options.HeaderWait)
                {
                    WriteHeaderAndPending();
                }
                return;
            }

            if (newServers.Count > 0)
            {
                AddLateColumns(newServers);
            }

            WriteRow(row);
            _writer.Flush(); // keep flush on so `tail -f` / Excel-reopen shows fresh data
        }
    }

    public static string Column(string server, string key) => server + Separator + key;

    private void WriteHeaderAndPending()
    {
        _columns = new List<string> { "timestamp" };
        foreach (var server in Servers)
        {
            _columns.Add(Column(server, StatusColumn));
            _columns.Add(Column(server, AgeColumn));
            foreach (var key in _serverColumns[server] ?? Enumerable.Empty<string>())
            {
                _columns.Add(Column(server, key));
            }
        }

        _writer.WriteLine(string.Join(",", _columns.Select(CsvColumns.EscapeCsv)));
        foreach (var row in _pending) WriteRow(row);
        _pending.Clear();
        _writer.Flush();
    }

    // A server whose first snapshot came after the header: its columns go on
    // the end, and the rows already written get that many empty cells.
    private void AddLateColumns(List<string> servers)
    {
        var added = servers.SelectMany(s => _serverColumns[s]!.Select(k => Column(s, k))).ToList();
        _writer.Flush();
        _writer.Dispose();

        var temp = FilePath + ".tmp";
        var padding = new string(',', added.Count);
        using (var reader = new StreamReader(FilePath))
        using (var output = new StreamWriter(temp, append: false) { NewLine = "\n" })
        {
            var header = reader.ReadLine();
            output.WriteLine(header + "," + string.Join(",", added.Select(CsvColumns.EscapeCsv)));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                output.WriteLine(line + padding);
            }
        }

        File.Move(temp, FilePath, overwrite: true);
        _columns!.AddRange(added);
        _writer = new StreamWriter(FilePath, append: true) { NewLine = "\n" };
    }

    private void WriteRow(Dictionary<string, string> row)
    {
        var values = _columns!.Select(c => row.TryGetValue(c, out var v) ? v : string.Empty);
        _writer.WriteLine(string.Join(",", values.Select(CsvColumns.EscapeCsv)));
        RowsWritten++;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                // Stopped before every server reported: write what there is.
                if (_columns is null && _pending.Count > 0) WriteHeaderAndPending();
                _writer.Flush();
            }
            catch { }
            _writer.Dispose();
        }
    }
}

public sealed record CombinedLogOptions
{
    /// <summary>Write the 20 top-process slots for each server (80 columns a server).</summary>
    public bool IncludeProcesses { get; init; } = true;

    /// <summary>How long the header waits for every server's first snapshot.</summary>
    public TimeSpan HeaderWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A snapshot older than this counts as missing: its server's cells are left empty.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The options for a poll interval: stale after three missed polls, and never under five seconds.</summary>
    public static CombinedLogOptions For(AppConfig config) => new()
    {
        IncludeProcesses = config.CombinedLogProcesses,
        StaleAfter = TimeSpan.FromMilliseconds(Math.Max(5000, config.PollIntervalMs * 3)),
    };
}
